# Security model

## What goes over the network

Only what the mod needs to draw other cars: a player-chosen name, a car model number, and car
motion (position, rotation, velocity, steering, lights, RPM, gear). No account data, no hardware or
system information, nothing from the game save.

- **Relay server:** players see only the server's address. The server sees each player's IP (unavoidable
  for UDP) and logs it in its journal.
- **Direct hosting (F11):** the host and each client see each other's IP, like any peer-to-peer game.

## What is protected

- Every packet from the network is parsed inside a guard: malformed data is dropped, never crashes the
  process; repeat offenders are disconnected.
- Fixed packet sizes, exact-size checks, name length caps, rich-text/control-character stripping,
  finite/range checks on all motion values, player-count caps, unknown car models rejected.
- Per-client incoming rate limit (100 packets/s) and per-address connect cooldown on the server and the
  in-game host; outgoing rates are distance-paced, so a client cannot use the server as an amplifier.
- One identity per connection; ids are assigned by the server and never trusted from clients.
- Chat: sanitised like names (no markup/control characters), 200 characters, 3 messages per 2 s per
  player, logged on the server for moderation.
- Optional session password (`--password` / `NRMP_PASSWORD` on the server, `HostPassword` for F11);
  the relay service runs as an unprivileged user in a systemd sandbox.
- The update check only reads GitHub's redirect and only ever opens the hardcoded releases URL.

## Known limitations

- **No encryption.** LiteNetLib is plaintext UDP. Anyone on the path (your LAN, your ISP) can read car
  positions and names — and the session password, which travels inside the connection request. Treat the
  password as "keeps strangers out", not as a secret against someone sniffing your network. Given what is
  transmitted, this is an accepted trade-off; DTLS-style encryption is possible later if ever needed.
- Anyone who can reach the port and knows the password can join; there are no accounts. Operators can
  `kick`/`ban` (persistent IP ban list) from the server console or the admin command file.
- Everyone in a session must run the same mod version (the protocol key rejects mismatches).

Report issues via GitHub issues on this repository.
