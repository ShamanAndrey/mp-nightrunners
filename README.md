# Night Runners Multiplayer Mod

A multiplayer sync mod for **Night Runners** (PLANET JEM, private alpha), built with MelonLoader.

## For players

1. Download the latest `NightRunnersMP-v*.zip` from the [Releases page](https://github.com/ShamanAndrey/mp-nightrunners/releases/latest) and extract it.
2. Double-click **`Install.bat`** — it finds the game, installs MelonLoader if needed, installs the mod, and asks for your name and the host's address.
3. First launch takes a few minutes (MelonLoader prepares files). Then: drive into free-roam, **F11** to host or **F12** to connect.

Hosting without port-forwarding: use [playit.gg](https://playit.gg) (UDP tunnel — ngrok does **not** carry UDP), Tailscale, or a LAN. `Uninstall.bat` removes everything again.

The HUD title shows whether you're on the latest release; when a newer one exists, **F4** opens the download page (disable with `CheckForUpdates = false`). Everyone in a session must run the same version.

> **Note:** this repo must never contain game files or game-derived assets. `refs/` holds
> locally generated reverse-engineering output and is gitignored — do not commit or publish it.

## Layout

- `src/` — the C# MelonLoader plugin
- `refs/dump/` — Il2CppDumper output from `GameAssembly.dll` (regenerate after every game update)

## Game tech notes

- Unity **2019.4.41f2**, IL2CPP (metadata v24.5, unencrypted). Addressables, Easy Save 3, Rewired.
- Car physics: **RCC — Realistic Car Controller V3** (`RCC_CarControllerV3`, `RCC_SceneManager`).
- Car spawning: `CarParent` MonoBehaviour — `AICarSpawn(...)`, `StockCarSpawn(...)`,
  `OwnedCarSpawn(...)`, all take a spawn `Transform`; `car_overwrite` describes a full car build
  (model, parts, paint) → can materialize a remote player's exact car.
- Livery/paint sync surface: `car_AIRacer.AILivery : Texture` + paint color fields.
- Deprecated UNet is compiled into the build — do not use; networking via bundled LiteNetLib.

## Architecture (planned)

- MelonLoader plugin (Unity 2019.4 IL2CPP), Harmony hooks via Il2CppInterop.
- LiteNetLib UDP; one player hosts, clients own their car state (peer-trust).
- Remote cars: spawned via `CarParent`, kinematic rigidbody, ~100 ms interpolation buffer,
  20–30 Hz state packets (pos, rot, velocity, steer, throttle/brake, RPM, gear, lights).
- AI traffic stays local-only (not synced).

## Roadmap

- **v0.1** — two players free-roam, see each other driving (transform sync only)
- **v0.2** — correct car models/customization/liveries, RPM-driven engine audio, lights
- **v0.3** — lobby UI, chat, synced race starts

## Controls (v0.1)

| Key | Action |
|-----|--------|
| F4  | Open the GitHub releases page (the HUD title says whether an update exists) |
| F5  | Toggle car collisions (persisted as `GhostCollisions`). **Host-controlled in a session** like traffic. On: ghosts are solid and moved through PhysX (`MovePosition`, interpolated) so contacts carry momentum; off: cars pass through each other |
| F6  | Toggle AI traffic (persisted as `TrafficEnabled`; "off" clears existing traffic and is re-applied on every map load). **Host-controlled in a session:** the host's rule is sent to clients on join and on change; a client's F6 is ignored until it disconnects, then its own setting is restored |
| F7  | Toggle the in-game HUD (on by default: car, host/client, ghosts, recent log) |
| F9  | Write a status snapshot to the log |
| F11 | Host a session on `HostPort` |
| F12 | Connect to `ConnectAddress:ConnectPort` (if already hosting and address is localhost → loopback self-test) |
| F8  | Disconnect and remove ghosts |

Config lives in `<game>\UserData\MelonPreferences.cfg` under `[NightRunnersMP]`:
`PlayerName`, `HostPort`, `ConnectAddress`, `ConnectPort`, `SendRateHz` (25; rounded to whole physics steps), `InterpDelayMs` (80; floor for the adaptive interpolation delay), `GhostCollisions` (false; true makes remote cars solid — one-way, like walls), `GhostOffset` (4; metres to shift **only the loopback ghost** right — real players are never offset).

Remote cars show a floating name tag with distance, spin their wheels from synced velocity, and are hidden after 5 s without packets (owner in garage/menu) and shown again when packets resume.

### Distance-scaled update rates (`Sync/SendRate.cs`)

Every snapshot stream is paced by the distance between the two cars involved: **0–50 m full rate
(`SendRateHz`), 50–150 m 10 Hz, 150–400 m 4 Hz, beyond 1 Hz**, with 20 % hysteresis before dropping
a tier. Each sender paces its upload by its nearest known player; the host additionally paces every
(sender → recipient) relay pair, which is what keeps the host's upload sane with many players.
The receiver notices rate drops within a packet or two and stretches its prediction window,
correction time and snap threshold with the measured interval, so far cars stay plausible at 1 Hz.

### Smoothing (`Sync/RemoteCar.cs`)

Snapshots are sampled in the sender's `FixedUpdate` and stamped with `Time.fixedTime`. The receiver estimates the clock offset (chasing the lowest observed latency), renders `max(InterpDelayMs, 2×interval + 3×jitter)` behind the newest snapshot, uses cubic Hermite (position + velocity) between snapshots, dead-reckons from velocity/angular velocity when data is late (≤300 ms), and dissolves any correction over ~80 ms instead of snapping (jumps >5 m snap). The HUD shows each ghost's delay, jitter and mode (`interp` / `predict Nms` / `hold`).

**Solo loopback test:** drive in free-roam → F11 → F12. Your own car's state travels host→client→host through real UDP and a ghost copy of your car appears 4 m to your right, mirroring you with ~100 ms latency.

**Two players:** both install MelonLoader + `NightRunnersMP.dll` (Mods) + `LiteNetLib.dll` (UserLibs). Host forwards UDP `HostPort` on their router (or both use Radmin VPN / ZeroTier / Tailscale and use the VPN IP). Host: drive in free-roam, F11. Friend: set `ConnectAddress` to the host's IP, drive in free-roam, F12. Both must be on the same map.

## Shipping a release

`.\tools\release.ps1` builds, assembles `dist\pkg\` (DLLs + everything in `installer\`), and zips it as
`dist\NightRunnersMP-v<Version>.zip` (version from the csproj — bump `<Version>` and the `MelonInfo` string together).
Publish with `gh release create v<version> dist\NightRunnersMP-v<version>.zip`. Never include game files.
The installer (`installer\install.ps1`) is offline: it only downloads MelonLoader.

## Dev workflow

1. Game install: `D:\itch\night-runners-private-alpha` (MelonLoader + built DLL in `Mods\` are
   the only things that live there).
2. First launch after installing MelonLoader generates interop assemblies under
   `MelonLoader\Il2CppAssemblies\` — the plugin project references those.
3. Post-build step copies the plugin DLL into the game's `Mods\` folder.
4. After a game update: re-run Il2CppDumper into `refs/dump/`, delete the generated interop
   assemblies, relaunch, rebuild.
