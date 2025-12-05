using Sandbox;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Sandbox.Mounting.Halo;

public class HaloModel : ResourceLoader<HaloMount>
{
	public HaloMap Map { get; set; }
	public TagItem Tag { get; set; }

	public HaloModel( HaloMap map, TagItem tag )
	{
		Map = map;
		Tag = tag;
	}

	protected override object Load()
	{
		try
		{
			using var stream = File.OpenRead( Map.FilePath );
			using var reader = new BinaryReader( stream );

			// Seek to Tag Data
			var tagDataOffset = Map.GetFileOffset( (uint)Tag.DataOffset );
			stream.Seek( tagDataOffset, SeekOrigin.Begin );
			
			// Debug: Dump first 260 bytes of model tag (geometry block may be at offset 240)
			var debugBuffer = reader.ReadBytes( 260 );
			var hexDump = "";
			for ( int i = 0; i < debugBuffer.Length; i++ )
			{
				if ( i > 0 && i % 16 == 0 ) hexDump += "\n";
				hexDump += $"{debugBuffer[i]:X2} ";
			}
			Log.Info( $"[HaloModel] Tag Data Dump:\n{hexDump}" );
			
			// Show geometries block bytes directly
			// Offset 208 was wrong - actual geometry block appears at 240
			if ( debugBuffer.Length >= 252 )
			{
				var geomCount208 = BitConverter.ToInt32( debugBuffer, 208 );
				var geomPtr208 = BitConverter.ToUInt32( debugBuffer, 212 );
				var geomCount240 = BitConverter.ToInt32( debugBuffer, 240 );
				var geomPtr240 = BitConverter.ToUInt32( debugBuffer, 244 );
				Log.Info( $"[HaloModel] Offset 208: Count={geomCount208} Ptr={geomPtr208:X} | Offset 240: Count={geomCount240} Ptr={geomPtr240:X}" );
			}
			
			// Reset and read model tag
			stream.Seek( tagDataOffset, SeekOrigin.Begin );
			
			// Skip 36-byte tag data header (observed in hex dump: actual model starts at offset 36)
			reader.BaseStream.Seek( 36, SeekOrigin.Current );

			// Read Model Tag
			var modelTag = new ModelTag( reader );
			
			Log.Info( $"[HaloModel] ModelTag Blocks:" );
			Log.Info( $"  Markers: Count={modelTag.MarkersCount} Ptr={modelTag.MarkersPointer:X}" );
			Log.Info( $"  Nodes: Count={modelTag.NodesCount} Ptr={modelTag.NodesPointer:X}" );
			Log.Info( $"  Regions: Count={modelTag.RegionsCount} Ptr={modelTag.RegionsPointer:X}" );
			Log.Info( $"  Geometries: Count={modelTag.GeometriesCount} Ptr={modelTag.GeometriesPointer:X}" );
			Log.Info( $"  Shaders: Count={modelTag.ShadersCount} Ptr={modelTag.ShadersPointer:X}" );
			
			// Read Geometries
			if ( modelTag.GeometriesCount == 0 )
			{
				Log.Warning( $"[HaloModel] No geometries in model" );
				return null;
			}

			var geometriesOffset = Map.GetFileOffset( modelTag.GeometriesPointer );
			Log.Info( $"[HaloModel] GeometriesPtr {modelTag.GeometriesPointer:X} -> FileOffset {geometriesOffset}" );
			
			// Also show what Regions resolves to for comparison
			var regionsOffset = Map.GetFileOffset( modelTag.RegionsPointer );
			Log.Info( $"[HaloModel] RegionsPtr {modelTag.RegionsPointer:X} -> FileOffset {regionsOffset}" );
			
			stream.Seek( geometriesOffset, SeekOrigin.Begin );
			
			// Debug: dump more geometry data (96 bytes for 2 geometries worth)
			var geomDebug = reader.ReadBytes( 96 );
			var geomHex = "";
			for ( int i = 0; i < geomDebug.Length; i++ )
			{
				if ( i > 0 && i % 16 == 0 ) geomHex += "\n";
				geomHex += $"{geomDebug[i]:X2} ";
			}
			Log.Info( $"[HaloModel] Geometry Data (96 bytes):\n{geomHex}" );
			stream.Seek( geometriesOffset, SeekOrigin.Begin );

			// For simplicity, just load the first geometry for now
			var geometry = new ModelGeometry( reader );
			
			Log.Info( $"[HaloModel] Parsed Geometry: Flags={geometry.Flags} Parts={geometry.PartsCount} Ptr={geometry.PartsPointer:X}" );

			// Read Parts
			if ( geometry.PartsCount == 0 )
			{
				Log.Warning( $"[HaloModel] No parts in geometry" );
				return null;
			}

			var partsOffset = Map.GetFileOffset( geometry.PartsPointer );
			Log.Info( $"[HaloModel] PartsPtr {geometry.PartsPointer:X} -> FileOffset {partsOffset} (StreamLen={stream.Length})" );
			
			if ( partsOffset < 0 || partsOffset >= stream.Length )
			{
				Log.Warning( $"[HaloModel] Parts offset out of bounds!" );
				return null;
			}
		
		var modelBuilder = new ModelBuilder();

		for ( int i = 0; i < geometry.PartsCount; i++ )
		{
			// Seek to part
			// PartsPtr resolves to an offset inside the extended Geometry block
			// Actual Part data starts 48 bytes after PartsPtr (Parts block is at end of Geometry)
			var partOffset = partsOffset + 48 + (i * 132);
			stream.Seek( partOffset, SeekOrigin.Begin ); // 132 is GBXModelGeometryPart size
			
			// Debug: dump part data
			if ( i == 0 )
			{
				var partDebug = reader.ReadBytes( 132 );
				var partHex = "";
				for ( int j = 0; j < partDebug.Length; j++ )
				{
					if ( j > 0 && j % 16 == 0 ) partHex += "\n";
					partHex += $"{partDebug[j]:X2} ";
				}
				Log.Info( $"[HaloModel] Part[0] Data (132 bytes):\n{partHex}" );
				stream.Seek( partOffset, SeekOrigin.Begin ); // Seek back to start of Part
			}
			
			var part = new GBXModelGeometryPart( reader );

			Log.Info( $"[HaloModel] Part[{i}]: VertexCount={part.VertexCount} VertexOffset={part.VertexOffset:X} TriangleCount={part.TriangleCount} TriangleOffset={part.TriangleOffset:X}" );
			
			// Read Vertices - VertexOffset is relative to the map's vertex data section
			// ModelVertexOffset in the IndexHeader is the base file offset for vertex data
			var vertexDataBase = (long)Map.Index.ModelVertexOffset;
			var vertices = new List<ModelVertexUncompressed>();
			if ( part.VertexCount > 0 && part.VertexOffset > 0 )
			{
				var absoluteVertexOffset = vertexDataBase + part.VertexOffset;
				Log.Info( $"[HaloModel] VertexDataBase={vertexDataBase:X} + VertexOffset={part.VertexOffset:X} = AbsoluteOffset={absoluteVertexOffset:X} (StreamLen={stream.Length})" );
				
				// Debug: dump first 128 bytes at vertex offset to check for compressed (32b) vs uncompressed (68b)
				if ( i == 0 )
				{
					stream.Seek( absoluteVertexOffset, SeekOrigin.Begin );
					var vtxDebug = reader.ReadBytes( 128 );
					var vtxHex = "";
					for ( int j = 0; j < vtxDebug.Length; j++ )
					{
						if ( j > 0 && j % 16 == 0 ) vtxHex += "\n";
						vtxHex += $"{vtxDebug[j]:X2} ";
					}
					Log.Info( $"[HaloModel] Vertex[0] Data (128 bytes):\n{vtxHex}" );
					stream.Seek( absoluteVertexOffset, SeekOrigin.Begin );
				}
				else
				{
					stream.Seek( absoluteVertexOffset, SeekOrigin.Begin );
				}
				
				for ( int v = 0; v < part.VertexCount; v++ )
				{
					vertices.Add( new ModelVertexUncompressed( reader ) );
				}
			}

			// Read Indices (Triangles) - TriangleOffset is relative to the END of vertex data
			// Per YAML: "On PC: offset to triangles relative to the end of the map's vertex data"
			var triangleDataBase = vertexDataBase + Map.Index.VertexDataSize;
			var indices = new List<int>();
			
			// Use IndexCount if available (from offset 80), otherwise fallback (though fallback is likely wrong)
			var indexCount = part.IndexCount;
			
			if ( indexCount > 0 && part.TriangleOffset > 0 )
			{
				var absoluteTriangleOffset = triangleDataBase + part.TriangleOffset;
				Log.Info( $"[HaloModel] TriangleDataBase={triangleDataBase:X} + TriangleOffset={part.TriangleOffset:X} = AbsoluteOffset={absoluteTriangleOffset:X}" );
				
				stream.Seek( absoluteTriangleOffset, SeekOrigin.Begin );
				
				// Read all raw indices first
				var rawIndices = new List<int>();
				for ( int k = 0; k < indexCount; k++ )
				{
					rawIndices.Add( reader.ReadUInt16() );
				}
				
				// Convert Triangle Strip to Triangle List
				// Halo uses 0xFFFF as restart index
				for ( int k = 0; k < rawIndices.Count - 2; k++ )
				{
					var i1 = rawIndices[k];
					var i2 = rawIndices[k + 1];
					var i3 = rawIndices[k + 2];

					// Check for restart index
					if ( i1 == 0xFFFF || i2 == 0xFFFF || i3 == 0xFFFF )
						continue;

					// Swap winding for odd triangles to maintain correct orientation
					// User reported faces were inverted, so we flipped the logic here.
					if ( k % 2 == 1 )
					{
						indices.Add( i1 );
						indices.Add( i2 );
						indices.Add( i3 );
					}
					else
					{
						indices.Add( i3 );
						indices.Add( i2 );
						indices.Add( i1 );
					}
				}
			}

			// Add Mesh to ModelBuilder
			if ( vertices.Count > 0 && indices.Count > 0 )
			{
				// Resolve Material
				Material material = Material.Load( "materials/dev/white.vmat" );
				if ( part.ShaderIndex >= 0 && part.ShaderIndex < modelTag.ShadersCount )
				{
					var shadersOffset = Map.GetFileOffset( modelTag.ShadersPointer );
					// Shader Block Entry size: TagDependency (16) + Permutation (2) + Pad (14) = 32 bytes
					stream.Seek( shadersOffset + (part.ShaderIndex * 32), SeekOrigin.Begin );
					
					var shaderDep = new TagDependency( reader );
					var shaderTagItem = Map.Tags.FirstOrDefault( t => t.Id == shaderDep.Id );
					if ( shaderTagItem.Id != 0 )
					{
						var loadedMat = new HaloMaterial( Map, shaderTagItem ).Load();
						if ( loadedMat != null )
							material = loadedMat;
					}
				}

				var mesh = new Mesh( material );
				
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
				
				// Calculate bounds (QuakeModel does this, might fix NaN issue if bounds are invalid)
				var bounds = BBox.FromPoints( vertices.Select( v => new Vector3( v.Position.x, v.Position.y, v.Position.z ) ) );
				mesh.Bounds = bounds;
				
				modelBuilder.AddMesh( mesh );
			}
		}
		
		return modelBuilder.Create();
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"[HaloModel] Failed to load model: {e.Message}" );
			return null;
		}
	}
}

// Structs based on YAML documentation

public struct ModelTag
{
	public int Flags;
	public int NodeListChecksum;
	public float SuperHighDetailCutoff;
	public float HighDetailCutoff;
	public float MediumDetailCutoff;
	public float LowDetailCutoff;
	public float SuperLowDetailCutoff;
	public short SuperLowDetailNodeCount;
	public short LowDetailNodeCount;
	public short MediumDetailNodeCount;
	public short HighDetailNodeCount;
	public short SuperHighDetailNodeCount;
	public short Pad1;
	public long Pad2; // 8 bytes
	public float BaseMapUScale;
	public float BaseMapVScale;
	// Pad 116 bytes
	
	// Blocks
	public int MarkersCount;
	public uint MarkersPointer;
	public int NodesCount;
	public uint NodesPointer;
	public int RegionsCount;
	public uint RegionsPointer;
	public int GeometriesCount;
	public uint GeometriesPointer;
	public int ShadersCount;
	public uint ShadersPointer;

	public ModelTag( BinaryReader br )
	{
		Flags = br.ReadInt32();
		NodeListChecksum = br.ReadInt32();
		SuperHighDetailCutoff = br.ReadSingle();
		HighDetailCutoff = br.ReadSingle();
		MediumDetailCutoff = br.ReadSingle();
		LowDetailCutoff = br.ReadSingle();
		SuperLowDetailCutoff = br.ReadSingle();
		SuperLowDetailNodeCount = br.ReadInt16();
		LowDetailNodeCount = br.ReadInt16();
		MediumDetailNodeCount = br.ReadInt16();
		HighDetailNodeCount = br.ReadInt16();
		SuperHighDetailNodeCount = br.ReadInt16();
		Pad1 = br.ReadInt16();
		Pad2 = br.ReadInt64();
		BaseMapUScale = br.ReadSingle();
		BaseMapVScale = br.ReadSingle();
		
		br.BaseStream.Seek( 120, SeekOrigin.Current ); // Pad (116 + 4 extra observed in hex dump)
		
		MarkersCount = br.ReadInt32();
		MarkersPointer = br.ReadUInt32();
		br.ReadInt32(); // Pad
		
		NodesCount = br.ReadInt32();
		NodesPointer = br.ReadUInt32();
		br.ReadInt32(); // Pad
		
		RegionsCount = br.ReadInt32();
		RegionsPointer = br.ReadUInt32();
		br.ReadInt32(); // Pad
		
		GeometriesCount = br.ReadInt32();
		GeometriesPointer = br.ReadUInt32();
		br.ReadInt32(); // Pad
		
		ShadersCount = br.ReadInt32();
		ShadersPointer = br.ReadUInt32();
		br.ReadInt32(); // Pad
	}
}

public struct ModelGeometry
{
	public int Flags;
	// Per gbxmodel.yml: Flags(4) + Pad(32) + Parts(12) = 48 bytes
	// But hex dump shows Parts at offset 76, not 36!
	// Possible extra 40 bytes in cached format?
	public int PartsCount;
	public uint PartsPointer;

	public ModelGeometry( BinaryReader br )
	{
		Flags = br.ReadInt32();
		// Skip 32 bytes standard pad + 40 extra bytes observed in hex dump
		br.BaseStream.Seek( 32 + 40, SeekOrigin.Current ); // = 72 bytes, making Parts at offset 76
		
		PartsCount = br.ReadInt32();
		PartsPointer = br.ReadUInt32();
		br.ReadInt32(); // Pad
	}
}

public struct GBXModelGeometryPart
{
	// Per c20-master model.yml + gbxmodel.yml
	// ModelGeometryPart (104 bytes) + GBXModelGeometryPart extra (28 bytes) = 132 bytes
	// In cached maps: non_cached Block fields are replaced with cache_only fields
	
	// Standard fields (bytes 0-31 = 32 bytes)
	public int Flags;                     // 0-3   (4)
	public short ShaderIndex;             // 4-5   (2)
	public byte PrevFilthyPartIndex;      // 6     (1)
	public byte NextFilthyPartIndex;      // 7     (1)
	public short CentroidPrimaryNode;     // 8-9   (2) cache_only
	public short CentroidSecondaryNode;   // 10-11 (2) cache_only
	public float CentroidPrimaryWeight;   // 12-15 (4) cache_only
	public float CentroidSecondaryWeight; // 16-19 (4) cache_only
	public Vector3 Centroid;              // 20-31 (12)
	
	// Cache-only fields
	public uint DoNotCrashTheGame;
	public uint TriangleCount;
	public uint TriangleOffset;
	public uint TriangleOffset2;
	public uint DoNotScrewUpTheModel;
	public uint VertexCount;
	public uint Bullshit;
	public uint VertexOffset;
	
	// Helper properties
	public uint StripCount => TriangleCount;
	public uint IndexCount => Bullshit;

	// GBX extra fields (bytes 104-131 = 28 bytes)
	public byte LocalNodeCount;           // 107
	public byte[] LocalNodeIndices;       // 108-129 (22 bytes)

	public GBXModelGeometryPart( BinaryReader br )
	{
		var startPos = br.BaseStream.Position;
		
		// In CACHED format, the layout is different from raw tag format!
		// Fields 0-3: Flags (4 bytes)
		// Fields 4-7: CentroidPrimaryWeight (float) - NOT shader_index!
		// Fields 8-11: CentroidSecondaryWeight (float)
		// Fields 12-23: Centroid (12 bytes)
		// Fields 24-31: zeros (8 bytes padding)
		// Fields 32-67: More padding/zeros (36 bytes)
		// Fields 68+: cache_only fields OR different layout
		// Fields 128-131: ShaderIndex (2) + Filthy (2)
		
		// Based on hex analysis, read from correct offsets:
		Flags = br.ReadInt32();  // offset 0-3
		
		// In cached format, offset 4-7 is CentroidPrimaryWeight, not shader_index!
		CentroidPrimaryWeight = br.ReadSingle();  // offset 4-7
		CentroidSecondaryWeight = br.ReadSingle(); // offset 8-11
		Centroid = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() ); // offset 12-23
		
		// Skip to cache_only fields
		// TriangleCount at 60, VertexCount at 64 (verified in logs)
		br.BaseStream.Seek( startPos + 48, SeekOrigin.Begin );
		
		// Cache-only fields (offset 48-95)
		DoNotCrashTheGame = br.ReadUInt32();   // 48-51
		br.ReadUInt32(); // padding             // 52-55
		br.ReadUInt32(); // padding             // 56-59
		TriangleCount = br.ReadUInt32();       // 60-63 (was TriangleCount, usually 1)
		VertexCount = br.ReadUInt32();         // 64-67
		TriangleOffset = br.ReadUInt32();      // 68-71
		TriangleOffset2 = br.ReadUInt32();     // 72-75
		DoNotScrewUpTheModel = br.ReadUInt32(); // 76-79
		Bullshit = br.ReadUInt32();            // 80-83 (was Bullshit, seems to be IndexCount)
		br.ReadUInt32(); // pad                 // 84-87
		br.ReadUInt32(); // pad                 // 88-91
		VertexOffset = br.ReadUInt32();        // 92-95
		
		// Read ShaderIndex and Filthy from end of struct (offset 128-131)
		br.BaseStream.Seek( startPos + 128, SeekOrigin.Begin );
		ShaderIndex = br.ReadInt16();
		PrevFilthyPartIndex = br.ReadByte();
		NextFilthyPartIndex = br.ReadByte();
		
		// Clear fields we didn't properly find positions for
		CentroidPrimaryNode = 0;
		CentroidSecondaryNode = 0;
		LocalNodeCount = 0;
		LocalNodeIndices = new byte[22];
		
		// Seek to end of struct (startPos + 132)
		br.BaseStream.Seek( startPos + 132, SeekOrigin.Begin );
	}
}

public struct ModelVertexUncompressed
{
	public Vector3 Position;
	public Vector3 Normal;
	public Vector3 Binormal;
	public Vector3 Tangent;
	public Vector2 TexCoord;
	public short Node0Index;
	public short Node1Index;
	public float Node0Weight;
	public float Node1Weight;

	public ModelVertexUncompressed( BinaryReader br )
	{
		Position = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		Normal = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		Binormal = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		Tangent = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		TexCoord = new Vector2( br.ReadSingle(), br.ReadSingle() );
		Node0Index = br.ReadInt16();
		Node1Index = br.ReadInt16();
		Node0Weight = br.ReadSingle();
		Node1Weight = br.ReadSingle();
	}
}
