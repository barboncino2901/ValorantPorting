# Changelog

What changed in each version. The app shows the new version's section when an update is available and once after
updating; the release workflow puts the same text on the GitHub release. Add a `## <version>` section before
running the release workflow with that version.

## 1.10.1

**After this update: close Blender and open it again.** The app now updates the Blender add-on by itself, and
Blender only loads add-ons when it starts.

### Updates and install
- **The Blender add-on updates itself**: the app carries the add-on and puts it into Blender 5 for you whenever the
  app updates (the old one goes to the Recycle Bin). If Blender is still running an older add-on, a bar in the app
  says so. (An outdated add-on was why some people got errors with scenes, or animations arriving as empty
  collections.)
- **One download**: `CUE4Parse-Natives.dll` is built into the app now; `ValorantPorting.exe` is all you need.
- **First install of the add-on**: Help > Install / update the Blender add-on, then tick it in Blender's add-on list.

### Fixes
- **Animations play at their real speed**, so an agent's animation and the gun's stay in sync. Valorant stores them
  at different frame rates (about 30 fps for 3rd person, 43-50 for 1st person, 60 for guns); before, the gun's
  equip lagged behind by about a second.
- **Maps**: no more white "Albedo" placeholder texture on map surfaces (very visible on Corrode, present on every
  map). Corrode's "overlay" walls now get their grime texture as in game.
- **Agents' eyes** no longer get a white film over them.
- **"Object reference not set to an instance of an object"** when sending things quickly one after another, or
  clicking another item while an export was still running: exports now take turns and remember what you clicked.

### Scenes and gun animations
- Gun animations in a scene always land on the gun, and each step goes to the right model without depending on
  what's selected in Blender.
- Gun animations are labelled **"Gun 1st person"** / **"Gun 3rd person"**.
- If a gun has no 3rd person animation for what you picked, the scene explains it (in game the gun then just
  follows the hands), lists the guns that do have one, and offers to send the scene without it.

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
