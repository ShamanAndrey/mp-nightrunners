// Health check for a Night Runners MP host or relay server:
// performs the real handshake (connect key + Hello) and reports Welcome, rules and who is online.
//   dotnet run --project tools/nrmp-ping -- <host> [port] [password] [--fuzz]
// --fuzz: after the handshake, send a battery of malformed/hostile packets and verify the
//         connection (and the server) survives. Exit code 0 = healthy.
using System.Diagnostics;
using LiteNetLib;
using LiteNetLib.Utils;

var positional = args.Where(a => !a.StartsWith("--")).ToArray();
var fuzz = args.Contains("--fuzz");
var host = positional.Length > 0 ? positional[0] : "127.0.0.1";
var port = positional.Length > 1 ? int.Parse(positional[1]) : 7777;
var password = positional.Length > 2 ? positional[2] : "";
var key = password.Length > 0 ? $"NRMP-0.4|{password}" : "NRMP-0.4";

var listener = new EventBasedNetListener();
var net = new NetManager(listener) { AutoRecycle = true };
var done = false;
var welcomed = false;
var rulesSeen = false;
var sw = Stopwatch.StartNew();
NetPeer? server = null;

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
    Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] disconnected: {info.Reason}" + (welcomed ? "" : "  <-- handshake failed (wrong version/password, server full, or unreachable)"));
    done = true;
};
listener.NetworkReceiveEvent += (peer, reader, _, _) =>
{
    var type = reader.GetByte();
    switch (type)
    {
        case 2:
            welcomed = true;
            var myId = reader.GetInt();
            var n = reader.GetInt();
            Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] WELCOME: I am #{myId}, {n} other player(s) online, ping {peer.Ping} ms");
            for (var i = 0; i < n; i++)
                Console.WriteLine($"    #{reader.GetInt()} {reader.GetString()} (model {reader.GetInt()})");
            break;
        case 6:
            rulesSeen = true;
            Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] RULES: traffic {(reader.GetBool() ? "on" : "off")}, collisions {(reader.GetBool() ? "on" : "off")}");
            if (!fuzz) { Console.WriteLine("OK — server is reachable and speaks the current protocol."); peer.Disconnect(); }
            break;
        case 3:
            Console.WriteLine($"    joined: #{reader.GetInt()} {reader.GetString()} (model {reader.GetInt()})");
            break;
        case 5:
            break; // state relay (other players / bots)
    }
};

net.Start();
net.Connect(host, port, key);
Console.WriteLine($"connecting to {host}:{port} (key {(password.Length > 0 ? "NRMP-0.4|***" : key)}) ...");

var fuzzStarted = false;
var fuzzEndsAt = long.MaxValue;
while (!done && sw.ElapsedMilliseconds < 12000)
{
    net.PollEvents();

    if (fuzz && rulesSeen && !fuzzStarted && server != null)
    {
        fuzzStarted = true;
        Console.WriteLine("--- fuzz: sending hostile packets ---");
        var w = new NetDataWriter();

        void Send(string label, Action<NetDataWriter> build, DeliveryMethod dm = DeliveryMethod.Unreliable)
        {
            w.Reset(); build(w); try { server.Send(w, dm); if (label.Length > 0) Console.WriteLine($"    sent {label} ({w.Length} bytes)"); } catch (Exception e) { Console.WriteLine($"    could not send {label}: {e.GetType().Name} (library refuses)"); }
        }

        Send("duplicate Hello", x => { x.Put((byte)1); x.Put("<size=200>evil</size>"); x.Put(7); }, DeliveryMethod.ReliableOrdered);
        Send("Hello with 60 KB name", x => { x.Put((byte)1); x.Put(new string('A', 60000)); x.Put(1); }, DeliveryMethod.ReliableOrdered);
        Send("truncated State", x => { x.Put((byte)5); x.Put(1); x.Put(1.0f); x.Put(2.0f); });
        Send("empty packet", x => { });
        Send("unknown type 200", x => { x.Put((byte)200); x.Put(12345); });
        Send("client-sent Settings", x => { x.Put((byte)6); x.Put(true); x.Put(true); }, DeliveryMethod.ReliableOrdered);
        Send("client-sent PlayerLeft #1", x => { x.Put((byte)4); x.Put(1); }, DeliveryMethod.ReliableOrdered);
        Send("State with NaN position", x => { x.Put((byte)5); x.Put(1); x.Put(0f); x.Put(float.NaN); x.Put(0f); x.Put(0f); for (var i = 0; i < 4; i++) x.Put(i == 3 ? 1f : 0f); for (var i = 0; i < 11; i++) x.Put(0f); x.Put((sbyte)0); x.Put((byte)0); });
        Send("State with 1e30 position", x => { x.Put((byte)5); x.Put(1); x.Put(0f); x.Put(1e30f); x.Put(0f); x.Put(0f); for (var i = 0; i < 4; i++) x.Put(i == 3 ? 1f : 0f); for (var i = 0; i < 11; i++) x.Put(0f); x.Put((sbyte)0); x.Put((byte)0); });
        Send("oversized State (1200 bytes, reliable/fragmented)", x => { x.Put((byte)5); x.Put(1); x.Put(new byte[1195]); }, DeliveryMethod.ReliableOrdered);
        for (var i = 0; i < 300; i++) // burst well above the 100/s cap
            Send(i == 0 ? "valid State x300 burst" : "", x => { x.Put((byte)5); x.Put(1); x.Put(1f); x.Put(10f); x.Put(0f); x.Put(10f); for (var k = 0; k < 4; k++) x.Put(k == 3 ? 1f : 0f); for (var k = 0; k < 11; k++) x.Put(0f); x.Put((sbyte)1); x.Put((byte)16); });
        fuzzEndsAt = sw.ElapsedMilliseconds + 2500;
    }

    if (fuzz && fuzzStarted && sw.ElapsedMilliseconds >= fuzzEndsAt)
    {
        var alive = server != null && server.ConnectionState == ConnectionState.Connected;
        Console.WriteLine(alive
            ? "FUZZ OK — still connected after the hostile batch (server did not crash or drop us)."
            : "FUZZ: connection lost during the batch (a kick for malformed input is acceptable if the server itself is still up).");
        server?.Disconnect();
        done = true;
        welcomed = welcomed && alive;
    }

    Thread.Sleep(5);
}
if (!done) Console.WriteLine("TIMEOUT — no answer: wrong address, server not running, or UDP port blocked by a firewall (OS or provider panel).");
net.Stop();
return welcomed ? 0 : 1;
