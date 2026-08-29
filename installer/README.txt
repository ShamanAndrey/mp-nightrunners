NIGHT RUNNERS MP
================
Multiplayer mod for Night Runners (private alpha). Everyone must run the same game version
and the same mod version.

INSTALL
  1. Extract this zip anywhere (or straight into the game folder).
  2. Double-click Install.bat
     - it finds the game (or asks you to pick the folder),
     - downloads MelonLoader (the mod loader) if you don't have it,
     - installs the mod and asks for your player name and the host's address.
  3. The FIRST launch takes a few minutes while MelonLoader prepares files. Wait for the main menu.

PLAY
  - Drive into free-roam on the same map as the host.
  - HOST:   press F11. Give your friends your address (playit.gg / Tailscale / LAN IP).
  - FRIEND: press F12, type the host's address (and your name), press Enter. It is remembered.
  - A panel in the top-left shows what is going on. F7 hides it.

KEYS
  F11 host      F12 connect (opens the address panel)      F8 disconnect
  Enter  chat (type, Enter to send, Esc to cancel)   /filter on|off  or F3 = your profanity filter
  F5  car collisions on/off (the host decides for everyone)
  F6  traffic on/off (the host decides for everyone)
  F7  hide/show panel      F9 write status to the log
  F4  open the download page (the panel title tells you when an update is out)

UPDATES
  The panel title shows "up to date" or "UPDATE vX.Y.Z available". Download the new zip and run
  Install.bat again - it keeps your config. Everyone in a session must be on the same version.
  Latest release: https://github.com/ShamanAndrey/mp-nightrunners/releases/latest

CONFIG   <game>\UserData\MelonPreferences.cfg  ->  [NightRunnersMP]
  PlayerName, ConnectAddress, ConnectPort, HostPort, TrafficEnabled, GhostCollisions

PROBLEMS
  - "connecting..." forever: wrong address, or the host's firewall blocks UDP 7777.
  - "disconnected: ConnectionRejected": different mod versions, wrong password, or the server is full.
  - Log file: <game>\MelonLoader\Latest.log

UNINSTALL
  Double-click Uninstall.bat (it can also remove MelonLoader to restore a vanilla game).
