# Halo: Combat Evolved (MCC) Mount for s&box

This project implements a mounter for Halo: Combat Evolved (MCC edition), allowing assets from the game (maps, models, textures, sounds) to be used within s&box.

## Features

- **Maps**: Loads `.map` files as Scenes (`.scene`), including level geometry (BSP) and placed entities (scenery, vehicles, weapons, machines, controls).
- **Models**: Loads `mod2` tags as Models (`.vmdl`).
- **Textures**: Loads `bitm` tags as Textures (`.vtex`), supporting DXT1/DXT5 compression.
- **Materials**: Automatically creates Materials (`.vmat`) from `soso` shader tags, linking to the correct textures.
- **Sounds**: Registers `snd!` tags as Sounds (`.vsnd`).
- **File Access**: Supports loading loose files from the `halo1` directory for overrides.

## Installation / Compilation

1.  **Build**: Open the project directory in a terminal and run:
    ```powershell
    dotnet build Sandbox.Mounting.Halo.csproj -c Release
    ```
2.  **Install DLL**: Locate the compiled DLL (e.g., `bin/Release/Sandbox.Mounting.Halo.dll`) and copy it to your s&box installation's `bin/managed/` folder.
3.  **Setup Mount Folder**: Create a folder named `halo1` inside your s&box `mount` directory (e.g., `sbox/game/mount/halo1/`).
4.  **Install Assets**: Copy the contents of the `Assets` folder (e.g., `shaders`) from this project into `sbox/game/mount/halo1/`.
5.  **Enable**: Ensure your project's `.sbproj` includes `"halo1"` in the `Mounts` list.

## Usage

Once mounted, you can access Halo assets via the Asset Browser or by path:
-   **Scenes**: `halo1/{mapName}.scene` (e.g., `halo1/bloodgulch.scene`)
-   **Models**: `halo1/{mapName}/{tagName}.vmdl`
-   **Textures**: `halo1/{mapName}/{tagName}.vtex`

## Requirements

-   **Halo: The Master Chief Collection** installed via Steam (AppID 976730).
-   **Halo: CE** installed within MCC.
-   **.NET 7 SDK** (or newer) installed to compile the project.
