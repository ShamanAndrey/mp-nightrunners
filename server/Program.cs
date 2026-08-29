using System.Collections.Concurrent;
using NightRunnersMP.Server;

var port = 7777;
var traffic = true;
var collisions = false;
var bots = 0;
var max = 32;
var rate = 25f;
var password = Environment.GetEnvironmentVariable("NRMP_PASSWORD"); // keeps it out of `ps` output and unit files
var banFile = "bans.txt";
var cmdFile = "admin.cmd";

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
        case "--bans": banFile = Next(); break;
        case "--cmdfile": cmdFile = Next(); break;
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
                  --bans FILE       persistent ban list (default bans.txt in the working directory)
                  --cmdfile FILE    admin command file, read+deleted every 0.5 s (default admin.cmd)
                Commands (console, or one per line written to the command file):
                  list | traffic on|off | collisions on|off | bots N | quit
                  kick <id|name|ip> [reason] | ban <id|name|ip> [reason] | unban <ip> | bans
                  say <message>     server notice in everyone's chat
                """);
            return 0;
        default:
            Console.Error.WriteLine($"unknown argument {args[i]} (try --help)");
            return 2;
    }
}

var server = new RelayServer(port, traffic, collisions, password, banFile) { MaxPlayers = Math.Clamp(max, 1, 32), FullRateHz = Math.Clamp(rate, 1f, 50f) };
if (!server.Start()) return 1;
for (var b = 0; b < bots; b++) server.AddBot();
var commandFile = new CommandFile(cmdFile);
RelayServer.Log($"admin: type commands here, or write them to {Path.GetFullPath(cmdFile)}");

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

    foreach (var fileCmd in commandFile.Drain()) { RelayServer.Log($"admin.cmd> {fileCmd}"); commands.Enqueue(fileCmd); }

    while (commands.TryDequeue(out var cmd))
    {
        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) continue;
        var reason = parts.Length > 2 ? string.Join(' ', parts.Skip(2)) : "no reason given";
        switch (parts[0].ToLowerInvariant())
        {
            case "traffic" when parts.Length > 1: server.SetTraffic(parts[1] is "on" or "true" or "1"); break;
            case "collisions" when parts.Length > 1: server.SetCollisions(parts[1] is "on" or "true" or "1"); break;
            case "bots" when parts.Length > 1 && int.TryParse(parts[1], out var n):
                server.RemoveBots();
                for (var b = 0; b < n; b++) server.AddBot();
                break;
            case "list": RelayServer.Log("players:\n" + server.Describe()); break;
            case "kick" when parts.Length > 1: RelayServer.Log(server.Kick(parts[1], reason)); break;
            case "ban" when parts.Length > 1: RelayServer.Log(server.Ban(parts[1], reason)); break;
            case "unban" when parts.Length > 1: RelayServer.Log(server.Unban(parts[1])); break;
            case "bans": RelayServer.Log("bans:\n" + server.Bans.Describe()); break;
            case "say" when parts.Length > 1: RelayServer.Log(server.Say(string.Join(' ', parts.Skip(1)))); break;
            case "quit" or "exit" or "stop": running = false; break;
            default: RelayServer.Log("commands: list | traffic on|off | collisions on|off | bots N | kick <id|name|ip> [reason] | ban <id|name|ip> [reason] | unban <ip> | bans | say <msg> | quit"); break;
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
