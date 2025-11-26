using Sandbox;
using System.IO;
using System;

namespace Sandbox.Mounting.Halo;

public class HaloTexture : ResourceLoader<HaloMount>
{
	HaloMap Map;
	TagItem Tag;

	public HaloTexture( HaloMap map, TagItem tag )
	{
		Map = map;
		Tag = tag;
	}

	protected override object Load()
	{
		return LoadTexture();
	}

	public Texture LoadTexture()
	{
		// Read the Bitmap Tag
		using var stream = Map.GetTagStream( Tag );
		if ( stream == null )
		{
			Log.Warning( $"[HaloTexture] GetTagStream failed for {Tag.ClassName} {Tag.Id:X}" );
			return null;
		}
		using var reader = new BinaryReader( stream );

		// Scan for BitmapData block
		// Heuristic: Look for [Count] [Ptr] [Pad] where Count > 0 and Ptr != 0
		// BitmapData is usually the last block in the struct.
		
		// Read tag data into buffer
		// Note: stream is already positioned at the start of the tag data by GetTagStream
		var buffer = reader.ReadBytes( 512 ); // Read enough bytes (increased to 512)
		
		int bitmapDataCount = 0;
		uint bitmapDataPtr = 0;

		int foundOffset = -1;
		int foundCount = 0;
		uint foundPtr = 0;

		// Start scanning from 100 to avoid false positives in the header
		for ( int i = 100; i <= buffer.Length - 12; i += 4 )
		{
			var count = BitConverter.ToInt32( buffer, i );
			var ptr = BitConverter.ToUInt32( buffer, i + 4 );
			var pad = BitConverter.ToUInt32( buffer, i + 8 );

			// Check for valid block pattern
			if ( count > 0 && count < 100 && ptr > 0x10000 && pad == 0 )
			{
				Log.Info( $"[HaloTexture] Candidate at {i}: Count={count} Ptr={ptr:X}" );
				
				// Verify if this points to a BitmapData struct
				var offset = Map.GetFileOffset( ptr );
				if ( offset > 0 )
				{
					using var checkStream = File.OpenRead( Map.FilePath );
					using var checkReader = new BinaryReader( checkStream );
					checkStream.Seek( offset, SeekOrigin.Begin );
					
					var debugBytes = checkReader.ReadBytes( 32 );
					var debugStr = "";
					for(int b=0; b<debugBytes.Length; b++) debugStr += $"{debugBytes[b]:X2} ";
					Log.Info( $"[HaloTexture] Candidate at {i} Data: {debugStr}" );
					
					checkStream.Seek( offset, SeekOrigin.Begin );
					var firstWord = checkReader.ReadUInt32();
					
					// Check for 'bitm' signature
					if ( firstWord == 0x6D746962 )
					{
						Log.Info( $"[HaloTexture] Valid BitmapData found at {i} (Signature Match)" );
						foundOffset = i;
						foundCount = count;
						foundPtr = ptr;
						break;
					}
					
					// Check for reasonable dimensions (Width/Height)
					// If class is missing, first 4 bytes are Width (16) and Height (16)
					var checkWidth = (ushort)(firstWord & 0xFFFF);
					var checkHeight = (ushort)((firstWord >> 16) & 0xFFFF);
					
					if ( checkWidth > 0 && checkWidth <= 8192 && checkHeight > 0 && checkHeight <= 8192 )
					{
						// Read next fields to be sure
						// Depth (2), Type (2), Format (2)
						var checkDepth = checkReader.ReadUInt16();
						var checkType = checkReader.ReadUInt16();
						var checkFormat = checkReader.ReadUInt16();
						
						// Type: 0-5, Format: 0-17
						if ( checkType <= 5 && checkFormat <= 17 )
						{
							Log.Info( $"[HaloTexture] Valid BitmapData found at {i} (Dimensions: {checkWidth}x{checkHeight}, Type: {checkType}, Format: {checkFormat})" );
							foundOffset = i;
							foundCount = count;
							foundPtr = ptr;
							// Don't break immediately, keep looking for better candidates? 
							// Actually, if we found valid dimensions, it's likely the one.
							// But 124 might be GroupSequence which also points to data.
							// GroupSequence points to BitmapGroupSprite.
							// BitmapGroupSprite: BitmapIndex (2), Pad (2), Pad (4), Left (4)...
							// BitmapIndex is usually small.
							// Let's prefer the one that looks most like a BitmapData.
							
							// If we found one, let's take it.
							break;
						}
					}
					
					Log.Info( $"[HaloTexture] Candidate at {i} rejected: {firstWord:X8}" );
				}
			}
		}

		if ( foundOffset != -1 )
		{
			Log.Info( $"[HaloTexture] Found Block at {foundOffset}: Count={foundCount} Ptr={foundPtr:X}" );
			bitmapDataCount = foundCount;
			bitmapDataPtr = foundPtr;
		}
		else
		{
			Log.Warning( "[HaloTexture] Could not find BitmapData block via scanning." );
			return null;
		}

		if ( bitmapDataCount == 0 )

		if ( bitmapDataCount == 0 )
		{
			Log.Warning( $"[HaloTexture] No bitmap data for {Tag.ClassName}" );
			return null;
		}

		// Read BitmapData entries
		var bitmapDataOffset = Map.GetFileOffset( bitmapDataPtr );
		if ( bitmapDataOffset < 0 )
		{
			Log.Warning( $"[HaloTexture] Invalid bitmap data offset {bitmapDataOffset} (Ptr: {bitmapDataPtr:X})" );
			return null;
		}

		using var mapStream = File.OpenRead( Map.FilePath );
		using var mapReader = new BinaryReader( mapStream );
		
		mapStream.Seek( bitmapDataOffset, SeekOrigin.Begin );
		
		mapReader.ReadInt32(); // Class
		var width = mapReader.ReadUInt16();
		var height = mapReader.ReadUInt16();
		var depth = mapReader.ReadUInt16();
		var type = mapReader.ReadUInt16();
		var format = mapReader.ReadUInt16();
		var flags = mapReader.ReadUInt16();
		mapReader.ReadInt32(); // RegPoint
		var mipmapCount = mapReader.ReadUInt16();
		mapReader.ReadUInt16(); // Pad
		var pixelDataOffset = mapReader.ReadUInt32();
		var pixelDataSize = mapReader.ReadUInt32();

		if ( pixelDataSize == 0 )
		{
			Log.Warning( $"[HaloTexture] Pixel data size is 0 for {Tag.ClassName}" );
			return null;
		}

		// Read Pixels
		mapStream.Seek( pixelDataOffset, SeekOrigin.Begin );
		var pixelData = mapReader.ReadBytes( (int)pixelDataSize );
		
		// Construct DDS Header
		var ddsHeader = CreateDDSHeader( width, height, mipmapCount, format );
		if ( ddsHeader == null )
		{
			Log.Warning( $"[HaloTexture] Unsupported format {format} for {Tag.ClassName}" );
			return Texture.White;
		}

		// Combine Header + Data
		var ddsData = new byte[ddsHeader.Length + pixelData.Length];
		Buffer.BlockCopy( ddsHeader, 0, ddsData, 0, ddsHeader.Length );
		Buffer.BlockCopy( pixelData, 0, ddsData, ddsHeader.Length, pixelData.Length );

		try
		{
			return TextureLoader.FromDds( ddsData );
		}
		catch ( System.Exception e )
		{
			Log.Warning( $"[HaloTexture] Failed to load DDS: {e.Message}" );
			return Texture.White;
		}
	}

	private byte[] CreateDDSHeader( int width, int height, int mipCount, int format )
	{
		using var ms = new MemoryStream();
		using var writer = new BinaryWriter( ms );

		// Magic
		writer.Write( 0x20534444 ); // 'DDS '

		// Header Size
		writer.Write( 124 );

		// Flags (CAPS | HEIGHT | WIDTH | PIXELFORMAT | MIPMAP | LINEARSIZE)
		// DDSD_CAPS = 0x1
		// DDSD_HEIGHT = 0x2
		// DDSD_WIDTH = 0x4
		// DDSD_PITCH = 0x8
		// DDSD_PIXELFORMAT = 0x1000
		// DDSD_MIPMAPCOUNT = 0x20000
		// DDSD_LINEARSIZE = 0x80000
		// DDSD_DEPTH = 0x800000
		uint flags = 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000;
		writer.Write( flags );

		writer.Write( height );
		writer.Write( width );
		writer.Write( 0 ); // PitchOrLinearSize (calculated by reader usually)
		writer.Write( 0 ); // Depth
		writer.Write( mipCount );

		for ( int i = 0; i < 11; i++ ) writer.Write( 0 ); // Reserved1

		// PixelFormat
		writer.Write( 32 ); // Size
		
		uint pfFlags = 0;
		uint fourCC = 0;
		uint rgbBitCount = 0;
		uint rMask = 0, gMask = 0, bMask = 0, aMask = 0;

		// Map Halo Format to DDS
		// 14: DXT1
		// 15: DXT3
		// 16: DXT5
		// 11: A8R8G8B8 (BGRA in DDS usually)
		
		switch ( format )
		{
			case 14: // DXT1
				pfFlags = 0x4; // DDPF_FOURCC
				fourCC = 0x31545844; // 'DXT1'
				break;
			case 15: // DXT3
				pfFlags = 0x4;
				fourCC = 0x33545844; // 'DXT3'
				break;
			case 16: // DXT5
				pfFlags = 0x4;
				fourCC = 0x35545844; // 'DXT5'
				break;
			case 11: // A8R8G8B8 (32-bit) -> BGRA
				pfFlags = 0x40 | 0x1; // DDPF_RGB | DDPF_ALPHAPIXELS
				rgbBitCount = 32;
				rMask = 0x00FF0000;
				gMask = 0x0000FF00;
				bMask = 0x000000FF;
				aMask = 0xFF000000;
				break;
			default:
				// Unsupported for now
				return null;
		}

		writer.Write( pfFlags );
		writer.Write( fourCC );
		writer.Write( rgbBitCount );
		writer.Write( rMask );
		writer.Write( gMask );
		writer.Write( bMask );
		writer.Write( aMask );

		// Caps
		// DDSCAPS_TEXTURE = 0x1000
		// DDSCAPS_MIPMAP = 0x400000
		// DDSCAPS_COMPLEX = 0x8
		uint caps = 0x1000 | 0x400000 | 0x8;
		writer.Write( caps );

		writer.Write( 0 ); // Caps2
		writer.Write( 0 ); // Caps3
		writer.Write( 0 ); // Caps4
		writer.Write( 0 ); // Reserved2

		return ms.ToArray();
	}
}
