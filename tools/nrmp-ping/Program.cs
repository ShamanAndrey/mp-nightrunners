// Health check for a Night Runners MP host or relay server:
// performs the real handshake (connect key + Hello) and reports Welcome, rules and who is online.
//   dotnet run --project tools/nrmp-ping -- <host> [port] [password] [--fuzz] [--say "text"] [--wait ms]
// --fuzz: after the handshake, send a battery of malformed/hostile packets and verify the
//         connection (and the server) survives. Exit code 0 = healthy.
// --say:  send one chat line after the handshake.   --wait: stay connected N ms and print chat.
using System.Diagnostics;
using LiteNetLib;
using LiteNetLib.Utils;

const string Protocol = "NRMP-0.5";

string? say = null;
var waitMs = 0;
var fuzz = false;
var positional = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--fuzz": fuzz = true; break;
        case "--say": say = i + 1 < args.Length ? args[++i] : null; break;
        case "--wait": waitMs = i + 1 < args.Length ? int.Parse(args[++i]) : 0; break;
        default: positional.Add(args[i]); break;
    }
}
var host = positional.Count > 0 ? positional[0] : "127.0.0.1";
var port = positional.Count > 1 ? int.Parse(positional[1]) : 7777;
var password = positional.Count > 2 ? positional[2] : "";
var key = password.Length > 0 ? $"{Protocol}|{password}" : Protocol;

var listener = new EventBasedNetListener();
var net = new NetManager(listener) { AutoRecycle = true };
var done = false;
var welcomed = false;
var rulesSeen = false;
var sw = Stopwatch.StartNew();
NetPeer? server = null;
var holdUntil = long.MaxValue;

listener.PeerConnectedEvent += peer =>
{
    server = peer;
    Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] connected to {host}:{port}, sending Hello");
    var w = new NetDataWriter();
    w.Put((byte)1); w.Put("nrmp-ping"); w.Put(0);
    peer.Send(w, DeliveryMethod.ReliableOrdered);
};
listener.PeerDisconnectedEvent += (_, info) =>
{
    string? msg = null;
    try { if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0) msg = info.AdditionalData.GetString(200); } catch { }
    if (msg != null) Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] server says: {msg}");
    Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] disconnected: {info.Reason}" + (welcomed ? "" : "  <-- handshake failed (wrong version/password, server full, banned, or unreachable)"));
    done = true;
};
listener.NetworkReceiveEvent += (peer, reader, _, _) =>
{
    var type = reader.GetByte();
    switch (type)
    {
        case 2:
        {
            welcomed = true;
            var myId = reader.GetInt();
            var n = reader.GetInt();
            Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] WELCOME: I am #{myId}, {n} other player(s) online, ping {peer.Ping} ms");
            for (var i = 0; i < n; i++)
                Console.WriteLine($"    #{reader.GetInt()} {reader.GetString()} (model {reader.GetInt()})");
            break;
        }
        case 6:
        {
            rulesSeen = true;
            Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] RULES: traffic {(reader.GetBool() ? "on" : "off")}, collisions {(reader.GetBool() ? "on" : "off")}");
            if (say != null)
            {
                var cw = new NetDataWriter();
                cw.Put((byte)7); cw.Put(0); cw.Put(say);
                peer.Send(cw, DeliveryMethod.ReliableOrdered);
                Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] sent chat: {say}");
            }
            if (fuzz) break;
            if (waitMs > 0) holdUntil = sw.ElapsedMilliseconds + waitMs;
            else { Console.WriteLine("OK - server is reachable and speaks the current protocol."); peer.Disconnect(); }
            break;
        }
        case 3:
            Console.WriteLine($"    joined: #{reader.GetInt()} {reader.GetString()} (model {reader.GetInt()})");
            break;
        case 4:
            Console.WriteLine($"    left: #{reader.GetInt()}");
            break;
        case 7:
        {
            var from = reader.GetInt();
            var text = reader.GetString();
            Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] CHAT {(from == -1 ? "SERVER" : $"#{from}")}: {text}");
            break;
        }
        case 5:
            break; // state relay (other players / bots)
    }
};

net.Start();
net.Connect(host, port, key);
Console.WriteLine($"connecting to {host}:{port} (key {(password.Length > 0 ? Protocol + "|***" : key)}) ...");

var fuzzStarted = false;
var fuzzEndsAt = long.MaxValue;
var deadline = 12000 + waitMs;
while (!done && sw.ElapsedMilliseconds < deadline)
{
    net.PollEvents();

    if (sw.ElapsedMilliseconds >= holdUntil)
    {
        holdUntil = long.MaxValue;
        Console.WriteLine("OK - done waiting.");
        server?.Disconnect();
    }

    if (fuzz && rulesSeen && !fuzzStarted && server != null)
    {
        fuzzStarted = true;
        Console.WriteLine("--- fuzz: sending hostile packets ---");
        var w = new NetDataWriter();

        void Send(string label, Action<NetDataWriter> build, DeliveryMethod dm = DeliveryMethod.Unreliable)
        {
            w.Reset(); build(w);
            try { server.Send(w, dm); if (label.Length > 0) Console.WriteLine($"    sent {label} ({w.Length} bytes)"); }
            catch (Exception e) { Console.WriteLine($"    could not send {label}: {e.GetType().Name} (library refuses)"); }
        }

        Send("duplicate Hello", x => { x.Put((byte)1); x.Put("<size=200>evil</size>"); x.Put(7); }, DeliveryMethod.ReliableOrdered);
        Send("Hello with 60 KB name", x => { x.Put((byte)1); x.Put(new string('A', 60000)); x.Put(1); }, DeliveryMethod.ReliableOrdered);
        Send("truncated State", x => { x.Put((byte)5); x.Put(1); x.Put(1.0f); x.Put(2.0f); });
        Send("empty packet", x => { });
        Send("unknown type 200", x => { x.Put((byte)200); x.Put(12345); });
        Send("client-sent Settings", x => { x.Put((byte)6); x.Put(true); x.Put(true); }, DeliveryMethod.ReliableOrdered);
        Send("client-sent PlayerLeft #1", x => { x.Put((byte)4); x.Put(1); }, DeliveryMethod.ReliableOrdered);
        Send("chat with markup + 5 KB", x => { x.Put((byte)7); x.Put(0); x.Put("<color=red>" + new string('Z', 5000)); }, DeliveryMethod.ReliableOrdered);
        Send("State with NaN position", x => { x.Put((byte)5); x.Put(1); x.Put(0f); x.Put(float.NaN); x.Put(0f); x.Put(0f); for (var i = 0; i < 4; i++) x.Put(i == 3 ? 1f : 0f); for (var i = 0; i < 11; i++) x.Put(0f); x.Put((sbyte)0); x.Put((byte)0); });
        Send("State with 1e30 position", x => { x.Put((byte)5); x.Put(1); x.Put(0f); x.Put(1e30f); x.Put(0f); x.Put(0f); for (var i = 0; i < 4; i++) x.Put(i == 3 ? 1f : 0f); for (var i = 0; i < 11; i++) x.Put(0f); x.Put((sbyte)0); x.Put((byte)0); });
        Send("oversized State (1200 bytes, reliable/fragmented)", x => { x.Put((byte)5); x.Put(1); x.Put(new byte[1195]); }, DeliveryMethod.ReliableOrdered);
        for (var i = 0; i < 300; i++) // burst well above the 100/s cap
            Send(i == 0 ? "valid State x300 burst" : "", x => { x.Put((byte)5); x.Put(1); x.Put(1f); x.Put(10f); x.Put(0f); x.Put(10f); for (var k = 0; k < 4; k++) x.Put(k == 3 ? 1f : 0f); for (var k = 0; k < 11; k++) x.Put(0f); x.Put((sbyte)1); x.Put((byte)16); });
        for (var i = 0; i < 10; i++) // chat flood: only 3 per 2 s should pass
        {
            var n = i;
            Send(i == 0 ? "chat x10 flood" : "", x => { x.Put((byte)7); x.Put(0); x.Put($"spam {n}"); }, DeliveryMethod.ReliableOrdered);
        }
        fuzzEndsAt = sw.ElapsedMilliseconds + 2500;
    }

    if (fuzz && fuzzStarted && sw.ElapsedMilliseconds >= fuzzEndsAt)
    {
        var alive = server != null && server.ConnectionState == ConnectionState.Connected;
        Console.WriteLine(alive
            ? "FUZZ OK - still connected after the hostile batch (server did not crash or drop us)."
            : "FUZZ: connection lost during the batch (a kick for malformed input is acceptable if the server itself is still up).");
        server?.Disconnect();
        done = true;
        welcomed = welcomed && alive;
    }

    Thread.Sleep(5);
}
if (!done) Console.WriteLine("TIMEOUT - no answer: wrong address, server not running, or UDP port blocked by a firewall (OS or provider panel).");
net.Stop();
return welcomed ? 0 : 1;
