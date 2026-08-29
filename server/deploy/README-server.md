# Night Runners MP — dedicated relay server

One small binary. Players connect **out** to it (F12 → `your.server.ip`), so nobody port-forwards or uses a VPN.
It relays car snapshots between everyone (paced by distance), keeps the player list, and owns the
session rules (traffic / collisions) — exactly what an in-game host does, minus a car.

## Quick test on Windows

```
nrmp-server.exe --bots 1
```
In the game: F12 → `127.0.0.1` → Enter. A bot car circles you. `bots 0` in the console removes it.

## Install on a Linux VPS (Ubuntu/Debian) — one paste

The binary and the systemd unit are attached to every GitHub release, so the VPS can fetch them directly.
SSH in as root (or prefix with `sudo`) and paste:
```
R=https://github.com/ShamanAndrey/mp-nightrunners/releases/latest/download
useradd -r -s /usr/sbin/nologin nrmp 2>/dev/null; mkdir -p /opt/nrmp
curl -fsSL $R/nrmp-server -o /opt/nrmp/nrmp-server && chmod +x /opt/nrmp/nrmp-server
curl -fsSL $R/nrmp-server.service -o /etc/systemd/system/nrmp-server.service
chown -R nrmp:nrmp /opt/nrmp
command -v ufw >/dev/null && ufw allow 7777/udp
systemctl daemon-reload && systemctl enable --now nrmp-server
sleep 2 && systemctl --no-pager status nrmp-server | head -5 && journalctl -u nrmp-server -n 3 --no-pager
```
Expected last line: `listening on UDP 7777 (protocol NRMP-0.4, ...)`. Updating later = re-run the same paste.

Live log: `journalctl -u nrmp-server -f`. Manual copy instead of curl: `scp dist\server\linux-x64\nrmp-server root@VPS:/opt/nrmp/`.
Rules are set in `ExecStart` inside the unit file (`--traffic on|off --collisions on|off`); after editing it:
`sudo systemctl daemon-reload && sudo systemctl restart nrmp-server`.

**Password (optional):** players then need it in the F12 panel.
```
echo 'NRMP_PASSWORD=yoursecret' > /opt/nrmp/nrmp.env && chown nrmp:nrmp /opt/nrmp/nrmp.env && chmod 600 /opt/nrmp/nrmp.env
systemctl restart nrmp-server
```
(The unit reads that file; keeping the password out of the command line hides it from `ps`.)

To run it interactively instead (console commands `traffic on|off`, `collisions on|off`, `bots N`, `list`, `quit`):
```
/opt/nrmp/nrmp-server --port 7777 --traffic off
```

## Moderation (kick / ban)

Commands work in the console **or** by writing them to `/opt/nrmp/admin.cmd` (read and deleted within
half a second; results go to the journal), which is how you moderate a systemd-run server:
```
echo "list" > /opt/nrmp/admin.cmd                 # ids, names, IPs, pings
echo "kick 3 calm down" > /opt/nrmp/admin.cmd     # by id, name, or IP
echo "ban 3 ramming everyone" > /opt/nrmp/admin.cmd
echo "ban 203.0.113.9 known griefer" > /opt/nrmp/admin.cmd   # offline player, by IP from the join log
echo "unban 203.0.113.9" > /opt/nrmp/admin.cmd
echo "bans" > /opt/nrmp/admin.cmd
journalctl -u nrmp-server -n 20 --no-pager
```
Bans persist in `/opt/nrmp/bans.txt` (editable by hand; one IP per line). Kicked/banned players see the
reason in their HUD; banned IPs are refused at connect.

## Notes
- UDP 7777 must be open in **both** the OS firewall (ufw) and the provider's firewall panel (Hostinger: VPS → Firewall).
- Protocol key `NRMP-0.4`: players on a different mod version are rejected at connect ("ConnectionRejected").
- Bandwidth per player pair is tiny at full rate (~3 KB/s) and drops with distance; a $4 VPS handles dozens of players.
