NIGHT RUNNERS MP
================
Multiplayer for Night Runners - works with the itch private alpha AND the Steam Prologue.
Everyone in a session must run the same game build and the same mod version.

INSTALL
  1. Extract this zip anywhere.
  2. Double-click Install.bat
     - it finds the game(s) - itch alpha and/or Steam Prologue - or asks you to pick the folder,
     - downloads MelonLoader (the mod loader) if you don't have it,
     - installs the mod and asks for your player name and the server address.
  3. The FIRST launch takes a few minutes while MelonLoader prepares files. Wait for the main menu.

PLAY
  - Drive into free roam.
  - Press F12, type the server address (or a friend's IP), your name and the password if any, press Enter.
    It is remembered for next time.
  - Without a server: one player presses F11 to host and shares their address (playit.gg / Tailscale / LAN).
  - The panel in the top-left shows what is going on. F7 hides it. T opens chat.

KEYS
  F12 connect      F11 host      F8 disconnect      T chat (Enter sends, Esc cancels, /help lists commands)
  F5  car collisions on/off (the host decides for everyone)
  F6  traffic on/off (the host decides for everyone)
  F3  your profanity filter (also /filter on|off)
  F7  hide/show panel      F9 write status to the log      F4 open the download page when an update is out
  F2  ALPHA + PROLOGUE OWNERS: load the Prologue's C1 Tatsumi city into the alpha (~20 s), then F2 again
      for the teleport menu. Chat: /tp list, /tp <area>, /tp next, /tp save <name>, /tp back, /city unload.

UPDATES
  The panel title shows "up to date" or "UPDATE vX.Y.Z available". Download the new zip and run
  Install.bat again - it keeps your config. Everyone in a session must be on the same version.
  Latest release: https://github.com/ShamanAndrey/mp-nightrunners/releases/latest

CONFIG   <game>\UserData\MelonPreferences.cfg  ->  [NightRunnersMP]
  PlayerName, ConnectAddress, ConnectPort, ConnectPassword, HostPort, HostPassword, ChatKey (T),
  TrafficEnabled, GhostCollisions, ChatFilter, PrologueDir (city import; auto-detected)

PROBLEMS
  - "connecting..." forever: wrong address, or the host's firewall blocks UDP 7777.
  - "disconnected: ConnectionRejected": different mod versions, wrong password, or the server is full.
  - Keys do nothing: you must be in free roam, in a car.
  - Log file: <game>\MelonLoader\Latest.log   Screenshots for bug reports: type /shot in chat.

UNINSTALL
  Double-click Uninstall.bat (it can also remove MelonLoader to restore a vanilla game).
