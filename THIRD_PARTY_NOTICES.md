# Third-party notices

This repository contains only the mod's own source code. Nothing from Cuphead is included.

## Used at build time (downloaded by NuGet, not stored in this repository)

| Component | License | Source |
|---|---|---|
| BepInEx.Core 5.4 | LGPL-2.1 | https://github.com/BepInEx/BepInEx |
| HarmonyX | MIT | https://github.com/BepInEx/HarmonyX |
| MonoMod (HarmonyX dependency) | MIT | https://github.com/MonoMod/MonoMod |
| Microsoft .NET Framework 3.5 reference assemblies | MIT | https://github.com/microsoft/dotnet |

## Used at run time, but not part of this repository

- **BepInEx 5.4.23.5** (LGPL-2.1, with HarmonyX/MonoMod, MIT), the mod loader the player installs.
  A release package that bundles BepInEx must include BepInEx's `LICENSE` file and a link to its source.
- **Steamworks.NET** (MIT) and the Steamworks SDK, **Rewired**, and **Unity**, which ship with Cuphead.
  The mod calls them from the player's own installed copy of the game and does not redistribute them.

## Cuphead

Cuphead is © StudioMDHR Entertainment Inc. This is an unofficial, fan-made mod. It is not affiliated
with, endorsed or sponsored by StudioMDHR. It requires a legitimate copy of the game. The build
references the player's own installed game assemblies; no game files, decompiled game code or game
assets are part of this repository or its releases. The screenshots in `docs/images` show Cuphead gameplay
(© StudioMDHR) recorded with the mod, to illustrate what it does.
