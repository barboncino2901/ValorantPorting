# ValorantPorting [![Discord](https://discord.com/api/guilds/866821077769781249/widget.png?style=shield)](https://discord.com/invite/valorant3d)
A free and open-source tool that automates porting Valorant agents, weapon skins, gun buddies, animations and maps
to Blender.

This is a community-updated fork of [Ka1serM/ValorantPorting](https://github.com/Ka1serM/ValorantPorting)
(the UE5 version by catxa2018), updated for current Valorant builds and **Blender 5**.

## What's new in this fork
- **Works with current Valorant (UE5)**: up-to-date CUE4Parse; mappings and the AES key download automatically at
  startup (from [uedb.dev](https://uedb.dev)), so the app keeps working after game patches.
- **Blender 5 support**: new PSK/PSA importer for Blender 5.x (the old add-ons broke in Blender 4.1+).
- **Animations tab**: every animation in the game, with readable names (agent, gun, skin, ability). Select an armature
  in Blender and double-click an animation to apply it. The list follows your Blender selection (3rd person,
  1st person, character select or gun). Agents keep their own face and body proportions.
- **Guns in hand**: select an agent in Blender, then send a gun - it snaps into the right hand and follows the
  animations (reloads included). Select a gun, then send a buddy - it hangs on the gun's buddy point.
  Guns have their own animations too (reload, equip, inspect), in sync with the agent's.
- **Maps tab** (experimental): exports a whole map (USD) and imports it into Blender with Valorant's environment
  shaders rebuilt (two-layer blends, tints, painted vertex colors), the map's sun and a sky light.
- **Favorites and recent**: right-click anything to favorite it; the "Show:" filter switches every tab between
  All, Favorites and Recent (what you sent to Blender).
- Default gun attachments (e.g. the Warden's scope), much lower memory use (~0.6 GB instead of 5 GB),
  faster map imports, and many smaller fixes.

## Installation

### Requirements
* Windows 10/11 x64
* [Blender 5.x](https://www.blender.org/download/) (tested with 5.2)
* [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) (the **Windows Desktop x64** installer)
* [Visual C++ Redistributable x64](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)
* A local Valorant installation

### ValorantPorting (the app)
1. Download `ValorantPorting.exe` and `CUE4Parse-Natives.dll` from the latest [release](../../releases) and put them
   in the same folder, somewhere programs can write to (not Program Files). `CUE4Parse-Natives.dll` is needed for
   animations.
2. Run `ValorantPorting.exe` and point it to your Valorant folder
   (usually `C:\Riot Games\VALORANT\live\ShooterGame\Content\Paks`). Close Valorant while you use the app.

### Blender add-on
1. Download `ValorantPortingBlender.zip` from the same release. **Don't extract it.**
2. In Blender: **Edit > Preferences > Add-ons > Install from Disk**, pick the zip, and make sure the add-on's
   checkbox is ticked (reinstalling an add-on can untick it).
3. Restart Blender. The add-on listens for the app in the background.

`valorant_psk_psa_b5.py` (optional, also in the release) is a standalone importer for .psk/.psa files by hand
(File > Import > Valorant PSA/PSK [Blender 5]). The add-on already contains it.

## Usage
- **Agents / Weapon Skins / Gun Buddies**: pick an item (and a chroma), then send it to Blender.
- **Animations**: select the agent's or gun's armature in Blender, pick an animation and apply it (or double-click).
  Gun animations: `GN_` = 1st person, `GNTP_` = 3rd person.
- **Guns in hand**: select the agent (3rd or 1st person) before sending a gun. To detach, delete the
  "Valorant Porting attach" constraint on the gun.
- **Maps**: pick a map and export it. The first export of a map takes a while and a few GB of disk space;
  Blender is busy for 10-30 seconds while it imports.
- **Favorites**: right-click > Add to favorites; use the "Show:" dropdown next to Search.

## Building from source
Clone the repository with its submodules:
```
git clone https://github.com/barboncino2901/ValorantPorting --recursive
```
Build the app (needs the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)):
```
dotnet publish ValorantPorting -c Release --no-self-contained -r win-x64 -f net10.0-windows -o "./Release" -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
```
Build `CUE4Parse-Natives.dll` (animation decompression; needs CMake and Visual Studio Build Tools with C++), then copy
it next to `ValorantPorting.exe`:
```
cmake -S CUE4Parse/CUE4Parse-Natives -B natives-build -A x64
cmake --build natives-build --config Release
```
The Blender add-on is the `ValorantPortingBlender` folder; zip the folder itself to install it.

The GitHub Actions workflow ("ValorantPorting Builder", run manually) builds all of this and creates a release.

## Credits
* Valorant [live] code: [FModel](https://github.com/4sval/FModel) & [MercuryCommons](https://github.com/FortniteCentral/MercuryCommons)
* [CUE4Parse](https://github.com/FabianFG/CUE4Parse) by FabianFG and contributors
* https://github.com/halfuwu
* https://github.com/djhaled
* https://github.com/KaiserM21
* https://github.com/Bmarquez1997
* https://github.com/catxa2018-hub
* PSK/PSA importer based on [blender3d_import_psk_psa](https://github.com/Befzz/blender3d_import_psk_psa)
  by Darknet, flufy3d, camg188 and befzz (GPL 2.0 or later)
* Game data names from [valorant-api.com](https://valorant-api.com); mappings from [uedb.dev](https://uedb.dev)

Valorant and all its assets belong to Riot Games. This project isn't affiliated with or endorsed by Riot Games.
