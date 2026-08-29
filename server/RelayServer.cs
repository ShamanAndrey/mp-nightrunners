using LiteNetLib;
using LiteNetLib.Utils;

namespace NightRunnersMP.Server;

/// <summary>
/// Dedicated relay: the same role the in-game host plays, minus a car of its own.
/// Assigns ids, keeps the player list, relays snapshots paced by distance, owns the session rules.
/// Everything that arrives from a client is treated as hostile until validated.
/// </summary>
public sealed class RelayServer
{
    private const float IncomingPacketsPerSecondLimit = 100f; // a client at full rate sends 25
    private const float ConnectCooldownPerAddress = 2f;       // seconds between accepted connects per IP
    private const int MalformedPacketsBeforeKick = 3;

    private sealed class Player
    {
        public int Id;
        public string Name = "";
        public int Model;
        public NetPeer? Peer;          // null for bots
        public Bot? Bot;
        public bool HasPos;
        public Vec3 Pos;
        public long Packets;
        public int Dropped;
        public int Malformed;
        public float RateWindowStart;
        public int RateWindowCount;
        public readonly Dictionary<int, RateGate> GatesBySender = new();

        public RateGate GateFor(int senderId)
        {
            if (!GatesBySender.TryGetValue(senderId, out var g)) GatesBySender[senderId] = g = new RateGate();
            return g;
        }
    }

    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly NetDataWriter _w = new();
    private readonly Dictionary<int, Player> _byId = new();
    private readonly Dictionary<NetPeer, Player> _byPeer = new();
    private readonly Dictionary<string, float> _lastConnectByAddress = new();
    private readonly string _key;
    private readonly NetDataWriter _reason = new();
    private int _nextId = 1;
    private float _nextBotTick;

    public BanList Bans { get; }

    public int Port { get; }
    public int MaxPlayers { get; set; } = 32;
    public float FullRateHz { get; set; } = 25f;
    public bool Traffic { get; private set; } = true;
    public bool Collisions { get; private set; }
    public bool HasPassword { get; }

    public int PlayerCount => _byPeer.Count;
    public int BotCount => _byId.Values.Count(p => p.Bot != null);

    public RelayServer(int port, bool traffic, bool collisions, string? password = null, string banFile = "bans.txt")
    {
        Port = port;
        Traffic = traffic;
        Collisions = collisions;
        HasPassword = !string.IsNullOrEmpty(password);
        _key = Protocol.KeyFor(password);
        Bans = new BanList(banFile);
        _net = new NetManager(_listener)
        {
            AutoRecycle = true,
            DisconnectTimeout = 10000,
            UnconnectedMessagesEnabled = false,
            BroadcastReceiveEnabled = false,
        };

        _listener.ConnectionRequestEvent += req =>
        {
            var addr = req.RemoteEndPoint.Address.ToString();
            if (Bans.Contains(addr)) { RejectWith(req, "You are banned from this server."); return; }
            if (_byPeer.Count >= MaxPlayers) { RejectWith(req, "Server is full."); return; }
            var now = SendRate.Now;
            if (_lastConnectByAddress.TryGetValue(addr, out var last) && now - last < ConnectCooldownPerAddress) { req.Reject(); return; }
            _lastConnectByAddress[addr] = now;
            if (_lastConnectByAddress.Count > 10_000) _lastConnectByAddress.Clear();
            if (req.AcceptIfKey(_key) == null) Log($"rejected {addr}: wrong version or password");
        };
        _listener.PeerConnectedEvent += peer => Log($"peer {peer.Id} connected from {peer.Address}, waiting for Hello");
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (!_byPeer.Remove(peer, out var p)) { Log($"peer {peer.Id} left before Hello ({info.Reason})"); return; }
            _byId.Remove(p.Id);
            Log($"{p.Name} (#{p.Id}) left: {info.Reason}. {PlayerCount} player(s) online");
            _w.Reset(); _w.Put(Protocol.PlayerLeft); _w.Put(p.Id);
            _net.SendToAll(_w, DeliveryMethod.ReliableOrdered);
        };
        _listener.NetworkReceiveEvent += (peer, reader, _, _) =>
        {
            try { OnReceive(peer, reader); }
            catch (Exception e)
            {
                // A malformed packet must never take the server down; repeat offenders are dropped.
                if (_byPeer.TryGetValue(peer, out var p) && ++p.Malformed >= MalformedPacketsBeforeKick)
                {
                    Log($"kicking {p.Name} (#{p.Id}): {p.Malformed} malformed packets ({e.GetType().Name})");
                    peer.Disconnect();
                }
                else if (!_byPeer.ContainsKey(peer))
                {
                    peer.Disconnect(); // garbage before Hello: no second chance
                }
            }
        };
    }

    public bool Start()
    {
        var ok = _net.Start(Port);
        Log(ok ? $"listening on UDP {Port} (protocol {Protocol.Key}, {(HasPassword ? "password protected" : "no password")}, traffic {(Traffic ? "on" : "off")}, collisions {(Collisions ? "on" : "off")}, max {MaxPlayers}, {Bans.Count} banned IP(s))"
               : $"FAILED to bind UDP {Port}");
        return ok;
    }

    // ---- moderation --------------------------------------------------------------------------

    private void RejectWith(ConnectionRequest req, string message)
    {
        _reason.Reset(); _reason.Put(message);
        req.Reject(_reason);
    }

    /// <summary>Resolve "3", "Andrey" or "1.2.3.4" to connected players (bots are never targets).</summary>
    private List<Player> Resolve(string target)
    {
        var real = _byPeer.Values;
        if (int.TryParse(target, out var id)) return real.Where(p => p.Id == id).ToList();
        if (BanList.IsIp(target)) return real.Where(p => p.Peer!.Address.ToString() == target).ToList();
        return real.Where(p => string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public string Kick(string target, string reason)
    {
        var hits = Resolve(target);
        if (hits.Count == 0) return $"no connected player matches '{target}' (use: list)";
        foreach (var p in hits)
        {
            _reason.Reset(); _reason.Put($"Kicked: {reason}");
            p.Peer!.Disconnect(_reason);
            Log($"kicked {p.Name} (#{p.Id}, {p.Peer.Address}): {reason}");
        }
        return $"kicked {hits.Count} player(s)";
    }

    public string Ban(string target, string reason)
    {
        var hits = Resolve(target);
        var ips = hits.Select(p => p.Peer!.Address.ToString()).Distinct().ToList();
        if (ips.Count == 0)
        {
            if (!BanList.IsIp(target)) return $"no connected player matches '{target}'; to ban an offline player give their IP (see the join log)";
            ips.Add(target);
        }
        foreach (var ip in ips) Bans.Add(ip, reason);
        foreach (var p in hits)
        {
            _reason.Reset(); _reason.Put($"Banned: {reason}");
            p.Peer!.Disconnect(_reason);
            Log($"banned {p.Name} (#{p.Id}, {p.Peer.Address}): {reason}");
        }
        if (hits.Count == 0) Log($"banned {target}: {reason}");
        return $"banned {string.Join(", ", ips)} ({Bans.Count} total)";
    }

    public string Unban(string ip) => Bans.Remove(ip) ? $"unbanned {ip}" : $"{ip} was not banned";

    public void Stop() => _net.Stop();

    public void Poll()
    {
        _net.PollEvents();
        TickBots();
    }

    // ---- rules -------------------------------------------------------------------------------

    public void SetTraffic(bool on) { Traffic = on; BroadcastSettings(); Log($"traffic {(on ? "on" : "off")}"); }
    public void SetCollisions(bool on) { Collisions = on; BroadcastSettings(); Log($"collisions {(on ? "on" : "off")}"); }

    private void BroadcastSettings()
    {
        if (_byPeer.Count == 0) return;
        WriteSettings();
        _net.SendToAll(_w, DeliveryMethod.ReliableOrdered);
    }

    private void WriteSettings()
    {
        _w.Reset(); _w.Put(Protocol.Settings); _w.Put(Traffic); _w.Put(Collisions);
    }

    // ---- bots --------------------------------------------------------------------------------

    public void AddBot(int model = 33)
    {
        var index = BotCount;
        var p = new Player { Id = _nextId++, Name = $"Bot{index + 1}", Model = model, Bot = new Bot(index, model) };
        _byId[p.Id] = p;
        _w.Reset(); _w.Put(Protocol.PlayerJoined); WritePlayerInfo(p);
        _net.SendToAll(_w, DeliveryMethod.ReliableOrdered);
        Log($"added {p.Name} (#{p.Id}, model {model})");
    }

    public void RemoveBots()
    {
        foreach (var p in _byId.Values.Where(p => p.Bot != null).ToList())
        {
            _byId.Remove(p.Id);
            _w.Reset(); _w.Put(Protocol.PlayerLeft); _w.Put(p.Id);
            _net.SendToAll(_w, DeliveryMethod.ReliableOrdered);
        }
        Log("bots removed");
    }

    private void TickBots()
    {
        var now = SendRate.Now;
        if (now < _nextBotTick) return;
        _nextBotTick = now + 0.04f; // 25 Hz, paced further per recipient by distance

        var anchor = _byPeer.Values.FirstOrDefault(p => p.HasPos);
        if (anchor == null) return; // bots idle until someone real is on the road

        foreach (var bot in _byId.Values)
        {
            if (bot.Bot == null) continue;
            _w.Reset(); _w.Put(Protocol.State); _w.Put(bot.Id);
            bot.Bot.WriteState(_w, now, anchor.Pos);
            bot.Pos = Vec3.FromBytes(_w.Data, 1 + 4 + Protocol.PosOffset);
            bot.HasPos = true;

            foreach (var kv in _byPeer)
            {
                var dist = kv.Value.HasPos ? Vec3.Distance(bot.Pos, kv.Value.Pos) : 0f;
                if (kv.Value.GateFor(bot.Id).ShouldSend(now, dist, FullRateHz)) kv.Key.Send(_w, DeliveryMethod.Unreliable);
            }
        }
    }

    // ---- packets -----------------------------------------------------------------------------

    private void OnReceive(NetPeer peer, NetPacketReader reader)
    {
        if (reader.AvailableBytes < 1) return;
        var type = reader.GetByte();
        switch (type)
        {
            case Protocol.Hello:
            {
                if (_byPeer.ContainsKey(peer)) return; // one identity per connection
                var p = new Player
                {
                    Id = _nextId++,
                    Name = Protocol.SanitizeName(reader.GetString(Protocol.MaxNameLength * 4)),
                    Model = reader.GetInt(),
                    Peer = peer,
                };
                if (p.Model < 0 || p.Model > 1000) p.Model = 0;
                _byPeer[peer] = p;
                _byId[p.Id] = p;
                Log($"{p.Name} (#{p.Id}, model {p.Model}) joined from {peer.Address}. {PlayerCount} player(s) online");

                // Welcome: newcomer's id, then everyone already here (players and bots).
                var others = _byId.Values.Where(o => o.Id != p.Id).ToList();
                _w.Reset(); _w.Put(Protocol.Welcome); _w.Put(p.Id); _w.Put(others.Count);
                foreach (var o in others) WritePlayerInfo(o);
                peer.Send(_w, DeliveryMethod.ReliableOrdered);

                WriteSettings();
                peer.Send(_w, DeliveryMethod.ReliableOrdered);

                _w.Reset(); _w.Put(Protocol.PlayerJoined); WritePlayerInfo(p);
                _net.SendToAll(_w, DeliveryMethod.ReliableOrdered, peer);
                break;
            }
            case Protocol.State:
            {
                if (!_byPeer.TryGetValue(peer, out var sender)) return;
                if (reader.AvailableBytes != 4 + Protocol.CarStateSize) { sender.Dropped++; return; } // exact size only

                // Incoming rate cap: protects CPU and stops a client from using us as an amplifier.
                var now = SendRate.Now;
                if (now - sender.RateWindowStart >= 1f) { sender.RateWindowStart = now; sender.RateWindowCount = 0; }
                if (++sender.RateWindowCount > IncomingPacketsPerSecondLimit) { sender.Dropped++; return; }

                reader.GetInt(); // id claimed by the client; the server is authoritative
                var raw = reader.GetRemainingBytes();
                if (!Protocol.ValidateCarState(raw)) { sender.Dropped++; return; }
                sender.Pos = Vec3.FromBytes(raw, Protocol.PosOffset);
                sender.HasPos = true;
                sender.Packets++;

                var written = false;
                foreach (var kv in _byPeer)
                {
                    if (kv.Key == peer) continue;
                    var c = kv.Value;
                    var dist = c.HasPos ? Vec3.Distance(sender.Pos, c.Pos) : 0f;
                    if (!c.GateFor(sender.Id).ShouldSend(now, dist, FullRateHz)) continue;
                    if (!written) { _w.Reset(); _w.Put(Protocol.State); _w.Put(sender.Id); _w.Put(raw); written = true; }
                    kv.Key.Send(_w, DeliveryMethod.Unreliable);
                }
                break;
            }
            // Clients may not send Welcome/PlayerJoined/PlayerLeft/Settings; anything else is ignored.
        }
    }

    private void WritePlayerInfo(Player p) { _w.Put(p.Id); _w.Put(p.Name); _w.Put(p.Model); }

    // ---- console -----------------------------------------------------------------------------

    public string Describe()
    {
        if (_byId.Count == 0) return "nobody online";
        var lines = _byId.Values.OrderBy(p => p.Id).Select(p =>
            $"  #{p.Id,-3} {p.Name,-24} model {p.Model,-3} " +
            (p.Bot != null ? "bot" : $"{p.Peer!.Address}  ping {p.Peer.Ping} ms  {p.Packets} pkts, {p.Dropped} dropped") +
            (p.HasPos ? $"  @ ({p.Pos.X:F0}, {p.Pos.Y:F0}, {p.Pos.Z:F0})" : "  (no position yet)"));
        return string.Join('\n', lines);
    }

    public static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
