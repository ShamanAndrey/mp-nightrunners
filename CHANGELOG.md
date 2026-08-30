# Changelog

## v0.4.0

**C1 Tatsumi inside the alpha (experimental).** If you own the Steam Prologue as well, press **F2** in the
alpha's free roam to load the Prologue's city and drive it — read at runtime from your own Prologue install,
nothing is redistributed. Roads, buildings, tunnels, streetlights, baked lightmaps, the Prologue's ambient
light and its sky. About 20 seconds to load and ~2 GB of extra memory; the whole city stays loaded.
Known gaps: no ground below the expressway (the Prologue draws it with its sky, which the alpha cannot show),
no traffic in the city, no garages or meet spots, some lighting differs from the Prologue.

**Teleport control.** F2 again opens a menu (arrows / W-S / Enter / mouse) with every area of the city,
your bookmarks and "back to Mount Haruna". Chat equivalents: `/tp list`, `/tp <area>`, `/tp next`,
`/tp prev`, `/tp save <name>`, `/tp <name>`, `/tp x y z`, `/tp back`, `/city unload`.

**Chat on T.** The chat line now opens with **T** instead of Enter (the game uses Enter to interact) and works
outside sessions too, for commands. Rebind with `ChatKey`.

**Screenshots for bug reports.** `/shot` saves a screenshot, `/shot top [height]` a bird's-eye view and
`/shot side` a side view, all to `UserData\NightRunnersMP-shots\`.

**Config.** New: `ChatKey`, `PrologueDir`, `CityLightmaps`, `CitySceneLighting`, `CitySkybox`, `CitySkydome`,
`CitySpawn`, `CitySpawnYaw`. The installer now also ships `UserLibs\classdata.tpk` (Unity type data used by
the city importer).

Protocol unchanged (`NRMP-0.6`): v0.3.0 servers keep working. Everyone in a session still needs the same
mod version.

## v0.3.0

Steam Prologue support: the same mod runs in the Prologue and the alpha; the installer finds either or both.
Sessions are per build and the relay server keeps alpha and Prologue players in separate rooms. Protocol
`NRMP-0.6`.

## v0.2.2

Optional profanity filter, per player (`ChatFilter`, F3, `/filter`) and per server (`--filter on`).

## v0.2.1

Game input is isolated while typing and while the window is unfocused.

## v0.2.0

In-game chat.

## v0.1.7

Server moderation: kick, ban, unban, persistent ban list.
