# Online Co-op for Cuphead (unofficial)

An unofficial, fan-made mod that adds online co-op to Cuphead (game version 1.3.4; macOS, Windows experimental),
built on BepInEx 5. One player **hosts** and plays Cuphead on their own save; the other **joins** over the
internet and plays Mugman. Both players need their own legitimate copy of Cuphead and this mod.

> **Not affiliated with StudioMDHR.** Cuphead is © StudioMDHR Entertainment Inc. This project is not made,
> endorsed or sponsored by StudioMDHR. It contains no game files, game code or game assets; see
> [Legal](#legal).

> Steam's built-in **Remote Play Together** also works with Cuphead and needs no mod (Shift+Tab → invite).
> This mod is the native alternative: the guest's game renders the action itself at full resolution
> instead of watching a video stream.

## Install

Both players need the **Steam** version of Cuphead 1.3.4 and this mod. The installers download only two things:
BepInEx 5.4.23.5 (the standard Unity mod loader) from its official GitHub release, and this mod's prebuilt
`CupheadOnline.dll` from the [latest release](https://github.com/nikrabaev/cuphead-online-coop/releases/latest).
Both are checked against SHA-256 checksums before anything in the game folder changes. Nothing else is installed,
and your save files are not touched.

### macOS

Open **Terminal** and paste:

```bash
curl -fsSL https://github.com/nikrabaev/cuphead-online-coop/releases/latest/download/install-macos.sh | bash
```

The installer finds Cuphead in your Steam libraries, installs BepInEx and the mod, and sets Cuphead's Steam launch
options so Steam starts the game with the mod. Steam has to be closed while launch options change: the installer
asks before closing it and reopens it afterwards. If you answer no, it prints the one line to paste into
*Steam → Cuphead → Properties → General → Launch Options* yourself.

Options go after `bash -s --`, for example `... | bash -s -- --yes`:

| Option | Effect |
|---|---|
| `--yes` | close Steam without asking |
| `--skip-launch-options` | don't touch Steam settings; just print the launch options to paste |
| `--game-dir "/path/to/Cuphead"` | use this Cuphead folder (the one containing `Cuphead.app`) |

Run the same command again to update to a newer release.

### Windows (experimental)

Open **PowerShell** and paste:

```powershell
irm https://github.com/nikrabaev/cuphead-online-coop/releases/latest/download/install-windows.ps1 | iex
```

That's all: on Windows BepInEx loads automatically from the game folder, so no launch options are needed. Start
Cuphead from Steam as usual. If Cuphead isn't found automatically, run
`$env:CUPHEAD_DIR = 'D:\SteamLibrary\steamapps\common\Cuphead'` first (your folder containing `Cuphead.exe`).

> The mod is developed and tested on macOS. The Windows game runs the same code, so it is expected to work,
> but it has not been tested on Windows yet. Please open an issue if it doesn't load.

### Manual install

1. Install [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) into the Cuphead folder
   (`BepInEx_macos_universal_*.zip` on macOS, `BepInEx_win_x64_*.zip` on Windows).
2. Download `CupheadOnline.dll` from the [latest release](https://github.com/nikrabaev/cuphead-online-coop/releases/latest)
   and put it in `BepInEx/plugins/`.
3. macOS only: in `run_bepinex.sh` set `executable_name="Cuphead.app"`, and set Cuphead's Steam launch options to
   `"/path/to/Cuphead/run_bepinex.sh" %command%`.

## Uninstall

**macOS** (Terminal):

```bash
curl -fsSL https://github.com/nikrabaev/cuphead-online-coop/releases/latest/download/uninstall-macos.sh | bash
```

**Windows** (PowerShell):

```powershell
irm https://github.com/nikrabaev/cuphead-online-coop/releases/latest/download/uninstall-windows.ps1 | iex
```

The uninstallers remove the mod and its settings, and BepInEx too unless you have other BepInEx mods installed
(keep it with `... | bash -s -- --keep-bepinex` on macOS, or `$env:CUPHEAD_ONLINE_KEEP_BEPINEX = '1'` on Windows).
On macOS Cuphead's launch options are cleared as well (Steam must be closed; the uninstaller asks). Saves are kept.

To remove it by hand: delete `BepInEx/plugins/CupheadOnline.dll` from the Cuphead folder, and on macOS clear Cuphead's
launch options in Steam. Steam's *Verify integrity of game files* does not remove mod files, so delete them yourself.

## Building from source

Players don't need this. Building needs the .NET SDK and your own installed copy of Cuphead: the project references
the game's assemblies from `../Cuphead.app` (override with `-p:GameManaged=<folder>`), and BepInEx/HarmonyX come
from NuGet. Nothing from the game is copied into this repository.

- `scripts/dev-install.sh` builds and installs into your local Cuphead (clone this repository inside the Cuphead folder).
- `scripts/release.sh` builds and packages a release in `dist/` (DLL, plugin zip, installers, `SHA256SUMS.txt`);
  `scripts/release.sh --publish` also creates the GitHub release for the version in `src/Plugin.cs`. Releases are
  built locally because the build needs the game's own assemblies, which can't be put on a CI server.

## Play

Press **F8** in game to open the online menu.

| | Host (player 1, Cuphead) | Guest (player 2, Mugman) |
|---|---|---|
| Via Steam (no port forwarding) | **Host via Steam**, then **Invite a Steam friend…** | Accept the invite, or press **Join** next to the friend in the F8 menu, or paste the host's SteamID |
| Via IP | **Host by IP** (UDP port 27960 by default; forward it, or use Tailscale/ZeroTier/LAN) | Enter `host-ip:27960` and **Join** |

Then, as the host, go to the map or a level. The guest presses any button to spawn Mugman (and once more
if the game shows the "join?" prompt), exactly like a second local controller. The guest's screen
follows the host's game, including menus, cutscenes, the map, levels, HUD, music and sound effects.

Both players must run the same game version and mod version (checked when joining). The guest's save
file is never touched; progress is saved to the host's slot, as in local co-op.

Autostart (handy for scripts): `CUPHEAD_ONLINE=host`, `host:PORT`, `steamhost`, `join:HOST:PORT` or
`steamjoin:STEAMID64` as an environment variable, or `--online=...` as a game argument.

Config: `BepInEx/config/dev.cupheadonline.mod.cfg` (player name, port, menu key, send rate:
`SendInterval = 2` halves the host's upload for slow connections).

## How it works

Cuphead's boss logic uses unseeded `UnityEngine.Random` everywhere, so lockstep simulation would desync
within seconds. Instead the **host is authoritative** and the **guest is a renderer**:

- **Input → host.** Every Cuphead input goes through Rewired's `Player.GetButton*/GetAxis(actionId)`
  (Cuphead's `PlayerInput.actions`, `PlayerManager`'s join logic, `AnyPlayerInput` for menus). The guest
  samples its own controller, sends it 60×/s with per-button press counters (so short taps survive packet
  loss), and the host feeds it into Rewired player 1 with Harmony patches. Like Rewired, button down/up
  edges are tracked separately for `Update` and `FixedUpdate`, since Cuphead polls weapon switch, jump, dash,
  parry and EX in `FixedUpdate`. P2 therefore joins and plays through the game's own code paths. The disconnect prompt is suppressed for the virtual controller.
- **World → guest.** The guest loads the same scene through Cuphead's `SceneLoader` (which also loads
  the sprite-atlas and music asset bundles), then freezes every gameplay `MonoBehaviour` at
  `sceneLoaded`, before `Level.Start` can spawn players or bosses. Every frame the host sends the visible
  `SpriteRenderer`s (sprite, transform, colour, flip, sorting, material, mask interaction), the
  `SpriteMask`s that clip them (DLC levels such as Old Man and the King of Games), the scene cameras, and
  UGUI graphics (`Image`, `Text`, TextMeshPro) as delta-compressed snapshots. Sprites are matched by name
  and geometry plus their late-bound `SpriteAtlas` tag, which the guest asks the game's own `AssetLoader`
  for when needed.
- **Masks and shader parameters.** UGUI `Mask`/`RectMask2D` clipping (e.g. the HUD weapon icon sliding up
  inside its slot) is sent as a clip rect per graphic and applied with `CanvasRenderer.EnableRectClipping`.
  Material parameters changed at runtime (`renderer.material` instances such as Chess Bishop's
  `_BlurAmount` fade, shared materials edited in place such as the Chalice `_RecolorFactor`) are sent per
  material; the guest draws with a clone carrying the host's values only where they differ from its copy.
  Unity 2017 cannot list a shader's properties at runtime, so `MaterialSync` carries the list of
  properties declared by the game's shaders. Boss hit flashes are `SpriteRenderer.color` changes on
  screen-blend shaders and need nothing extra.
- **Sound.** All SFX/music go through static, string-keyed `AudioManager` methods; the host records those
  calls and the guest replays them against its own `AudioManagerComponent`s (`LevelAudio` is created for
  the frozen level). Pause is replicated the same way.
- **Transport.** Unreliable datagrams over UDP or Steam P2P (`ISteamNetworking` with Valve relay), with a
  small ordered-reliable layer (cumulative + selective acks, backoff) for scene changes, asset names,
  despawns and audio.

The mod also fixes a game bug: Cuphead's `SteamManager` is not `DontDestroyOnLoad`, so the first scene
change shuts the Steam API down ("Tried to Initialize the SteamAPI twice"), which breaks Steam features
and DLC checks for the rest of the session. The mod keeps it alive.

## Known limitations

- Latency: the guest sees the game about ½ RTT late, and their input takes about ½ RTT to arrive (like
  Remote Play, but without video compression). Fine at typical internet pings; noticeable above ~120 ms.
- The guest sees what SpriteRenderers, SpriteMasks and UGUI draw. Not mirrored: `RawImage`s (runtime
  textures), camera image-effect parameters, and the few sounds played directly on an `AudioSource`
  instead of through `AudioManager`. UGUI stencil masks are approximated by their rectangle, which matches
  the game's rectangular HUD slots. (Cuphead 1.3.4 contains no `ParticleSystem`s or trail renderers at all,
  in scenes or asset bundles; its "particles" are sprite animations, so they are already mirrored.)
- The host's title screen and storybook pages only accept player 1's input (that's the game's rule).
- Two players only (as in the original game). One guest per host.

## Testing

`CUPHEAD_ONLINE_SCRIPT=file` runs a scripted test inside the game. On the host it plays player 1; on the
guest its presses travel the real network path as player 2. See `src/Sync/TestDriver.cs` for the
commands (`anykey`, `tap`, `hold`, `move`, `waitscene`, `waitp2`, `level`, `invincible`, `hudweapon`, `shot`,
`state`, `fxdump`, …). For example, a host script can go straight to a boss with `level Veggies` after the
house loads, and `fxdump` logs sprite masks, UGUI masks, material instances and property-block overrides.
`CUPHEAD_ONLINE_SHOTS=dir` saves screenshots and `CUPHEAD_ONLINE_DEBUG=1` logs scene/renderer dumps to
`BepInEx/LogOutput.log`. Two instances can run on one Mac (host + `join:127.0.0.1:27960`). Starting the
game outside Steam's launcher like this is a Steamworks developer setting: create `steam_appid.txt`
containing `268910` in the Cuphead folder for local testing only (Steam must still be running and own the
game), and don't ship that file to players.

## Legal

- **Your own copy required.** The mod only runs inside an installed, legitimately owned copy of Cuphead and
  does not bypass its Steam ownership check.
- **No game content.** No Cuphead files, decompiled code, art, audio or other assets are in this repository
  or its releases. The build references the developer's own installed game, like any mod.
- **Licensing.** The mod's source code is MIT-licensed ([LICENSE](LICENSE)); that license grants no rights
  to Cuphead. Third-party components and their licenses are listed in
  [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Release packages that bundle BepInEx must include its
  LGPL-2.1 license.
- **Trademarks.** "Cuphead" is used only to say which game this mod is for. Please don't use the game's
  logo or promotional art for this project.
- **Free, non-commercial.** Don't sell this mod or put it behind a paywall.
- If StudioMDHR asks for this project to be changed or taken down, we will comply.

## Layout

```
src/Plugin.cs            BepInEx entry, config, frame loop, autostart
src/Net/                 Transport (UDP, Steam P2P), Connection (reliability), Session, SteamLobby, Protocol
src/Sync/RemoteInput.cs  PadState / VirtualPad, Rewired patches
src/Sync/HostSync.cs     host capture: sprites, sprite masks, cameras, UGUI (+ mask clipping)
src/Sync/ClientMirror.cs guest: scene load + freeze, mirrored renderers, cameras, UI
src/Sync/MaterialSync.cs runtime material parameters (host capture, guest clones)
src/Sync/AudioSync.cs    AudioManager / PauseManager call replication
src/Sync/Assets.cs       cross-process asset identity (sprites, materials, fonts, atlases)
src/Sync/SteamFix.cs     SteamManager persistence fix
src/Sync/TestDriver.cs   scripted end-to-end tests
src/UI/Overlay.cs        F8 menu
src/Net/SteamSupport.cs  guarded entry point for Steam features (game builds without Steam still load)
scripts/install-*.sh/.ps1, uninstall-*   one-line player installers (attached to every release)
scripts/release.sh       maintainer: build + package + publish a release
scripts/dev-install.sh   developer: build from source into a local Cuphead
```
