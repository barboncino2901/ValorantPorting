# Changelog

What changed in each version. The app shows the new version's section when an update is available and once after
updating; the release workflow puts the same text on the GitHub release. Add a `## <version>` section before
running the release workflow with that version.

## 1.10.1

- **Fix**: agents' eyes no longer get a white film over them (the eye shadow layer was drawn as white glass).
- **The Blender add-on updates itself**: the app now carries the add-on and puts it into Blender 5 for you, every
  time the app updates (the old one goes to the Recycle Bin). **After an update, close Blender and open it again**:
  Blender only loads add-ons when it starts. If Blender still runs an older add-on, the app shows a bar saying so.
  (An outdated add-on was why some people got errors with scenes, or animations arriving as empty collections.)
- **One download**: `CUE4Parse-Natives.dll` is now built into the app; `ValorantPorting.exe` is all you need.
- **First install of the add-on**: Help > Install / update the Blender add-on.

## 1.10.0

- **Scenes**: pick an agent, a gun skin and animations with "Add to scene", then "Send scene to Blender" imports the
  agent, puts the gun in their hand and plays the animations on each, all in one go. Works with combined upper +
  lower body animations and with 1st person arms.
- **1st person camera**: when exporting an agent's 1st person arms, tick "Add a 1st person camera" to get a camera
  where the player's eyes are, with Valorant's field of view. It follows the animations.
- **Clearer layout**: the Animations, Abilities and Maps tabs show their controls in the right column, so the lists get
  the full height. In the Animations tab, "Apply in Blender" applies the selected animation; "Use as upper body" /
  "Use as legs" build a mix of two. The log at the bottom is visible again, and tile names no longer get cut off.
- **What's new**: the update banner and the first start after an update now show what changed (this list).
- **Faster**: the Animations tab remembers how it sorted the montages, so it's ready right away after the first start
  (until the game updates). The Abilities tab no longer freezes the window while it loads.
- **Fix**: standard gun animations (equip, reload, ...) now apply to skins whose gun has fewer bones.
- **Fix**: things sent to Blender while it was still busy importing could get lost; now every one is imported, in order.
- **Fix**: models and textures in very deep folders (paths over 260 characters) now load in Blender.

## 1.9.0

- **Abilities tab**: ability models (Skye's dog, Raze's Boom Bot, Chamber's trap, ...) with their in-game names,
  keys and icons. Models made of several parts come in assembled, rigs come with what hangs on them (Jett's Blade
  Storm knives), and models drawn only with effects aren't listed.
- Help > Discord points to the project's Discord server.

## 1.8.0

- **Search** ranks the best matches first and understands "forward", "upper body", gun and agent names, and small typos.
- **Update from the app**: a banner shows when a new version is out; "Update now" installs it and restarts.
- **Animations tab**: "(full body)" entries for upper + lower body pairs, duplicate montages hidden, montages that play
  several animations in a row shown as "(sequence)", and a badge for which model each animation is for.
- **Combine any two animations** (right-click: use as upper body / lower body) and **Repeat** for runs, walks and idles.
