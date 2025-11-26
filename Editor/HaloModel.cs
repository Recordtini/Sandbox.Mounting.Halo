using Sandbox;
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
		using var stream = File.OpenRead( Map.FilePath );
		using var reader = new BinaryReader( stream );

		// Seek to Tag Data
		var tagDataOffset = Map.GetFileOffset( (uint)Tag.DataOffset );
		stream.Seek( tagDataOffset, SeekOrigin.Begin );

		// Read Model Tag
		var modelTag = new ModelTag( reader );

		// Read Geometries
		if ( modelTag.GeometriesCount == 0 )
			return null;

		var geometriesOffset = Map.GetFileOffset( modelTag.GeometriesPointer );
		stream.Seek( geometriesOffset, SeekOrigin.Begin );

		// For simplicity, just load the first geometry for now
		var geometry = new ModelGeometry( reader );

		// Read Parts
		if ( geometry.PartsCount == 0 )
			return null;

		var partsOffset = Map.GetFileOffset( geometry.PartsPointer );
		
		var modelBuilder = new ModelBuilder();

		for ( int i = 0; i < geometry.PartsCount; i++ )
		{
			// Seek to part
			stream.Seek( partsOffset + (i * 132), SeekOrigin.Begin ); // 132 is GBXModelGeometryPart size
			var part = new GBXModelGeometryPart( reader );

			// Read Vertices
			var vertices = new List<ModelVertexUncompressed>();
			if ( part.UncompressedVerticesCount > 0 )
			{
				var verticesOffset = Map.GetFileOffset( part.UncompressedVerticesPointer );
				stream.Seek( verticesOffset, SeekOrigin.Begin );
				for ( int v = 0; v < part.UncompressedVerticesCount; v++ )
				{
					vertices.Add( new ModelVertexUncompressed( reader ) );
				}
			}

			// Read Indices (Triangles)
			var indices = new List<int>();
			if ( part.TrianglesCount > 0 )
			{
				var trianglesOffset = Map.GetFileOffset( part.TrianglesPointer );
				stream.Seek( trianglesOffset, SeekOrigin.Begin );
				for ( int t = 0; t < part.TrianglesCount; t++ )
				{
					indices.Add( reader.ReadUInt16() ); // Vertex0
					indices.Add( reader.ReadUInt16() ); // Vertex1
					indices.Add( reader.ReadUInt16() ); // Vertex2
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
				
				modelBuilder.AddMesh( mesh );
			}
		}

		return modelBuilder.Create();
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
		
		br.BaseStream.Seek( 116, SeekOrigin.Current ); // Pad
		
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
	// Pad 32
	public int PartsCount;
	public uint PartsPointer;

	public ModelGeometry( BinaryReader br )
	{
		Flags = br.ReadInt32();
		br.BaseStream.Seek( 32, SeekOrigin.Current );
		PartsCount = br.ReadInt32();
		PartsPointer = br.ReadUInt32();
		br.ReadInt32(); // Pad
	}
}

public struct GBXModelGeometryPart
{
	// Base ModelGeometryPart fields (104 bytes)
	public int Flags;
	public int ShaderIndex; // Index
	public byte PrevFilthyPartIndex;
	public byte NextFilthyPartIndex;
	public short CentroidPrimaryNode; // Index
	public short CentroidSecondaryNode; // Index
	public float CentroidPrimaryWeight;
	public float CentroidSecondaryWeight;
	public Vector3 Centroid;
	
	public int UncompressedVerticesCount;
	public uint UncompressedVerticesPointer;
	public int CompressedVerticesCount;
	public uint CompressedVerticesPointer;
	public int TrianglesCount;
	public uint TrianglesPointer;
	
	// Extra GBX fields (28 bytes)

	public GBXModelGeometryPart( BinaryReader br )
	{
		Flags = br.ReadInt32();
		ShaderIndex = br.ReadInt16(); 
		PrevFilthyPartIndex = br.ReadByte();
		NextFilthyPartIndex = br.ReadByte();
		CentroidPrimaryNode = br.ReadInt16();
		CentroidSecondaryNode = br.ReadInt16();
		CentroidPrimaryWeight = br.ReadSingle();
		CentroidSecondaryWeight = br.ReadSingle();
		Centroid = new Vector3( br.ReadSingle(), br.ReadSingle(), br.ReadSingle() );
		
		UncompressedVerticesCount = br.ReadInt32();
		UncompressedVerticesPointer = br.ReadUInt32();
		br.ReadInt32(); // Pad
		
		CompressedVerticesCount = br.ReadInt32();
		CompressedVerticesPointer = br.ReadUInt32();
		br.ReadInt32(); // Pad
		
		TrianglesCount = br.ReadInt32();
		TrianglesPointer = br.ReadUInt32();
		br.ReadInt32(); // Pad
		
		// Skip remaining fields (24 bytes base + 28 bytes GBX)
		br.BaseStream.Seek( 24 + 28, SeekOrigin.Current );
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
