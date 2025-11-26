using Sandbox;
using System.IO;

namespace Sandbox.Mounting.Halo;

public class HaloSound : ResourceLoader<HaloMount>
{
	HaloMap Map;
	TagItem Tag;

	public HaloSound( HaloMap map, TagItem tag )
	{
		Map = map;
		Tag = tag;
	}

	protected override object Load()
	{
		// Read Sound Tag
		using var stream = Map.GetTagStream( Tag );
		if ( stream == null ) return null;
		using var reader = new BinaryReader( stream );

		// Sound Tag Structure (Partial)
		// 0x00: Flags (4)
		// 0x04: Class (2)
		// 0x06: SampleRate (2)
		// ...
		// PitchRanges Block at offset ?
		// c20 sound.yml:
		// Flags (4)
		// Class (2)
		// SampleRate (2)
		// MinDist (4)
		// MaxDist (4)
		// SkipFrac (4)
		// RandomPitch (8)
		// InnerCone (4)
		// OuterCone (4)
		// OuterConeGain (4)
		// RandomGain (4)
		// MaxBend (4)
		// Pad (12)
		// ... lots of modifiers ...
		// PitchRanges (Block) at end?
		
		// Let's count bytes.
		// 4+2+2+4+4+4+8+4+4+4+4+4+12 = 60.
		// Modifiers: 4+4+4+12 + 4+4+4+12 = 48.
		// ChannelCount (2)
		// Format (2)
		// Promotion (Dependency: 16)
		// PromotionCount (2)
		// Pad (2)
		// LongestPerm (4)
		// CumulativeProm (4)
		// LastProm (4)
		// ScriptedSoundRem (4)
		// ScriptedSoundIndex (4)
		// PitchRanges (Block: 12)
		
		// 60 + 48 + 2+2 + 16 + 2+2 + 4+4+4+4+4 + 12 = 
		// 108 + 4 + 16 + 4 + 20 + 12 = 164.
		// PitchRanges is the LAST field.
		// Offset = 164 - 12 = 152.
		
		stream.Seek( 152, SeekOrigin.Begin );
		var rangeCount = reader.ReadInt32();
		var rangePtr = reader.ReadUInt32();
		
		if ( rangeCount == 0 ) return null;
		
		// Read First Pitch Range
		var rangeOffset = Map.GetFileOffset( rangePtr );
		if ( rangeOffset < 0 ) return null;
		
		using var mapStream = File.OpenRead( Map.FilePath );
		using var mapReader = new BinaryReader( mapStream );
		
		mapStream.Seek( rangeOffset, SeekOrigin.Begin );
		
		// PitchRange Struct (72 bytes)
		// Name (32)
		// NaturalPitch (4)
		// BendBounds (8)
		// ActualPermCount (2)
		// Pad (2)
		// PlaybackRate (4)
		// UsedPerms (4)
		// LastPerm (2)
		// NextPerm (2)
		// Permutations (Block: 12) -> Offset 60
		
		mapStream.Seek( rangeOffset + 60, SeekOrigin.Begin );
		var permCount = mapReader.ReadInt32();
		var permPtr = mapReader.ReadUInt32();
		
		if ( permCount == 0 ) return null;
		
		// Read First Permutation
		var permOffset = Map.GetFileOffset( permPtr );
		if ( permOffset < 0 ) return null;
		
		mapStream.Seek( permOffset, SeekOrigin.Begin );
		
		// SoundPermutation Struct (124 bytes)
		// Name (32)
		// SkipFrac (4)
		// Gain (4)
		// Format (2)
		// NextPerm (2)
		// SamplesPtr (4)
		// Pad (4)
		// TagId0 (4)
		// BufferSize (4)
		// TagId1 (4)
		// Samples (TagDataOffset: 20) -> Offset 64?
		// ...
		
		// Let's verify offset of Samples.
		// 32+4+4+2+2+4+4+4+4+4 = 64.
		// Samples starts at 64.
		
		mapStream.Seek( permOffset + 64, SeekOrigin.Begin );
		
		// TagDataOffset (20 bytes? Or 12?)
		// c20 says 20 bytes.
		// Size (4)
		// External (4)
		// FileOffset (4)
		// Pointer (8)
		
		var sampleSize = mapReader.ReadUInt32();
		var flags = mapReader.ReadUInt32(); // External?
		var sampleOffset = mapReader.ReadUInt32();
		
		// If external (flag 1?), it's in sounds.map.
		// For now, assume internal or try to read.
		
		// Log.Info( $"[HaloSound] Loading {Tag.ClassName} Size={sampleSize} Offset={sampleOffset} Flags={flags}" );
		
		if ( sampleSize == 0 ) return null;
		
		// Read Samples
		// If internal, sampleOffset is file offset?
		// Or relative to something?
		// Usually absolute file offset in MCC.
		
		mapStream.Seek( sampleOffset, SeekOrigin.Begin );
		var data = mapReader.ReadBytes( (int)sampleSize );
		
		// Create Sound
		// We need format.
		// Format is at offset 40 in Permutation?
		// 32+4+4 = 40.
		mapStream.Seek( permOffset + 40, SeekOrigin.Begin );
		var format = mapReader.ReadInt16();
		
		// Format Enum:
		// 0: 16-bit PCM
		// 1: Xbox ADPCM
		// 2: IMA ADPCM
		// 3: Ogg Vorbis
		
		// s&box supports WAV (PCM) and maybe Ogg?
		// If PCM, we can wrap in WAV header.
		// If ADPCM, we need to decode.
		
		return SoundFile.FromWav( "halo_sound.wav", data, false ); // Placeholder
	}
}
