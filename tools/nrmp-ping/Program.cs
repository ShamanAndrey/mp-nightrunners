// Health check for a Night Runners MP host or relay server:
// performs the real handshake (connect key + Hello) and reports Welcome, rules and who is online.
//   dotnet run --project tools/nrmp-ping -- 168.231.107.135 7777
using System.Diagnostics;
using LiteNetLib;
using LiteNetLib.Utils;

const string Key = "NRMP-0.4";
var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 ? int.Parse(args[1]) : 7777;

var listener = new EventBasedNetListener();
var net = new NetManager(listener) { AutoRecycle = true };
var done = false;
var welcomed = false;
var sw = Stopwatch.StartNew();

listener.PeerConnectedEvent += peer =>
{
    Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] connected to {host}:{port}, sending Hello");
    var w = new NetDataWriter();
    w.Put((byte)1); w.Put("nrmp-ping"); w.Put(0);
    peer.Send(w, DeliveryMethod.ReliableOrdered);
};
listener.PeerDisconnectedEvent += (_, info) =>
{
    Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] disconnected: {info.Reason}" + (welcomed ? "" : "  <-- handshake failed"));
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
            Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] RULES: traffic {(reader.GetBool() ? "on" : "off")}, collisions {(reader.GetBool() ? "on" : "off")}");
            Console.WriteLine("OK — server is reachable and speaks the current protocol.");
            peer.Disconnect();
            break;
        case 3:
            Console.WriteLine($"    joined: #{reader.GetInt()} {reader.GetString()} (model {reader.GetInt()})");
            break;
    }
};

net.Start();
net.Connect(host, port, Key);
Console.WriteLine($"connecting to {host}:{port} (key {Key}) ...");
while (!done && sw.ElapsedMilliseconds < 8000) { net.PollEvents(); Thread.Sleep(15); }
if (!done) Console.WriteLine("TIMEOUT — no answer in 8 s: wrong address, server not running, or UDP 7777 blocked by a firewall (OS or provider panel).");
net.Stop();
return welcomed ? 0 : 1;
