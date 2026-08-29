using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using NightRunnersMP.Sync;
using UnityEngine;

namespace NightRunnersMP.Net;

public abstract class NetSession
{
    public event Action<PlayerInfo>? PlayerJoined;
    public event Action<int>? PlayerLeft;
    public event Action<int, CarState>? StateReceived;

    protected readonly Action<string> Log;
    protected readonly EventBasedNetListener Listener = new();
    protected readonly NetManager Net;
    protected readonly NetDataWriter Writer = new();
    protected readonly string Key;

    protected NetSession(Action<string> log, string? password)
    {
        Log = log;
        Key = Wire.KeyFor(password);
        Net = new NetManager(Listener)
        {
            AutoRecycle = true,
            DisconnectTimeout = 10000,
            UnconnectedMessagesEnabled = false,
            BroadcastReceiveEnabled = false,
        };
        Listener.NetworkReceiveEvent += (peer, reader, _, _) =>
        {
            // Anything a remote sends is untrusted: a malformed packet must never take us down.
            try { OnReceive(peer, reader); }
            catch (Exception e)
            {
                Log($"[net] dropped malformed packet from peer {peer.Id}: {e.GetType().Name}");
                OnMalformed(peer);
            }
        };
    }

    public abstract string Status { get; }
    public abstract void SendState(in CarState state);
    protected abstract void OnReceive(NetPeer peer, NetPacketReader reader);
    protected virtual void OnMalformed(NetPeer peer) { }

    // Events are dispatched here, on the game thread, so handlers may touch Unity objects.
    public void Poll() => Net.PollEvents();
    public virtual void Stop() => Net.Stop();

    protected void RaiseJoined(PlayerInfo p) => PlayerJoined?.Invoke(p);
    protected void RaiseLeft(int id) => PlayerLeft?.Invoke(id);
    protected void RaiseState(int id, in CarState s) => StateReceived?.Invoke(id, s);
}

/// <summary>
/// Listens for clients, owns player id 0, relays every client's state to the others.
/// Every (sender → recipient) stream is paced by the distance between the two cars.
/// </summary>
public sealed class HostSession : NetSession
{
    private const float IncomingPacketsPerSecondLimit = 100f; // a client at full rate sends 25
    private const float ConnectCooldownPerAddress = 2f;       // seconds; slows reconnect spam

    private sealed class Client
    {
        public PlayerInfo Info;
        public Vector3 Pos;
        public bool HasPos;
        public float RateWindowStart;
        public int RateWindowCount;
        public readonly Dictionary<int, RateGate> GatesBySender = new(); // sender id -> pacing towards this client

        public RateGate GateFor(int senderId)
        {
            if (!GatesBySender.TryGetValue(senderId, out var g)) GatesBySender[senderId] = g = new RateGate();
            return g;
        }
    }

    private readonly Dictionary<NetPeer, Client> _players = new();
    private readonly Dictionary<string, float> _lastConnectByAddress = new();
    private readonly int _port;
    private PlayerInfo _self;
    private int _nextId = 1;

    public float FullRateHz = 25f;
    public bool HasPassword { get; }

    public HostSession(int port, PlayerInfo self, Action<string> log, string? password = null) : base(log, password)
    {
        _port = port;
        _self = self;
        _self.Id = 0;
        HasPassword = !string.IsNullOrEmpty(password);

        Listener.ConnectionRequestEvent += req =>
        {
            if (_players.Count >= Wire.MaxPlayers) { req.Reject(); return; }
            var addr = req.RemoteEndPoint.Address.ToString();
            var now = SendRate.Now;
            if (_lastConnectByAddress.TryGetValue(addr, out var last) && now - last < ConnectCooldownPerAddress) { req.Reject(); return; }
            _lastConnectByAddress[addr] = now;
            req.AcceptIfKey(Key);
        };
        Listener.PeerConnectedEvent += peer => Log($"[host] peer {peer.Id} connected, waiting for Hello");
        Listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (!_players.Remove(peer, out var c)) return;
            Log($"[host] {c.Info.Name} (#{c.Info.Id}) left: {info.Reason}");
            Writer.Reset(); Writer.Put((byte)PacketType.PlayerLeft); Writer.Put(c.Info.Id);
            Net.SendToAll(Writer, DeliveryMethod.ReliableOrdered);
            RaiseLeft(c.Info.Id);
        };
    }

    public bool Start()
    {
        var ok = Net.Start(_port);
        Log(ok ? $"[host] listening on UDP {_port}{(HasPassword ? " (password protected)" : "")}" : $"[host] FAILED to bind UDP {_port}");
        return ok;
    }

    public override string Status => Net.IsRunning ? $"hosting on UDP {_port}, {_players.Count} client(s){(HasPassword ? ", password" : "")}" : "stopped";

    public void UpdateSelfModel(int model) => _self.Model = model;

    /// <summary>Session rules owned by the host; clients mirror them.</summary>
    public bool TrafficEnabled { get; set; } = true;
    public bool CollisionsEnabled { get; set; }

    public void BroadcastSettings()
    {
        if (_players.Count == 0) return;
        WriteSettings();
        Net.SendToAll(Writer, DeliveryMethod.ReliableOrdered);
    }

    private void SendSettingsTo(NetPeer peer)
    {
        WriteSettings();
        peer.Send(Writer, DeliveryMethod.ReliableOrdered);
    }

    private void WriteSettings()
    {
        Writer.Reset();
        Writer.Put((byte)PacketType.Settings);
        Writer.Put(TrafficEnabled);
        Writer.Put(CollisionsEnabled);
    }

    public override void SendState(in CarState state)
    {
        if (_players.Count == 0) return;
        var now = SendRate.Now;
        var written = false;
        foreach (var kv in _players)
        {
            var c = kv.Value;
            var dist = c.HasPos ? Vector3.Distance(state.Pos, c.Pos) : 0f;
            if (!c.GateFor(0).ShouldSend(now, dist, FullRateHz)) continue;
            if (!written) { WriteState(0, state); written = true; }
            kv.Key.Send(Writer, DeliveryMethod.Unreliable);
        }
    }

    private void WriteState(int id, in CarState state)
    {
        Writer.Reset(); Writer.Put((byte)PacketType.State); Writer.Put(id); state.Write(Writer);
    }

    protected override void OnMalformed(NetPeer peer) => peer.Disconnect();

    protected override void OnReceive(NetPeer peer, NetPacketReader reader)
    {
        var type = (PacketType)reader.GetByte();
        switch (type)
        {
            case PacketType.Hello:
            {
                if (_players.ContainsKey(peer)) return; // one identity per connection
                var info = new PlayerInfo
                {
                    Id = _nextId++,
                    Name = Wire.SanitizeName(reader.GetString(Wire.MaxNameLength * 4)),
                    Model = reader.GetInt(),
                };
                _players[peer] = new Client { Info = info };
                Log($"[host] {info.Name} joined as #{info.Id} (model {info.Model})");

                // Welcome: newcomer's id, then everyone already present (host first, then other clients).
                Writer.Reset(); Writer.Put((byte)PacketType.Welcome); Writer.Put(info.Id);
                Writer.Put(_players.Count); // == 1 (host) + (clients - newcomer)
                _self.Write(Writer);
                foreach (var kv in _players) if (kv.Key != peer) kv.Value.Info.Write(Writer);
                peer.Send(Writer, DeliveryMethod.ReliableOrdered);
                SendSettingsTo(peer);

                Writer.Reset(); Writer.Put((byte)PacketType.PlayerJoined); info.Write(Writer);
                Net.SendToAll(Writer, DeliveryMethod.ReliableOrdered, peer);
                RaiseJoined(info);
                break;
            }
            case PacketType.State:
            {
                if (!_players.TryGetValue(peer, out var sender)) return;
                if (reader.AvailableBytes != 4 + CarState.Size) return; // exact size only

                // Incoming rate cap: protects CPU and stops a client from using us as an amplifier.
                var now = SendRate.Now;
                if (now - sender.RateWindowStart >= 1f) { sender.RateWindowStart = now; sender.RateWindowCount = 0; }
                if (++sender.RateWindowCount > IncomingPacketsPerSecondLimit) return;

                reader.GetInt(); // id claimed by the client is ignored; the host is authoritative
                var s = CarState.Read(reader);
                if (!s.Validate()) return;
                sender.Pos = s.Pos;
                sender.HasPos = true;
                RaiseState(sender.Info.Id, s);

                // Relay to every other client, each paced by its distance to the sender.
                var written = false;
                foreach (var kv in _players)
                {
                    if (kv.Key == peer) continue;
                    var c = kv.Value;
                    var dist = c.HasPos ? Vector3.Distance(s.Pos, c.Pos) : 0f;
                    if (!c.GateFor(sender.Info.Id).ShouldSend(now, dist, FullRateHz)) continue;
                    if (!written) { WriteState(sender.Info.Id, s); written = true; }
                    kv.Key.Send(Writer, DeliveryMethod.Unreliable);
                }
                break;
            }
            // Anything else from a client is ignored.
        }
    }
}

/// <summary>Connects to a host and mirrors its player list.</summary>
public sealed class ClientSession : NetSession
{
    private readonly string _address;
    private readonly int _port;
    private readonly PlayerInfo _self;
    private NetPeer? _server;

    public int MyId { get; private set; } = -1;
    public bool Connected => _server != null && _server.ConnectionState == ConnectionState.Connected;
    public string? LastDisconnectReason { get; private set; }

    /// <summary>Raised with the host's rules (traffic, collisions) whenever the host sends them.</summary>
    public event Action<bool, bool>? RulesReceived;

    public ClientSession(string address, int port, PlayerInfo self, Action<string> log, string? password = null) : base(log, password)
    {
        _address = address; _port = port; _self = self;

        Listener.PeerConnectedEvent += peer =>
        {
            Log($"[client] connected to {_address}:{_port}, sending Hello");
            Writer.Reset(); Writer.Put((byte)PacketType.Hello); Writer.Put(_self.Name); Writer.Put(_self.Model);
            peer.Send(Writer, DeliveryMethod.ReliableOrdered);
        };
        Listener.PeerDisconnectedEvent += (_, info) =>
        {
            LastDisconnectReason = info.Reason.ToString();
            // Servers attach a human-readable message when they kick, ban, or reject us.
            string? message = null;
            try
            {
                if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0)
                    message = Wire.SanitizeName(info.AdditionalData.GetString(200));
            }
            catch { /* not ours to worry about */ }
            var hint = message != null ? $" — {message}"
                     : info.Reason == DisconnectReason.ConnectionRejected ? " (version or password mismatch, or server full)" : "";
            Log($"[client] disconnected: {info.Reason}{hint}");
            _server = null;
        };
    }

    public void Start()
    {
        Net.Start();
        _server = Net.Connect(_address, _port, Key);
        Log($"[client] connecting to {_address}:{_port} ...");
    }

    public override string Status => Connected ? $"connected to {_address}:{_port} as #{MyId}, ping {_server!.Ping} ms" : "not connected";

    public override void SendState(in CarState state)
    {
        if (!Connected) return;
        Writer.Reset(); Writer.Put((byte)PacketType.State); Writer.Put(MyId); state.Write(Writer);
        _server!.Send(Writer, DeliveryMethod.Unreliable);
    }

    protected override void OnReceive(NetPeer peer, NetPacketReader reader)
    {
        var type = (PacketType)reader.GetByte();
        switch (type)
        {
            case PacketType.Welcome:
            {
                MyId = reader.GetInt();
                var n = reader.GetInt();
                if (n < 0 || n > Wire.MaxPlayers) throw new InvalidOperationException("bad player count");
                Log($"[client] welcomed as #{MyId}, {n} player(s) already here");
                for (var i = 0; i < n; i++) RaiseJoined(PlayerInfo.Read(reader));
                break;
            }
            case PacketType.PlayerJoined:
                RaiseJoined(PlayerInfo.Read(reader));
                break;
            case PacketType.PlayerLeft:
                RaiseLeft(reader.GetInt());
                break;
            case PacketType.State:
            {
                if (reader.AvailableBytes != 4 + CarState.Size) return;
                var id = reader.GetInt();
                var s = CarState.Read(reader);
                if (id == MyId || !s.Validate()) return;
                RaiseState(id, s);
                break;
            }
            case PacketType.Settings:
            {
                var traffic = reader.GetBool();
                var collisions = reader.GetBool();
                RulesReceived?.Invoke(traffic, collisions);
                break;
            }
        }
    }
}
