using System;
using System.Collections.Generic;
using System.IO;
using Sandbox;

namespace Sandbox.Mounting.Halo;

public class HaloMap
{
	public string FilePath { get; private set; }
	public MapHeader Header;
	public IndexHeader Index;
	public List<TagItem> Tags = new();
	public uint Magic;
	public bool IsValid { get; private set; }
	public byte[] StringTable { get; private set; }
	public string Name => System.IO.Path.GetFileNameWithoutExtension( FilePath ).ToLowerInvariant();

	public HaloMap( string path )
	{
		FilePath = path;
		if ( Validate() )
		{
			ReadIndex();
		}
	}

	private bool Validate()
	{
		if ( !File.Exists( FilePath ) )
			return false;

		try
		{
			using var stream = File.OpenRead( FilePath );
			using var reader = new BinaryReader( stream );

			if ( stream.Length < 2048 )
				return false;

			stream.Seek( 0, SeekOrigin.Begin );
			
			// Read Header
			Header = new MapHeader( reader );

			return Header.Head == 0x68656164; // 'head'
		}
		catch
		{
			return false;
		}
	}

	public long StringTableFileOffset { get; private set; }

	private void ReadIndex()
	{
		using var stream = File.OpenRead( FilePath );
		using var reader = new BinaryReader( stream );

		// Log Header Info for debugging
		Log.Info( $"[HaloMount] Map: {Name}, Version: {Header.Version}, IndexOffset: {Header.IndexOffset}" );

		var indexOffset = (long)Header.IndexOffset;

		// Verify Index Offset
		if ( !IsValidIndex( stream, indexOffset ) )
		{
			Log.Warning( $"[HaloMount] Header IndexOffset {indexOffset} seems invalid. Searching for 'tags' signature..." );
			indexOffset = FindIndexOffset( stream );
			
			if ( indexOffset == -1 )
			{
				Log.Error( $"[HaloMount] Could not find valid Index Header in {FilePath}" );
				return;
			}
			Log.Info( $"[HaloMount] Found Index Header at {indexOffset}" );
		}

		stream.Seek( indexOffset, SeekOrigin.Begin );
		
		Index = new IndexHeader( reader );
		
		// Calculate Magic
		// Magic = VirtualAddress - FileOffset
		Magic = (uint)Index.IndexMagic - (uint)indexOffset;
		Log.Info( $"[HaloMount] Magic: {Magic:X} (IndexMagic: {Index.IndexMagic:X}, IndexOffset: {indexOffset})" );

		// Seek to Tag Array
		var tagsOffset = indexOffset + IndexHeader.Size;
		
		// Validate TagCount
		if ( Index.TagCount < 0 || Index.TagCount > 100000 ) 
		{
			Log.Warning( $"[HaloMount] Invalid TagCount {Index.TagCount} in {FilePath} (Magic: {Magic:X})" );
			return;
		}

		stream.Seek( tagsOffset, SeekOrigin.Begin );

		for ( int i = 0; i < Index.TagCount; i++ )
		{
			var tag = new TagItem( reader );
			Tags.Add( tag );
		}
		
		// Read String Table
		var tagsSize = Index.TagCount * 32; // TagItem is 32 bytes
		StringTableFileOffset = tagsOffset + tagsSize;
		var stringTableSize = Header.IndexLength - IndexHeader.Size - tagsSize;

		Log.Info( $"[HaloMount] StringTable Info: Offset={StringTableFileOffset}, Size={stringTableSize}, IndexLength={Header.IndexLength}" );

		if ( stringTableSize > 0 )
		{
			if ( StringTableFileOffset < 0 || StringTableFileOffset >= stream.Length )
			{
				Log.Warning( $"[HaloMount] Invalid StringTableOffset {StringTableFileOffset} in {FilePath}" );
				return;
			}

			stream.Seek( StringTableFileOffset, SeekOrigin.Begin );
			StringTable = reader.ReadBytes( stringTableSize );
			Log.Info( $"[HaloMount] Read StringTable ({StringTable.Length} bytes)" );
		}
		else
		{
			Log.Warning( $"[HaloMount] StringTableSize is {stringTableSize} (IndexLength: {Header.IndexLength}, TagsSize: {tagsSize})" );
		}
		
		IsValid = true;
	}

	private bool IsValidIndex( Stream stream, long offset )
	{
		if ( offset < 0 || offset + IndexHeader.Size > stream.Length ) return false;
		
		stream.Seek( offset + 36, SeekOrigin.Begin ); // Signature is at offset 36 in 40-byte header
		using var reader = new BinaryReader( stream, System.Text.Encoding.Default, true );
		var sig = reader.ReadInt32();
		return sig == 0x73676174 || sig == 0x74616773; // 'tags'
	}

	private long FindIndexOffset( Stream stream )
	{
		// Brute force search for 'tags' signature
		// It's usually aligned? Let's search every 4 bytes.
		// Optimization: Search only the first few MBs or near the expected offset?
		// Halo maps can be large.
		// The signature is at the END of the header (offset 32).
		
		var buffer = new byte[4096];
		stream.Seek( 0, SeekOrigin.Begin );
		
		long position = 0;
		int bytesRead;
		
		while ( (bytesRead = stream.Read( buffer, 0, buffer.Length )) > 0 )
		{
			for ( int i = 0; i < bytesRead - 4; i += 4 )
			{
				// Check for 'tags' (0x74616773) or 'sgat' (0x73676174)
				// Little Endian: 'sgat' = 0x74616773 ? 
				// 't' = 0x74, 'a' = 0x61, 'g' = 0x67, 's' = 0x73
				// "tags" in ASCII bytes: 74 61 67 73
				// As int (LE): 0x73676174
				
				uint val = BitConverter.ToUInt32( buffer, i );
				if ( val == 0x73676174 || val == 0x74616773 )
				{
					// Found signature. Index Header starts 32 bytes before this.
					long foundSigOffset = position + i;
					long possibleIndexOffset = foundSigOffset - 36;
					
					if ( possibleIndexOffset >= 0 )
					{
						// Verify it looks like a header
						if ( IsValidIndex( stream, possibleIndexOffset ) )
							return possibleIndexOffset;
					}
				}
			}
			position += bytesRead;
		}
		
		return -1;
	}

	public string GetString( int virtualOffset )
	{
		if ( StringTable == null ) return string.Empty;
		
		// Convert Virtual Address to Local String Table Offset
		// FileOffset = VirtualAddress - Magic
		// LocalOffset = FileOffset - StringTableFileOffset
		
		long fileOffset = (uint)virtualOffset - Magic;
		long localOffset = fileOffset - StringTableFileOffset;

		if ( localOffset < 0 || localOffset >= StringTable.Length )
		{
			// Log.Warning( $"[HaloMount] GetString: Invalid local offset {localOffset} (Virtual: {virtualOffset}, Magic: {Magic:X}, FileOffset: {fileOffset})" );
			return string.Empty;
		}

		// Heuristic: Scan backwards to find the start of the string
		// The pointers seem to point into the middle of strings sometimes (e.g. "rnoon" instead of "afternoon")
		// This might be due to suffix sharing or slight offset miscalculation.
		// For asset paths, we want the full string.
		
		int start = (int)localOffset;
		while ( start > 0 && StringTable[start - 1] != 0 )
		{
			start--;
		}

		var end = start;
		while ( end < StringTable.Length && StringTable[end] != 0 )
		{
			end++;
		}

		return System.Text.Encoding.ASCII.GetString( StringTable, start, end - start );
	}

	public long GetFileOffset( uint virtualAddress )
	{
		return virtualAddress - Magic;
	}

	public Stream GetTagStream( TagItem tag )
	{
		var offset = GetFileOffset( (uint)tag.DataOffset );
		if ( offset < 0 || offset >= new FileInfo( FilePath ).Length ) return null;

		var stream = File.OpenRead( FilePath );
		stream.Seek( offset, SeekOrigin.Begin );
		return stream;
	}
}

public struct MapHeader
{
	public int Head;
	public int Version;
	public int FileLength;
	public int Unknown0;
	public int IndexOffset;
	public int IndexLength;
	public int TagCount;
	public int RootTagCount;

	public MapHeader( BinaryReader br )
	{
		Head = br.ReadInt32();
		Version = br.ReadInt32();
		FileLength = br.ReadInt32();
		Unknown0 = br.ReadInt32();
		IndexOffset = br.ReadInt32();
		IndexLength = br.ReadInt32();
		TagCount = br.ReadInt32();
		RootTagCount = br.ReadInt32();
	}
}

public struct IndexHeader
{
	public const int Size = 40; // 10 * 4 bytes
	// Actually, standard index header is:
	// 0x00: IndexMagic (4)
	// 0x04: BaseTag (4)
	// 0x08: TagCount (4)
	// 0x0C: VertexCount (4)
	// 0x10: ModelVertexOffset (4)
	// 0x14: IndicesCount (4)
	// 0x18: ModelIndicesOffset (4)
	// 0x1C: ModelDataSize (4)
	// 0x20: Signature (4) 'tags'

	public int IndexMagic;       // tag_array_pointer
	public int ScenarioTagId;    // scenario_tag_id
	public int Checksum;         // checksum
	public int TagCount;         // tag_count
	public int ModelPartCount;   // model_part_count
	public int ModelVertexOffset;// model_data_file_offset
	public int ModelPartCountPC; // model_part_count_pc
	public int VertexDataSize;   // vertex_data_size
	public int ModelDataSize;    // model_data_size
	public int Signature;        // magic ('tags')

	public IndexHeader( BinaryReader br )
	{
		IndexMagic = br.ReadInt32();
		ScenarioTagId = br.ReadInt32();
		Checksum = br.ReadInt32();
		TagCount = br.ReadInt32();
		ModelPartCount = br.ReadInt32();
		ModelVertexOffset = br.ReadInt32();
		ModelPartCountPC = br.ReadInt32();
		VertexDataSize = br.ReadInt32();
		ModelDataSize = br.ReadInt32();
		Signature = br.ReadInt32();
	}
}

public struct TagItem
{
	public int ClassA;
	public int ClassB;
	public int ClassC;
	public int Ident;
	public uint Id => (uint)Ident;
	public int StringOffset;
	public int DataOffset;
	public int Unknown;
	public int Unknown2;

	public string ClassName
	{
		get
		{
			// Convert int to 4 chars, reversed because of endianness usually, but let's check
			// Halo tags are usually 4 chars.
			var bytes = BitConverter.GetBytes( ClassA );
			Array.Reverse( bytes ); // Big endian tag names?
			return System.Text.Encoding.ASCII.GetString( bytes ).Trim( '\0' );
		}
	}

	public TagItem( BinaryReader br )
	{
		ClassA = br.ReadInt32();
		ClassB = br.ReadInt32();
		ClassC = br.ReadInt32();
		Ident = br.ReadInt32();
		StringOffset = br.ReadInt32();
		DataOffset = br.ReadInt32();
		Unknown = br.ReadInt32();
		Unknown2 = br.ReadInt32();
	}
}
