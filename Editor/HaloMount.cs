using Sandbox.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System.Linq;

namespace Sandbox.Mounting.Halo;

/// <summary>
/// A mounting implementation for Halo: Combat Evolved (MCC)
/// </summary>
public partial class HaloMount : BaseGameMount
{
	public override string Ident => "halo1";
	public override string Title => "Halo: Combat Evolved (MCC)";

	const long appId = 976730; // Halo: The Master Chief Collection

	readonly Dictionary<string, HaloMap> maps = new();

	protected override void Initialize( InitializeContext context )
	{
		if ( !context.IsAppInstalled( appId ) )
			return;

		var dir = context.GetAppDirectory( appId );
		_installDirectory = dir;
		
		// Halo 1 files are typically in "halo1" folder within MCC
		var halo1Dir = Path.Combine( dir, "halo1" );
		if ( !System.IO.Directory.Exists( halo1Dir ) )
			return;

		// Look for maps in 'maps' subdirectory
		var mapsDir = Path.Combine( halo1Dir, "maps" );
		if ( System.IO.Directory.Exists( mapsDir ) )
		{
			foreach ( var mapPath in System.IO.Directory.EnumerateFiles( mapsDir, "*.map", SearchOption.TopDirectoryOnly ) )
			{
				var mapName = Path.GetFileNameWithoutExtension( mapPath ).ToLowerInvariant();
				var map = new HaloMap( mapPath );
				
				if ( map.IsValid )
				{
					maps[mapName] = map;
				}
			}
		}

		IsInstalled = maps.Count > 0;
	}

	protected override Task Mount( MountContext context )
	{
		foreach ( var (mapName, map) in maps )
		{
			Log.Info( $"[HaloMount] Mounting map: {mapName} (Tags: {map.Tags.Count})" );
			int addedCount = 0;

			// Iterate over tags in the map
			int tagIndex = 0;
			foreach ( var tag in map.Tags )
			{
				tagIndex++;
				var rawTagName = map.GetString( tag.StringOffset );
				
				// Debug first few tags
				if ( tagIndex <= 5 )
				{
					Log.Info( $"[HaloMount] Tag {tagIndex}: Class={tag.ClassName}, Name='{rawTagName}', StringOffset={tag.StringOffset}" );
				}

				if ( string.IsNullOrEmpty( rawTagName ) ) continue;

				var tagName = rawTagName.Replace( '\\', '/' );
				
				// For now, just log or register specific types
				// Example: bitm = Bitmap (Texture)
				if ( tag.ClassName == "bitm" )
				{
					// Register texture
					context.Add( ResourceType.Texture, $"halo1/{mapName}/{tagName}.vtex", new HaloTexture( map, tag ) );
					addedCount++;
				}
				// Example: mod2 = GBXModel (Model)
				else if ( tag.ClassName == "mod2" )
				{
					// Register model
					context.Add( ResourceType.Model, $"halo1/{mapName}/{tagName}.vmdl", new HaloModel( map, tag ) );
					addedCount++;
				}
				// Example: scnr = Scenario (Scene)
				else if ( tag.ClassName == "scnr" )
				{
					// Register scene
					// Use the map name as the scene name for easy access
					context.Add( ResourceType.Scene, $"halo1/{mapName}.scene", new HaloScene( map, tag ) );
					addedCount++;
				}
				// Example: snd! = Sound
				else if ( tag.ClassName == "snd!" )
				{
					context.Add( ResourceType.Sound, $"halo1/{mapName}/{tagName}.vsnd", new HaloSound( map, tag ) );
					addedCount++;
				}
			}
			Log.Info( $"[HaloMount] Added {addedCount} resources from {mapName}" );
		}

		IsMounted = true;
		return Task.CompletedTask;
	}

	public Stream GetFileStream( string filename )
	{
		// Check for loose files in the halo1 directory
		// This allows users to override or add files easily
		var dir = GetAppDirectory( appId );
		if ( string.IsNullOrEmpty( dir ) ) return Stream.Null;
		
		var path = Path.Combine( dir, "halo1", filename );
		if ( File.Exists( path ) )
		{
			return File.OpenRead( path );
		}
		
		return Stream.Null;
	}
	
	public bool FileExists( string filename )
	{
		var dir = GetAppDirectory( appId );
		if ( string.IsNullOrEmpty( dir ) ) return false;
		
		var path = Path.Combine( dir, "halo1", filename );
		return File.Exists( path );
	}
	
	public string GetFullFilePath( string filename )
	{
		var dir = GetAppDirectory( appId );
		if ( string.IsNullOrEmpty( dir ) ) return null;
		
		var path = Path.Combine( dir, "halo1", filename );
		if ( File.Exists( path ) ) return path;
		
		return null;
	}
	
	private string GetAppDirectory( long appId )
	{
		// We don't have direct access to context here, but we can store the directory in Initialize
		// For now, let's assume we can't easily get it without storing it.
		// Let's update Initialize to store the directory.
		return _installDirectory;
	}
	
	private string _installDirectory;
}
