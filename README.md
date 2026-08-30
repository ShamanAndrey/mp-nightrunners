# Night Runners MP

Multiplayer for **Night Runners** (PLANET JEM): see your friends' cars on the mountain, drive together, chat —
plus an experimental import of the Prologue's **C1 Tatsumi city** into the alpha.

Built on MelonLoader. Works with the itch **private alpha** (Mount Haruna) and the Steam **Prologue** (C1 Tatsumi).
The mod never redistributes game files: everything it shows comes from the games you already own.

- [Install](#install) · [Play](#play) · [Controls](#controls) · [Chat commands](#chat-commands)
- [The city in the alpha](#the-city-in-the-alpha)
- [Hosting](#hosting) · [Configuration](#configuration) · [Troubleshooting](#troubleshooting)
- [For developers](#for-developers)

---

## Install

1. Download `NightRunnersMP-v*.zip` from the [latest release](https://github.com/ShamanAndrey/mp-nightrunners/releases/latest) and extract it anywhere.
2. Double-click **`Install.bat`**. It finds your game(s) — alpha, Prologue or both — installs MelonLoader if
   needed, copies the mod, and asks for your player name and the server address you want to use.
3. Start the game normally. The **first launch takes a few minutes** while MelonLoader prepares its files; later
   launches are quick.

`Uninstall.bat` removes the mod and MelonLoader again. Updating: install the new zip over the old one — the
game's config and your bookmarks are kept.

The HUD title tells you whether you are on the latest version. If not, **F4** opens the download page. Everyone
in a session must run the same version.

## Play

1. Drive into **free roam**.
2. **F12** → type the server address (or a friend's IP), your name and the password if there is one → **Enter**.
3. Your friends appear as cars with a name tag. **T** opens chat.

To play without a server, one player presses **F11** to host and the others connect to that player's address
(see [Hosting](#hosting)). A session is always one game build: alpha players play with alpha players, Prologue
with Prologue — the server keeps them in separate rooms.

What is synchronised: car model and position/rotation/velocity, steering, lights, RPM and gear, so remote cars
roll their wheels and sound like they should. AI traffic is local to each player (not synced). Remote cars are
hidden after five seconds without data (their owner is in a menu or the garage) and return when data resumes.

## Controls

| Key | What it does |
|-----|--------------|
| **F12** | Connect panel: address, name, password. Enter connects, Esc cancels. Remembers your last entries. |
| **F11** | Host a session yourself on `HostPort` (default 7777). |
| **F8** | Disconnect and remove the other cars. |
| **T** | Open the chat line (Enter sends, Esc cancels). Rebind with `ChatKey`. Works outside sessions too, for commands. |
| **F5** | Car collisions on/off. On: other cars are solid. **Host decides** for the whole session. |
| **F6** | AI traffic on/off. **Host decides** for the whole session; your own setting returns when you leave. |
| **F3** | Your personal profanity filter (masks words *you* see, e.g. `f***`). |
| **F7** | Show/hide the HUD. |
| **F4** | Open the releases page when an update exists. |
| **F2** | Alpha only: load the Prologue's city / open the teleport menu. See [below](#the-city-in-the-alpha). |
| **F9** | Write a status snapshot to the MelonLoader log (for bug reports). |

While a text box is open the mod pauses the game's keyboard input and car control, so typing never shifts gears
or opens menus. The same pause applies while the game window is unfocused (`BlockInputWhenUnfocused`), because
the game otherwise keeps reacting to keys while you are tabbed out.

## Chat commands

| Command | Effect |
|---------|--------|
| `/help` | List commands and keys. |
| `/filter on` / `/filter off` | Personal profanity filter (same as F3). Extra words: `UserData\NightRunnersMP-badwords.txt`, one per line, trailing `*` for prefixes. |
| `/tp list` | Areas and bookmarks you can teleport to (city loaded). |
| `/tp <area>` · `/tp next` · `/tp prev` | Jump to an area of the city, or step through its roads one by one. |
| `/tp save <name>` · `/tp <name>` | Bookmark your current spot / return to it. Bookmarks persist in `UserData\NightRunnersMP-bookmarks.txt`. |
| `/tp x y z` | Teleport to Prologue coordinates. |
| `/tp back` | Back to where you were on Mount Haruna. |
| `/city unload` | Free the city's memory. |
| `/city lighting on\|off` · `/city skydome on\|off` | Compare the Prologue's lighting / horizon with the alpha's. |
| `/shot` · `/shot top [height]` · `/shot side` | Save a screenshot (normal, bird's-eye, or side view) to `UserData\NightRunnersMP-shots\`. Handy for bug reports. |

## The city in the alpha

The alpha ships one map, Mount Haruna. The Steam Prologue ships the much larger **C1 Tatsumi** city. If you own
both, the mod can rebuild the city inside the alpha: press **F2** in free roam.

The first load takes about 20 seconds and reads the city straight from **your** Prologue installation on
Steam — meshes, textures, materials, colliders, streetlights, baked lightmaps, the Prologue's ambient light and
its horizon skybox. Nothing is copied into the alpha and nothing is redistributed; every player who wants to
drive the city needs the Prologue installed. The install is found automatically through Steam; set `PrologueDir`
if it lives somewhere unusual.

Once loaded, **F2** opens the teleport menu (arrow keys / W-S, Enter, Esc; the mouse works too): spawn road, every
area of the city grouped by district, your bookmarks, back to Mount Haruna, unload. The same things are
available as `/tp` chat commands.

Status: **experimental.** Multiplayer on the city works like anywhere else (everyone must load it). Not yet:
ground below the expressway (the Prologue draws it with its sky, which the alpha cannot show), traffic in the
city, the Prologue's garages and meet spots, distance-based streaming (the whole city stays loaded — it uses
about 2 GB of extra memory).

## Hosting

**Easiest: a relay server.** Run `nrmp-server` on any machine with a public address (a cheap VPS is plenty).
Players connect *out* to it with F12, so nobody port-forwards or installs a VPN. The server owns the session
rules (traffic, collisions), supports a password, and lets the operator `kick`, `ban` and `unban` from its
console or the `admin.cmd` file. Build it with `.\tools\publish-server.ps1`; `server\deploy\README-server.md`
has the step-by-step VPS setup with a systemd unit.

**Without a server:** one player presses **F11**. The others need to reach that player's UDP `HostPort`
(default 7777): forward the port on the router, or use a LAN / [Tailscale](https://tailscale.com) /
[playit.gg](https://playit.gg) (UDP tunnel — ngrok does **not** carry UDP). The host's F5/F6 settings apply to
everyone. Set `HostPassword` to keep strangers out.

`SECURITY.md` explains exactly what goes over the wire and what is protected. Short version: only names and car
motion are sent, nothing from your system or save; every packet is validated and rate-limited; but the
connection is plain UDP, not encrypted.

## Configuration

`<game>\UserData\MelonPreferences.cfg`, section `[NightRunnersMP]`. Everything has a sensible default; the
installer fills in your name and server.

| Key | Default | Meaning |
|-----|---------|---------|
| `PlayerName` | `Runner` | Name shown above your car and in chat. |
| `ConnectAddress`, `ConnectPort`, `ConnectPassword` | `127.0.0.1`, `7777`, empty | What F12 starts with (also editable in the panel). |
| `HostPort`, `HostPassword` | `7777`, empty | For hosting with F11. |
| `ChatKey` | `T` | Key that opens chat (a Unity KeyCode name). |
| `ChatFilter` | `false` | Personal profanity filter. |
| `GhostCollisions` | `false` | Other cars are solid (host-controlled in a session). |
| `TrafficEnabled` | `true` | AI traffic (host-controlled in a session). |
| `BlockInputWhenUnfocused` | `true` | Pause game input while the window is unfocused. |
| `CheckForUpdates` | `true` | Ask GitHub once per launch whether a newer release exists. |
| `SendRateHz` | `25` | Snapshots per second at close range. |
| `InterpDelayMs` | `80` | Minimum smoothing delay; raised automatically when the connection jitters. |
| `PrologueDir` | auto | Steam Prologue folder, for the city import. |
| `CityLightmaps`, `CitySceneLighting`, `CitySkybox`, `CitySkydome` | `true` | Import the Prologue's baked lighting / ambient & fog / sky cubemap / horizon panorama. |
| `CitySpawn`, `CitySpawnYaw` | — | Fallback spawn in Prologue coordinates (normally not needed). |
| `GhostOffset` | `0` | Dev only: shift remote cars sideways (set 4 for the loopback self-test). |

## Troubleshooting

- **"Version mismatch" / cannot connect** — everyone needs the same mod version, and the server must be updated
  too. The protocol key includes the version and the game build.
- **Nothing happens on F12 / keys** — you must be in free roam, in a car. The HUD (F7) shows the mod's state.
- **Friend's car is jerky** — that is the network, not the game: the HUD shows each car's delay, jitter and
  whether it is interpolating or predicting. Beyond 150 m cars update at lower rates by design.
- **Typing in chat drives the car** — make sure `BlockInputWhenUnfocused` is on and you are on the current
  version; the mod disables the game's keyboard while a text box is open.
- **City: cannot load** — the Prologue must be installed via Steam; check the MelonLoader log for `[city]` lines
  and set `PrologueDir` if it was not found.
- **Bug reports** — attach `MelonLoader\Latest.log` and, if it is visual, a `/shot` screenshot.

---

## For developers

### Repository layout

| Path | Contents |
|------|----------|
| `src/` | The mod (C#, MelonLoader, Il2CppInterop). `Net/` protocol and sessions, `Sync/` car state, smoothing and game glue, `Ui/` HUD, panels and chat, `MapImport/` the city importer. |
| `shared/` | Code compiled into both the mod and the server (profanity filter). |
| `server/` | `nrmp-server`, the standalone relay. `server/Protocol.cs` mirrors `src/Net/Packets.cs` — change both together. |
| `installer/` | `Install.bat` / `Uninstall.bat` and the PowerShell behind them. Offline except for the MelonLoader download. |
| `tools/` | `release.ps1` (build + zip), `publish-server.ps1` (single-file server binaries), `nrmp-ping` (protocol probe/fuzzer), `nrmp-mapinspect` (reads Prologue scene files without Unity). |
| `refs/` | Il2CppDumper output for both games. **Gitignored** — game-derived, never commit. |

### Building

- .NET SDK; the mod targets MelonLoader's `net6` runtime and references the interop assemblies MelonLoader
  generates on the game's first launch (`<game>\MelonLoader\Il2CppAssemblies\`).
- `dotnet build src -c Release` builds and copies the DLL (plus `LiteNetLib.dll` and `UserLibs\classdata.tpk`)
  into the alpha and, if present, the Prologue. Copies are skipped while a game is running and holds the file.
- After a game update: rerun Il2CppDumper into `refs/dump` (alpha) or `refs/dump-prologue`, delete the generated
  interop assemblies, launch once, rebuild.

### Releasing

Bump `<Version>` in `src/NightRunnersMP.csproj` and the `MelonInfo` version in `src/Core.cs` together, then
`.\tools\release.ps1` → `dist\NightRunnersMP-v<version>.zip`, and
`gh release create v<version> dist\NightRunnersMP-v<version>.zip`. Deploy the matching server with
`.\tools\publish-server.ps1` (the protocol key changes with the version).

### How it works

**Networking.** LiteNetLib UDP. The host — a player (F11) or the relay server — assigns ids and relays fixed-size
state packets. Each stream is paced by the distance between the two cars: full rate within 50 m, then 10 / 4 /
1 Hz at 150 / 400 m and beyond, with hysteresis. Receivers sample snapshots stamped with the sender's physics
time, estimate the clock offset, render `max(InterpDelayMs, 2×interval + 3×jitter)` behind the newest
snapshot with cubic Hermite interpolation, dead-reckon when data is late, and dissolve corrections over ~80 ms
instead of snapping. Session rules (traffic, collisions) come from the host. Every inbound packet is parsed
under a guard with size, range and rate checks (`SECURITY.md`).

**Game glue.** Both builds expose the same classes (`CarParent` spawns cars, `RCC_CarControllerV3` drives them,
`GodConstant` holds the world, Rewired reads input). Differences live in `Sync/GameVariant.cs` and
`Sync/TrafficControl.cs`; the alpha additionally uses a floating origin, handled by `Sync/WorldOrigin.cs`.

**City import** (`src/MapImport/`). AssetsTools.NET reads the Prologue's serialized scenes at runtime using
`classdata.tpk` (Unity type information). The importer rebuilds the original GameObject hierarchy (needed:
the map's zones are Blender-style parents rotated −90° and scaled ×27), decodes meshes from raw vertex
streams, uploads DXT/BC6H textures as-is, remaps materials onto the alpha's Standard shader, keeps colliders
on the physics layer, appends the Prologue's lightmaps to the alpha's, and replays what the Prologue's own
streaming script would do: only the driving scenes (`C1_1` + `C1_AREA_*`), no far-LOD proxies, tunnel shells
switched on, the horizon skybox scaled to the far clip plane. Imported renderers ignore the alpha's light
probes (which are black outside Mount Haruna) and take the Prologue's ambient instead. `tools/nrmp-mapinspect`
is the offline companion for looking at scenes, meshes, materials, lightmaps and textures.

### Ground rules

- Never commit game files or game-derived assets. `refs/` and `dist/` are gitignored for that reason.
- Change `src/Net/Packets.cs` and `server/Protocol.cs` together and bump the protocol version.
- Keep the installer offline (MelonLoader is the only download).
