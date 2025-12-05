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

		// The Bitmap tag structure (from c20-master/bitmap.yml):
		// Total size: 108 bytes
		// bitmap_group_sequence (Block) is at offset 84 (12 bytes)
		// bitmap_data (Block) is at offset 96 (12 bytes)
		// Block = [Count(4)] [Ptr(4)] [Pad(4)]
		
		// Read and dump tag data for debugging
		var buffer = reader.ReadBytes( 200 ); // Read extra to see what's there
		
		// Hex dump first 200 bytes
		var hexDump = "";
		for ( int i = 0; i < buffer.Length; i++ )
		{
			if ( i > 0 && i % 16 == 0 ) hexDump += "\n";
			hexDump += $"{buffer[i]:X2} ";
		}
		Log.Info( $"[HaloTexture] Tag Data Dump:\n{hexDump}" );
		
		if ( buffer.Length < 108 )
		{
			Log.Warning( $"[HaloTexture] Tag data too short: {buffer.Length} bytes" );
			return null;
		}
		
		// Based on hex dump analysis:
		// Offset 64-79 seems to contain width/height data
		// Offset 124: bitmap_group_sequence (Block)
		// Offset 136: bitmap_data (Block)
		
		// Use offset 136 for bitmap_data
		var bitmapDataCount = BitConverter.ToInt32( buffer, 136 );
		var bitmapDataPtr = BitConverter.ToUInt32( buffer, 140 );
		
		Log.Info( $"[HaloTexture] bitmap_data Block at 136: Count={bitmapDataCount} Ptr={bitmapDataPtr:X}" );

		if ( bitmapDataCount == 0 || bitmapDataPtr == 0 )
		{
			// Fallback: try offset 124
			bitmapDataCount = BitConverter.ToInt32( buffer, 124 );
			bitmapDataPtr = BitConverter.ToUInt32( buffer, 128 );
			Log.Info( $"[HaloTexture] Trying offset 124: Count={bitmapDataCount} Ptr={bitmapDataPtr:X}" );
		}

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
		
		mapStream.Seek( bitmapDataOffset + 40, SeekOrigin.Begin ); // BitmapData starts 40 bytes into the block
		
		// Debug: dump first 48 bytes of BitmapData
		var bitmapDataDebug = mapReader.ReadBytes( 48 );
		var debugStr = "";
		for ( int b = 0; b < bitmapDataDebug.Length; b++ )
		{
			if ( b > 0 && b % 16 == 0 ) debugStr += "\n";
			debugStr += $"{bitmapDataDebug[b]:X2} ";
		}
		Log.Info( $"[HaloTexture] BitmapData at offset {bitmapDataOffset}:\n{debugStr}" );
		
		// Parse the BitmapData struct (48 bytes)
		var bitmapClass = BitConverter.ToUInt32( bitmapDataDebug, 0 ); // bitmap_class
		var width = BitConverter.ToUInt16( bitmapDataDebug, 4 );
		var height = BitConverter.ToUInt16( bitmapDataDebug, 6 );
		var depth = BitConverter.ToUInt16( bitmapDataDebug, 8 );
		var type = BitConverter.ToUInt16( bitmapDataDebug, 10 );
		var format = BitConverter.ToUInt16( bitmapDataDebug, 12 );
		var flags = BitConverter.ToUInt16( bitmapDataDebug, 14 );
		var regX = BitConverter.ToInt16( bitmapDataDebug, 16 );
		var regY = BitConverter.ToInt16( bitmapDataDebug, 18 );
		var mipmapCount = BitConverter.ToUInt16( bitmapDataDebug, 20 );
		// pad at 22
		var pixelDataOffset = BitConverter.ToUInt32( bitmapDataDebug, 24 );
		var pixelDataSize = BitConverter.ToUInt32( bitmapDataDebug, 28 );
		
		Log.Info( $"[HaloTexture] BitmapData: Class={bitmapClass:X8} {width}x{height}x{depth} Type={type} Fmt={format} Flags={flags} Mipmaps={mipmapCount} PixOff={pixelDataOffset:X} PixSize={pixelDataSize}" );

		if ( pixelDataSize == 0 )
		{
			Log.Warning( $"[HaloTexture] Pixel data size is 0 for {Tag.ClassName}" );
			return null;
		}

		// Read Pixels - may be in map file or in external bitmaps.map
		byte[] pixelData;
		var mapFileInfo = new FileInfo( Map.FilePath );
		
		// Check if offset is within this map file
		if ( pixelDataOffset < (uint)mapFileInfo.Length )
		{
			// Pixel data is in this map file
			mapStream.Seek( pixelDataOffset, SeekOrigin.Begin );
			pixelData = mapReader.ReadBytes( (int)pixelDataSize );
		}
		else
		{
			// Pixel data is likely in external bitmaps.map
			var mapsDir = System.IO.Path.GetDirectoryName( Map.FilePath );
			var bitmapsMapPath = System.IO.Path.Combine( mapsDir, "bitmaps.map" );
			
			if ( !File.Exists( bitmapsMapPath ) )
			{
				Log.Warning( $"[HaloTexture] External bitmaps.map not found at {bitmapsMapPath}" );
				return null;
			}
			
			using var bitmapsStream = File.OpenRead( bitmapsMapPath );
			using var bitmapsReader = new BinaryReader( bitmapsStream );
			
			bitmapsStream.Seek( pixelDataOffset, SeekOrigin.Begin );
			pixelData = bitmapsReader.ReadBytes( (int)pixelDataSize );
		}
		
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
