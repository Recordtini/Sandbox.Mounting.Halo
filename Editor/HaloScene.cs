using Sandbox;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace Sandbox.Mounting.Halo;

public class HaloScene : ResourceLoader<HaloMount>
{
	public HaloMap Map { get; set; }
	public TagItem Tag { get; set; }

	public HaloScene( HaloMap map, TagItem tag )
	{
		Map = map;
		Tag = tag;
	}

	protected override object Load()
	{
		using var stream = File.OpenRead( Map.FilePath );
		using var reader = new BinaryReader( stream );

		// Seek to Scenario Tag Data
		var scnrOffset = Map.GetFileOffset( (uint)Tag.DataOffset );
		stream.Seek( scnrOffset, SeekOrigin.Begin );

		var scenario = new ScenarioTag( reader );

		// Create Scene
		var scene = new Scene();
		var world = scene.CreateObject();
		world.Name = "Map World";

		// Load BSPs (Level Geometry)
		if ( scenario.StructureBSPs.Count > 0 )
		{
			var bspsOffset = Map.GetFileOffset( scenario.StructureBSPs.Pointer );
			
			for ( int i = 0; i < scenario.StructureBSPs.Count; i++ )
			{
				stream.Seek( bspsOffset + (i * 32), SeekOrigin.Begin ); // 32 is StructureBSP size
				var bspRef = new ScenarioStructureBSP( reader );

				var bspTagItem = Map.Tags.FirstOrDefault( t => t.Id == bspRef.BSP.Id );
				if ( bspTagItem.Id != 0 )
				{
					LoadBSP( scene, world, bspTagItem, stream, reader );
				}
			}
		}

		// Load Entities
		LoadEntities( scene, world, scenario, stream, reader );

		return scene;
	}

	private void LoadBSP( Scene scene, GameObject parent, TagItem bspTagItem, Stream stream, BinaryReader reader )
	{
		var bspOffset = Map.GetFileOffset( (uint)bspTagItem.DataOffset );
		stream.Seek( bspOffset, SeekOrigin.Begin );

		var bsp = new BspTag( reader );

		if ( bsp.Materials.Count > 0 )
		{
			var materialsOffset = Map.GetFileOffset( bsp.Materials.Pointer );
			var modelBuilder = new ModelBuilder();
			bool hasGeometry = false;

			for ( int m = 0; m < bsp.Materials.Count; m++ )
			{
				stream.Seek( materialsOffset + (m * 256), SeekOrigin.Begin );
				var material = new ScenarioStructureBSPMaterial( reader );

				if ( material.RenderedVerticesCount > 0 && material.SurfaceCount > 0 )
				{
					long verticesFileOffset = 0;
					if ( material.RenderedVerticesOffset > 0x40000000 )
					{
						verticesFileOffset = Map.GetFileOffset( material.RenderedVerticesOffset );
					}
					else
					{
						verticesFileOffset = bspOffset + material.RenderedVerticesOffset;
					}

					stream.Seek( verticesFileOffset, SeekOrigin.Begin );
					var vertices = new List<BspVertex>();
					for ( int v = 0; v < material.RenderedVerticesCount; v++ )
					{
						vertices.Add( new BspVertex( reader ) );
					}

					var surfacesBlockOffset = Map.GetFileOffset( bsp.Surfaces.Pointer );
					var firstSurfaceOffset = surfacesBlockOffset + (material.SurfacesIndex * 6);
					
					stream.Seek( firstSurfaceOffset, SeekOrigin.Begin );
					var indices = new List<int>();
					for ( int s = 0; s < material.SurfaceCount; s++ )
					{
						indices.Add( reader.ReadUInt16() );
						indices.Add( reader.ReadUInt16() );
						indices.Add( reader.ReadUInt16() );
					}

					if ( vertices.Count > 0 && indices.Count > 0 )
					{
						// Resolve Material
						Material sceneMaterial = Material.Load( "materials/dev/white.vmat" );
						
						// The material struct has a Shader TagDependency
						var shaderTagItem = Map.Tags.FirstOrDefault( t => t.Id == material.Shader.Id );
						if ( shaderTagItem.Id != 0 )
						{
							var loadedMat = new HaloMaterial( Map, shaderTagItem ).Load();
							if ( loadedMat != null )
								sceneMaterial = loadedMat;
						}

						var mesh = new Mesh( sceneMaterial );
						
						var vertexBuffer = new Vertex[vertices.Count];
						for ( int v = 0; v < vertices.Count; v++ )
						{
							var hv = vertices[v];
							vertexBuffer[v] = new Vertex( 
								new Vector3( hv.Position.x, hv.Position.y, hv.Position.z ),
								new Vector3( hv.Normal.x, hv.Normal.y, hv.Normal.z ),
								new Vector3( hv.Tangent.x, hv.Tangent.y, hv.Tangent.z ),
								new Vector2( hv.TexCoord.x, hv.TexCoord.y )
							);
						}

						mesh.CreateVertexBuffer( vertices.Count, Vertex.Layout, vertexBuffer );
						mesh.CreateIndexBuffer( indices.Count, indices.ToArray() );
						
						modelBuilder.AddMesh( mesh );
						hasGeometry = true;
					}
				}
			}

			if ( hasGeometry )
			{
				var model = modelBuilder.Create();
				var bspObject = scene.CreateObject();
				bspObject.Parent = parent;
				bspObject.Name = $"BSP {bspTagItem.Id}";
				var renderer = bspObject.Components.Create<ModelRenderer>();
				renderer.Model = model;
			}
		}
	}

	private void LoadEntities( Scene scene, GameObject parent, ScenarioTag scenario, Stream stream, BinaryReader reader )
	{
		// Load Scenery
		LoadObjectBlock<ScenarioScenery, ScenarioSceneryPalette>( scene, parent, scenario.Scenery, scenario.SceneryPalette, stream, reader, "Scenery" );
		
		// Load Vehicles
		LoadObjectBlock<ScenarioVehicle, ScenarioVehiclePalette>( scene, parent, scenario.Vehicles, scenario.VehiclePalette, stream, reader, "Vehicles" );
		
		// Load Weapons
		LoadObjectBlock<ScenarioWeapon, ScenarioWeaponPalette>( scene, parent, scenario.Weapons, scenario.WeaponPalette, stream, reader, "Weapons" );

		// Load Machines (Doors, etc.)
		LoadObjectBlock<ScenarioMachine, ScenarioMachinePalette>( scene, parent, scenario.Machines, scenario.MachinePalette, stream, reader, "Machines" );

		// Load Controls (Switches, etc.)
		LoadObjectBlock<ScenarioControl, ScenarioControlPalette>( scene, parent, scenario.Controls, scenario.ControlPalette, stream, reader, "Controls" );

		// Load Player Starting Locations
		LoadPlayerStarts( scene, parent, scenario.PlayerStartingLocations, stream, reader );
	}

	private void LoadPlayerStarts( Scene scene, GameObject parent, TagBlock playerStarts, Stream stream, BinaryReader reader )
	{
		if ( playerStarts.Count == 0 ) return;

		var startsOffset = Map.GetFileOffset( playerStarts.Pointer );
		stream.Seek( startsOffset, SeekOrigin.Begin );

		var groupObj = scene.CreateObject();
		groupObj.Parent = parent;
		groupObj.Name = "Player Starts";

		for ( int i = 0; i < playerStarts.Count; i++ )
		{
			stream.Seek( startsOffset + (i * 52), SeekOrigin.Begin ); // 52 is ScenarioPlayerStartingLocation size
			var start = new ScenarioPlayerStartingLocation( reader );

			var go = new GameObject();
			go.Parent = groupObj;
			go.Name = $"Player Start {i}";
			
			// Set Transform
			go.Transform.LocalPosition = new Vector3( start.Position.x, start.Position.y, start.Position.z );
			go.Transform.LocalRotation = Rotation.From( 0, start.Facing * 57.2958f, 0 ); // Facing is likely Yaw in radians

			// Add SpawnPoint Component (if available, otherwise just a marker)
			// go.Components.Create<SpawnPoint>(); 
			// For now, let's assume the game logic looks for objects with "spawnpoint" tag or similar.
			go.Tags.Add( "spawnpoint" );
		}
	}

	private void LoadObjectBlock<TObject, TPalette>( Scene scene, GameObject parent, TagBlock objects, TagBlock palette, Stream stream, BinaryReader reader, string groupName ) 
		where TObject : struct, IScenarioObject 
		where TPalette : struct, IScenarioPalette
	{
		if ( objects.Count == 0 ) return;

		var paletteList = new List<TPalette>();
		if ( palette.Count > 0 )
		{
			var paletteOffset = Map.GetFileOffset( palette.Pointer );
			stream.Seek( paletteOffset, SeekOrigin.Begin );
			for ( int i = 0; i < palette.Count; i++ )
			{
				paletteList.Add( (TPalette)new TPalette().Read( reader ) );
			}
		}

		var objectsOffset = Map.GetFileOffset( objects.Pointer );
		stream.Seek( objectsOffset, SeekOrigin.Begin );

		var groupObj = scene.CreateObject();
		groupObj.Parent = parent;
		groupObj.Name = groupName;

		for ( int i = 0; i < objects.Count; i++ )
		{
			// Re-seek for each object because we might have jumped around (though we shouldn't have in this loop)
			stream.Seek( objectsOffset + (i * new TObject().Size), SeekOrigin.Begin );
			var obj = new TObject().Read( reader );

			if ( obj.PaletteIndex >= 0 && obj.PaletteIndex < paletteList.Count )
			{
				var paletteEntry = paletteList[obj.PaletteIndex];
				SpawnEntity( groupObj, obj, paletteEntry );
			}
		}
	}

	private void SpawnEntity( GameObject parent, IScenarioObject obj, IScenarioPalette paletteEntry )
	{
		var go = new GameObject();
		go.Parent = parent;
		go.Name = paletteEntry.Name.Id.ToString(); // Placeholder name
		
		// Set Transform
		go.Transform.LocalPosition = new Vector3( obj.Position.x, obj.Position.y, obj.Position.z );
		go.Transform.LocalRotation = Rotation.From( obj.Rotation.x * 57.2958f, obj.Rotation.y * 57.2958f, obj.Rotation.z * 57.2958f ); // Radians to Degrees? Halo uses Euler angles in radians usually.

		// Resolve Model
		var modelTagItem = Map.Tags.FirstOrDefault( t => t.Id == paletteEntry.Name.Id );
		if ( modelTagItem.Id != 0 )
		{
			var tagName = Map.GetString( modelTagItem.StringOffset );
			go.Name = System.IO.Path.GetFileName( tagName );

			// Add ModelRenderer
			var renderer = go.Components.Create<ModelRenderer>();
			renderer.Model = Model.Load( $"halo1/{Map.Name}/{tagName}.vmdl" );
		}
	}
}

// Interfaces for generic loading
public interface IScenarioObject
{
	int Size { get; }
	short PaletteIndex { get; }
	Vector3 Position { get; }
	Vector3 Rotation { get; }
	IScenarioObject Read( BinaryReader br );
}

public interface IScenarioPalette
{
	TagDependency Name { get; }
	IScenarioPalette Read( BinaryReader br );
}

// Structs

public struct TagBlock
{
	public int Count;
	public uint Pointer;
	public int Pad;

	public TagBlock( BinaryReader br )
	{
		Count = br.ReadInt32();
		Pointer = br.ReadUInt32();
		Pad = br.ReadInt32();
	}
}

public struct ScenarioTag
{
	public short Unused;
	public short Type;
	public short Flags;
	public TagBlock ChildScenarios;
	public float LocalNorth;
	public TagBlock PredictedResources;
	public TagBlock Functions;
	public TagBlock EditorComments;
	public TagBlock ObjectNames;
	public TagBlock Scenery;
	public TagBlock SceneryPalette;
	public TagBlock Bipeds;
	public TagBlock BipedPalette;
	public TagBlock Vehicles;
	public TagBlock VehiclePalette;
	public TagBlock Equipment;
	public TagBlock EquipmentPalette;
	public TagBlock Weapons;
	public TagBlock WeaponPalette;
	public TagBlock DeviceGroups;
	public TagBlock Machines;
	public TagBlock MachinePalette;
	public TagBlock Controls;
	public TagBlock ControlPalette;
	public TagBlock LightFixtures;
	public TagBlock LightFixturePalette;
	public TagBlock SoundScenery;
	public TagBlock SoundSceneryPalette;
	public TagBlock PlayerStartingProfiles;
	public TagBlock PlayerStartingLocations;
	public TagBlock TriggerVolumes;
	public TagBlock RecordedAnimations;
	public TagBlock NetgameFlags;
	public TagBlock NetgameEquipment;
	public TagBlock StartingEquipment;
	public TagBlock BSPSwitchTriggerVolumes;
	public TagBlock Decals;
	public TagBlock DecalPalette;
	public TagBlock DetailObjectCollectionPalette;
	public TagBlock StylePalette;
	public TagBlock SquadGroups;
	public TagBlock Squads;
	public TagBlock Zones;
	public TagBlock MissionScenes;
	public TagBlock CutsceneCameraPoints;
	public TagBlock CutsceneTitles;
	public TagBlock CustomObjectNames;
	public TagBlock ChapterTitles;
	public TagBlock HUDMessages;
	public TagBlock StructureBSPs;

	public ScenarioTag( BinaryReader br )
	{
		Unused = br.ReadInt16();
		Type = br.ReadInt16();
		Flags = br.ReadInt16();
		ChildScenarios = new TagBlock( br );
		LocalNorth = br.ReadSingle();
		PredictedResources = new TagBlock( br );
		Functions = new TagBlock( br );
		EditorComments = new TagBlock( br );
		ObjectNames = new TagBlock( br );
		Scenery = new TagBlock( br );
		SceneryPalette = new TagBlock( br );
		Bipeds = new TagBlock( br );
		BipedPalette = new TagBlock( br );
		Vehicles = new TagBlock( br );
		VehiclePalette = new TagBlock( br );
		Equipment = new TagBlock( br );
		EquipmentPalette = new TagBlock( br );
		Weapons = new TagBlock( br );
		WeaponPalette = new TagBlock( br );
		DeviceGroups = new TagBlock( br );
		Machines = new TagBlock( br );
		MachinePalette = new TagBlock( br );
		Controls = new TagBlock( br );
		ControlPalette = new TagBlock( br );
		LightFixtures = new TagBlock( br );
		LightFixturePalette = new TagBlock( br );
		SoundScenery = new TagBlock( br );
		SoundSceneryPalette = new TagBlock( br );
		PlayerStartingProfiles = new TagBlock( br );
		PlayerStartingLocations = new TagBlock( br );
		TriggerVolumes = new TagBlock( br );
		RecordedAnimations = new TagBlock( br );
		NetgameFlags = new TagBlock( br );
		NetgameEquipment = new TagBlock( br );
		StartingEquipment = new TagBlock( br );
		BSPSwitchTriggerVolumes = new TagBlock( br );
		Decals = new TagBlock( br );
		DecalPalette = new TagBlock( br );
		DetailObjectCollectionPalette = new TagBlock( br );
		StylePalette = new TagBlock( br );
		SquadGroups = new TagBlock( br );
		Squads = new TagBlock( br );
		Zones = new TagBlock( br );
		MissionScenes = new TagBlock( br );
		CutsceneCameraPoints = new TagBlock( br );
		CutsceneTitles = new TagBlock( br );
		CustomObjectNames = new TagBlock( br );
		ChapterTitles = new TagBlock( br );
		HUDMessages = new TagBlock( br );
		StructureBSPs = new TagBlock( br );
	}
}

public struct ScenarioStructureBSP
{
	public int StructureBSPPointer;
	public int Size;
	public uint Magic;
	public int Zero;
	public TagDependency BSP;

	public ScenarioStructureBSP( BinaryReader br )
	{
		br.ReadInt32();
		br.ReadInt32();
		br.ReadInt32();
		br.ReadInt32();
		BSP = new TagDependency( br );
	}
}

public struct TagDependency
{
	public int Class;
	public int NamePointer;
	public int Reserved;
	public uint Id;

	public TagDependency( BinaryReader br )
	{
		Class = br.ReadInt32();
		NamePointer = br.ReadInt32();
		Reserved = br.ReadInt32();
		Id = br.ReadUInt32();
	}
}

public struct BspTag
{
	public TagBlock LightmapBitmaps;
	public int VehicleFloor;
	public int VehicleCeiling;
	public TagBlock Materials;
	public TagBlock Clusters;
	public TagBlock LensFlares;
	public TagBlock LensFlareMarkers;
	public TagBlock Surfaces;

	public BspTag( BinaryReader br )
	{
		LightmapBitmaps = new TagBlock( br );
		VehicleFloor = br.ReadInt32();
		VehicleCeiling = br.ReadInt32();
		br.ReadBytes( 20 ); // Pad
		Materials = new TagBlock( br );
		Clusters = new TagBlock( br );
		LensFlares = new TagBlock( br );
		LensFlareMarkers = new TagBlock( br );
		Surfaces = new TagBlock( br );
	}
}

public struct ScenarioStructureBSPMaterial
{
	public TagDependency Shader;
	public short ShaderPermutation;
	public short Flags;
	public int SurfacesIndex;
	public int SurfaceCount;
	public short RenderedVerticesType;
	public int RenderedVerticesCount;
	public uint RenderedVerticesOffset;

	public ScenarioStructureBSPMaterial( BinaryReader br )
	{
		Shader = new TagDependency( br );
		ShaderPermutation = br.ReadInt16();
		Flags = br.ReadInt16();
		SurfacesIndex = br.ReadInt32();
		SurfaceCount = br.ReadInt32();
		
		br.BaseStream.Seek( 12 + 12 + 2 + 2 + 12 + 12 + 12 + 12 + 12 + 16 + 12 + 12 + 16 + 4 + 2, SeekOrigin.Current );
		
		RenderedVerticesType = br.ReadInt16();
		br.ReadInt16(); // Pad2
		RenderedVerticesCount = br.ReadInt32();
		RenderedVerticesOffset = br.ReadUInt32();
	}
}

public struct BspVertex
{
	public Vector3 Position;
	public Vector3 Normal;
	public Vector3 Binormal;
	public Vector3 Tangent;
	public Vector2 TexCoord;

	public BspVertex( BinaryReader br )
	{
		Position = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		Normal = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		Binormal = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		Tangent = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		TexCoord = new Vector2( br.ReadSingle(), br.ReadSingle() );
	}
}

// Entity Structs

public struct ScenarioScenery : IScenarioObject
{
	public short PaletteIndex;
	public short NameIndex;
	public short NotPlaced;
	public short DesiredPermutation;
	public Vector3 Position;
	public Vector3 Rotation;
	// ...

	public int Size => 72;
	short IScenarioObject.PaletteIndex => PaletteIndex;
	Vector3 IScenarioObject.Position => Position;
	Vector3 IScenarioObject.Rotation => Rotation;

	public IScenarioObject Read( BinaryReader br )
	{
		PaletteIndex = br.ReadInt16();
		NameIndex = br.ReadInt16();
		NotPlaced = br.ReadInt16();
		DesiredPermutation = br.ReadInt16();
		Position = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		Rotation = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		
		br.ReadBytes( 72 - 32 ); // Skip rest
		return this;
	}
}

public struct ScenarioSceneryPalette : IScenarioPalette
{
	public TagDependency Name;

	TagDependency IScenarioPalette.Name => Name;

	public IScenarioPalette Read( BinaryReader br )
	{
		Name = new TagDependency( br );
		br.ReadBytes( 32 ); // Pad
		return this;
	}
}

public struct ScenarioVehicle : IScenarioObject
{
	public short PaletteIndex;
	public short NameIndex;
	public short NotPlaced;
	public short DesiredPermutation;
	public Vector3 Position;
	public Vector3 Rotation;
	// ...

	public int Size => 120;
	short IScenarioObject.PaletteIndex => PaletteIndex;
	Vector3 IScenarioObject.Position => Position;
	Vector3 IScenarioObject.Rotation => Rotation;

	public IScenarioObject Read( BinaryReader br )
	{
		PaletteIndex = br.ReadInt16();
		NameIndex = br.ReadInt16();
		NotPlaced = br.ReadInt16();
		DesiredPermutation = br.ReadInt16();
		Position = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		Rotation = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		
		br.ReadBytes( 120 - 32 ); // Skip rest
		return this;
	}
}

public struct ScenarioVehiclePalette : IScenarioPalette
{
	public TagDependency Name;

	TagDependency IScenarioPalette.Name => Name;

	public IScenarioPalette Read( BinaryReader br )
	{
		Name = new TagDependency( br );
		br.ReadBytes( 32 ); // Pad
		return this;
	}
}

public struct ScenarioWeapon : IScenarioObject
{
	public short PaletteIndex;
	public short NameIndex;
	public short NotPlaced;
	public short DesiredPermutation;
	public Vector3 Position;
	public Vector3 Rotation;
	// ...

	public int Size => 92;
	short IScenarioObject.PaletteIndex => PaletteIndex;
	Vector3 IScenarioObject.Position => Position;
	Vector3 IScenarioObject.Rotation => Rotation;

	public IScenarioObject Read( BinaryReader br )
	{
		PaletteIndex = br.ReadInt16();
		NameIndex = br.ReadInt16();
		NotPlaced = br.ReadInt16();
		DesiredPermutation = br.ReadInt16();
		Position = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		Rotation = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		
		br.ReadBytes( 92 - 32 ); // Skip rest
		return this;
	}
}

public struct ScenarioWeaponPalette : IScenarioPalette
{
	public TagDependency Name;

	TagDependency IScenarioPalette.Name => Name;

	public IScenarioPalette Read( BinaryReader br )
	{
		Name = new TagDependency( br );
		br.ReadBytes( 32 ); // Pad
		return this;
	}
}

public struct ScenarioPlayerStartingLocation
{
	public Vector3 Position;
	public float Facing;
	public short TeamIndex;
	public short BspIndex;
	public short Type0;
	public short Type1;
	public short Type2;
	public short Type3;

	public ScenarioPlayerStartingLocation( BinaryReader br )
	{
		Position = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		Facing = br.ReadSingle();
		TeamIndex = br.ReadInt16();
		BspIndex = br.ReadInt16();
		Type0 = br.ReadInt16();
		Type1 = br.ReadInt16();
		Type2 = br.ReadInt16();
		Type3 = br.ReadInt16();
		
		br.ReadBytes( 24 ); // Pad
	}
}

public struct ScenarioMachine : IScenarioObject
{
	public short PaletteIndex;
	public short NameIndex;
	public short NotPlaced;
	public short DesiredPermutation;
	public Vector3 Position;
	public Vector3 Rotation;
	// ...

	public int Size => 64;
	short IScenarioObject.PaletteIndex => PaletteIndex;
	Vector3 IScenarioObject.Position => Position;
	Vector3 IScenarioObject.Rotation => Rotation;

	public IScenarioObject Read( BinaryReader br )
	{
		PaletteIndex = br.ReadInt16();
		NameIndex = br.ReadInt16();
		NotPlaced = br.ReadInt16();
		DesiredPermutation = br.ReadInt16();
		Position = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		Rotation = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		
		br.ReadBytes( 64 - 32 ); // Skip rest
		return this;
	}
}

public struct ScenarioMachinePalette : IScenarioPalette
{
	public TagDependency Name;

	TagDependency IScenarioPalette.Name => Name;

	public IScenarioPalette Read( BinaryReader br )
	{
		Name = new TagDependency( br );
		br.ReadBytes( 32 ); // Pad
		return this;
	}
}

public struct ScenarioControl : IScenarioObject
{
	public short PaletteIndex;
	public short NameIndex;
	public short NotPlaced;
	public short DesiredPermutation;
	public Vector3 Position;
	public Vector3 Rotation;
	// ...

	public int Size => 64;
	short IScenarioObject.PaletteIndex => PaletteIndex;
	Vector3 IScenarioObject.Position => Position;
	Vector3 IScenarioObject.Rotation => Rotation;

	public IScenarioObject Read( BinaryReader br )
	{
		PaletteIndex = br.ReadInt16();
		NameIndex = br.ReadInt16();
		NotPlaced = br.ReadInt16();
		DesiredPermutation = br.ReadInt16();
		Position = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		Rotation = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		
		br.ReadBytes( 64 - 32 ); // Skip rest
		return this;
	}
}

public struct ScenarioControlPalette : IScenarioPalette
{
	public TagDependency Name;

	TagDependency IScenarioPalette.Name => Name;

	public IScenarioPalette Read( BinaryReader br )
	{
		Name = new TagDependency( br );
		br.ReadBytes( 32 ); // Pad
		return this;
	}
}
