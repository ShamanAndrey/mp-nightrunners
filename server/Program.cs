using System.Collections.Concurrent;
using NightRunnersMP.Server;

var port = 7777;
var traffic = true;
var collisions = false;
var bots = 0;
var max = 32;
var rate = 25f;
var password = Environment.GetEnvironmentVariable("NRMP_PASSWORD"); // keeps it out of `ps` output and unit files

for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    static bool OnOff(string v) => v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
    switch (args[i])
    {
        case "--port": port = int.Parse(Next()); break;
        case "--traffic": traffic = OnOff(Next()); break;
        case "--collisions": collisions = OnOff(Next()); break;
        case "--bots": bots = int.Parse(Next()); break;
        case "--max": max = int.Parse(Next()); break;
        case "--rate": rate = float.Parse(Next()); break;
        case "--password": password = Next(); break;
        case "-h" or "--help":
            Console.WriteLine("""
                nrmp-server — dedicated relay for the Night Runners MP mod
                  --port N          UDP port (default 7777)
                  --traffic on|off  AI traffic rule for everyone (default on)
                  --collisions on|off  car collisions rule (default off)
                  --bots N          fake players that orbit the first real player (testing)
                  --max N           player cap (default 32)
                  --rate N          full snapshot rate in Hz (default 25)
                  --password X      players must enter this to join (or set env NRMP_PASSWORD)
                Console commands while running: traffic on|off, collisions on|off, bots N, list, quit
                """);
            return 0;
        default:
            Console.Error.WriteLine($"unknown argument {args[i]} (try --help)");
            return 2;
    }
}

var server = new RelayServer(port, traffic, collisions, password) { MaxPlayers = Math.Clamp(max, 1, 32), FullRateHz = Math.Clamp(rate, 1f, 50f) };
if (!server.Start()) return 1;
for (var b = 0; b < bots; b++) server.AddBot();

// Console commands arrive on a reader thread and are applied on the main loop.
var commands = new ConcurrentQueue<string>();
var running = true;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; running = false; };
new Thread(() =>
{
    while (running)
    {
        var line = Console.ReadLine();
        if (line == null) { Thread.Sleep(500); continue; } // no stdin (systemd): keep the thread alive quietly
        commands.Enqueue(line.Trim());
    }
}) { IsBackground = true }.Start();

var nextStats = DateTime.UtcNow.AddMinutes(5);
while (running)
{
    server.Poll();

    while (commands.TryDequeue(out var cmd))
    {
        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) continue;
        switch (parts[0].ToLowerInvariant())
        {
            case "traffic" when parts.Length > 1: server.SetTraffic(parts[1] is "on" or "true" or "1"); break;
            case "collisions" when parts.Length > 1: server.SetCollisions(parts[1] is "on" or "true" or "1"); break;
            case "bots" when parts.Length > 1 && int.TryParse(parts[1], out var n):
                server.RemoveBots();
                for (var b = 0; b < n; b++) server.AddBot();
                break;
            case "list": Console.WriteLine(server.Describe()); break;
            case "quit" or "exit" or "stop": running = false; break;
            default: Console.WriteLine("commands: traffic on|off, collisions on|off, bots N, list, quit"); break;
        }
    }

    if (DateTime.UtcNow >= nextStats)
    {
        nextStats = DateTime.UtcNow.AddMinutes(5);
        RelayServer.Log($"{server.PlayerCount} player(s), {server.BotCount} bot(s) online");
    }

    Thread.Sleep(5);
}

server.Stop();
RelayServer.Log("stopped");
return 0;
