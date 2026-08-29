using LiteNetLib;
using LiteNetLib.Utils;

namespace NightRunnersMP.Server;

/// <summary>
/// Dedicated relay: the same role the in-game host plays, minus a car of its own.
/// Assigns ids, keeps the player list, relays snapshots paced by distance, owns the session rules.
/// </summary>
public sealed class RelayServer
{
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
    private int _nextId = 1;
    private float _nextBotTick;

    public int Port { get; }
    public int MaxPlayers { get; set; } = 32;
    public float FullRateHz { get; set; } = 25f;
    public bool Traffic { get; private set; } = true;
    public bool Collisions { get; private set; }

    public int PlayerCount => _byPeer.Count;
    public int BotCount => _byId.Values.Count(p => p.Bot != null);

    public RelayServer(int port, bool traffic, bool collisions)
    {
        Port = port;
        Traffic = traffic;
        Collisions = collisions;
        _net = new NetManager(_listener) { AutoRecycle = true, DisconnectTimeout = 10000 };

        _listener.ConnectionRequestEvent += req =>
        {
            if (_byPeer.Count >= MaxPlayers) { req.Reject(); return; }
            req.AcceptIfKey(Protocol.Key);
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
        _listener.NetworkReceiveEvent += (peer, reader, _, _) => OnReceive(peer, reader);
    }

    public bool Start()
    {
        var ok = _net.Start(Port);
        Log(ok ? $"listening on UDP {Port} (protocol {Protocol.Key}, traffic {(Traffic ? "on" : "off")}, collisions {(Collisions ? "on" : "off")}, max {MaxPlayers})"
               : $"FAILED to bind UDP {Port}");
        return ok;
    }

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
                var p = new Player { Id = _nextId++, Name = reader.GetString(), Model = reader.GetInt(), Peer = peer };
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = $"Player{p.Id}";
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
                if (reader.AvailableBytes < 4 + Protocol.CarStateSize) return;
                reader.GetInt(); // id claimed by the client; the server is authoritative
                var raw = reader.GetRemainingBytes();
                sender.Pos = Vec3.FromBytes(raw, Protocol.PosOffset);
                sender.HasPos = true;
                sender.Packets++;

                var now = SendRate.Now;
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
        }
    }

    private void WritePlayerInfo(Player p) { _w.Put(p.Id); _w.Put(p.Name); _w.Put(p.Model); }

    // ---- console -----------------------------------------------------------------------------

    public string Describe()
    {
        if (_byId.Count == 0) return "nobody online";
        var lines = _byId.Values.OrderBy(p => p.Id).Select(p =>
            $"  #{p.Id,-3} {p.Name,-16} model {p.Model,-3} {(p.Bot != null ? "bot" : $"{p.Peer!.Address}  ping {p.Peer.Ping} ms  {p.Packets} pkts")}" +
            (p.HasPos ? $"  @ ({p.Pos.X:F0}, {p.Pos.Y:F0}, {p.Pos.Z:F0})" : "  (no position yet)"));
        return string.Join('\n', lines);
    }

    public static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
