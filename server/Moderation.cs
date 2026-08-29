using System.Net;

namespace NightRunnersMP.Server;

/// <summary>Persistent IP ban list: one entry per line, "ip  # reason  date". Missing file = no bans.</summary>
public sealed class BanList
{
    private readonly string _path;
    private readonly Dictionary<string, string> _bans = new(); // ip -> note

    public int Count => _bans.Count;

    public BanList(string path)
    {
        _path = path;
        Load();
    }

    public bool Contains(string ip) => _bans.ContainsKey(ip);

    public void Add(string ip, string reason)
    {
        _bans[ip] = $"{reason}  {DateTime.UtcNow:yyyy-MM-dd HH:mm}Z";
        Save();
    }

    public bool Remove(string ip)
    {
        var removed = _bans.Remove(ip);
        if (removed) Save();
        return removed;
    }

    public string Describe() => _bans.Count == 0
        ? "no bans"
        : string.Join('\n', _bans.OrderBy(kv => kv.Key).Select(kv => $"  {kv.Key,-40} # {kv.Value}"));

    public static bool IsIp(string text) => IPAddress.TryParse(text, out _);

    private void Load()
    {
        if (!File.Exists(_path)) return;
        foreach (var raw in File.ReadAllLines(_path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var hash = line.IndexOf('#');
            var ip = (hash >= 0 ? line[..hash] : line).Trim();
            var note = hash >= 0 ? line[(hash + 1)..].Trim() : "";
            if (IsIp(ip)) _bans[ip] = note;
        }
    }

    private void Save()
    {
        try
        {
            var lines = new List<string> { "# Night Runners MP relay - banned IPs. One per line; text after # is a note." };
            lines.AddRange(_bans.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}  # {kv.Value}"));
            File.WriteAllLines(_path, lines);
        }
        catch (Exception e)
        {
            RelayServer.Log($"WARNING: could not write {_path}: {e.Message}");
        }
    }
}

/// <summary>
/// Admin commands from a file, for servers running under systemd with no console:
///   echo "ban 3 spamming" > /opt/nrmp/admin.cmd
/// The file is read, executed line by line, and deleted.
/// </summary>
public sealed class CommandFile
{
    private readonly string _path;
    private DateTime _nextCheck;

    public CommandFile(string path) { _path = path; }

    public IEnumerable<string> Drain()
    {
        if (DateTime.UtcNow < _nextCheck) yield break;
        _nextCheck = DateTime.UtcNow.AddMilliseconds(500);
        if (!File.Exists(_path)) yield break;

        string[] lines;
        try { lines = File.ReadAllLines(_path); File.Delete(_path); }
        catch (Exception e) { RelayServer.Log($"WARNING: could not read {_path}: {e.Message}"); yield break; }

        foreach (var line in lines)
        {
            var cmd = line.Trim();
            if (cmd.Length > 0 && !cmd.StartsWith('#')) yield return cmd;
        }
    }
}
