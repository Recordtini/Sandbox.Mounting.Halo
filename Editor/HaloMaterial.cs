using Sandbox;
using System.IO;
using System.Linq;

namespace Sandbox.Mounting.Halo;

public class HaloMaterial
{
	public HaloMap Map { get; set; }
	public TagItem Tag { get; set; }

	public HaloMaterial( HaloMap map, TagItem tag )
	{
		Map = map;
		Tag = tag;
	}

	public Material Load()
	{
		using var stream = File.OpenRead( Map.FilePath );
		using var reader = new BinaryReader( stream );

		// Seek to Tag Data
		var tagDataOffset = Map.GetFileOffset( (uint)Tag.DataOffset );
		stream.Seek( tagDataOffset, SeekOrigin.Begin );

		// We need to handle different shader types
		// soso = Shader Model
		// senv = Shader Environment
		// schi = Shader Transparent Chicago
		// etc.

		TagDependency baseMap = default;

		if ( Tag.ClassName == "soso" ) // Shader Model
		{
			// Base Map is at offset ~115 in YAML?
			// Shader (40 bytes) + ShaderModel fields
			// Let's try to find the dependency by reading.
			// Or just skip to where we think it is.
			// ShaderModel is complex.
			// Let's try a heuristic: Read dependencies until we find a 'bitm' dependency.
			
			// But dependencies are embedded in the struct.
			// Let's assume the offset I found earlier or try to be robust.
			// In `shader_model.yml`, `base_map` is after `map_v_scale`.
			// `map_u_scale` (4), `map_v_scale` (4).
			// Before that: `animation_color_upper_bound` (12), Pad (12).
			
			// Let's skip the first ~140 bytes and look for a bitm dependency.
			stream.Seek( 140, SeekOrigin.Current );
			
			// Read potential dependencies
			for ( int i = 0; i < 20; i++ ) // Check next 20 fields
			{
				var dep = new TagDependency( reader );
				if ( dep.Class == 0x6269746D ) // 'bitm'
				{
					baseMap = dep;
					break;
				}
				// If not, rewind 12 bytes (TagDependency is 16, we read 16, need to advance 4? No, just read next 4 bytes?)
				// TagDependency is 16 bytes.
				// If we read a dependency and it's not bitm, it might be just data.
				// This heuristic is risky.
				
				// Better approach: Use known offset.
				// Shader (40)
				// ShaderModel:
				// Flags (2) + Pad (14) + Translucency (4?) + Pad (16) + ChangeColor (2) + Pad (30) + MoreFlags (2) + Pad (2) + ColorSource (2) + Anim (20?)
				
				// Let's try to find the exact offset for `soso`.
				// Base Map is usually around offset 0xAC (172) from start of tag data?
				// Let's try seeking to 172.
			}
		}
		else if ( Tag.ClassName == "senv" ) // Shader Environment
		{
			// Base Map is usually the first bitmap dependency?
			// Shader Environment has `base_map` too.
			// Offset is different.
		}

		// If we found a base map
		if ( baseMap.Id != 0 )
		{
			var bitmapTag = Map.Tags.FirstOrDefault( t => t.Id == baseMap.Id );
			if ( bitmapTag.Id != 0 )
			{
				var texture = new HaloTexture( Map, bitmapTag ).LoadTexture();
				if ( texture != null )
				{
					var material = Material.Create( Tag.ClassName, "shaders/halo_simple.shader" );
					material.Set( "Color", texture );
					return material;
				}
			}
		}

		// Fallback
		return Material.Load( "materials/dev/white.vmat" );
	}
}
