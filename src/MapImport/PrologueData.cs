using System;
using System.Collections.Generic;
using System.IO;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace NightRunnersMP.MapImport;

/// <summary>
/// Opens a Night Runners Prologue installation (the player's own Steam copy) for reading with
/// AssetsTools.NET. Nothing is copied or redistributed: scenes are parsed straight from the
/// player's files every time the city is loaded.
/// </summary>
public sealed class PrologueData : IDisposable
{
    public const string ExeName = "NIGHT-RUNNERS PROLOGUE.exe";
    public const string DataDirName = "NIGHT-RUNNERS PROLOGUE_Data";

    public string DataDir { get; }
    public AssetsManager Am { get; }
    public string UnityVersion { get; }
    public List<string> Scenes { get; } = new();

    public PrologueData(string dataDir, string classDataPath)
    {
        DataDir = dataDir;
        Am = new AssetsManager();
        Am.LoadClassPackage(classDataPath);
        var ggm = Am.LoadAssetsFile(Path.Combine(dataDir, "globalgamemanagers"), false);
        UnityVersion = ggm.file.Metadata.UnityVersion;
        Am.LoadClassDatabaseFromPackage(UnityVersion);
        foreach (var info in ggm.file.GetAssetsOfType(AssetClassID.BuildSettings))
        {
            var bf = Am.GetBaseField(ggm, info);
            foreach (var s in bf["scenes.Array"]) Scenes.Add(s.AsString);
        }
    }

    public int SceneIndex(string nameContains) =>
        Scenes.FindIndex(s => s.Contains(nameContains, StringComparison.OrdinalIgnoreCase));

    public static string SceneShortName(string path) => Path.GetFileNameWithoutExtension(path);

    public AssetsFileInstance LoadLevel(int index) => Am.LoadAssetsFile(Path.Combine(DataDir, $"level{index}"), true);

    /// <summary>Reads a byte range from a streamed resource file (.resS) referenced by an asset.</summary>
    public byte[] ReadStream(string path, long offset, long size)
    {
        var name = Path.GetFileName(path.Replace("archive:/", "").Replace('/', Path.DirectorySeparatorChar));
        var full = Path.Combine(DataDir, name);
        using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[size];
        var read = 0;
        while (read < buf.Length)
        {
            var n = fs.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return buf;
    }

    public void Dispose()
    {
        try { Am.UnloadAll(true); } catch { }
    }

    // ---- locating the install -------------------------------------------------------------------

    /// <summary>Configured directory first, then every Steam library on the machine.</summary>
    public static string? LocateDataDir(string? configuredGameDir, Action<string> log)
    {
        if (!string.IsNullOrWhiteSpace(configuredGameDir))
        {
            var d = Path.Combine(configuredGameDir, DataDirName);
            if (Directory.Exists(d)) return d;
            log($"[city] PrologueDir '{configuredGameDir}' has no {DataDirName}; scanning Steam libraries instead");
        }
        foreach (var lib in SteamLibraries())
        {
            var d = Path.Combine(lib, "steamapps", "common", "NIGHT-RUNNERS PROLOGUE", DataDirName);
            if (Directory.Exists(d)) return d;
        }
        return null;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var libs = new List<string>();
        void Add(string? p) { if (!string.IsNullOrEmpty(p)) { p = p.Replace('/', '\\'); if (!libs.Contains(p)) libs.Add(p); } }

        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            Add(k?.GetValue("SteamPath") as string);
        }
        catch { }
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            Add(k?.GetValue("InstallPath") as string);
        }
        catch { }
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            foreach (var sub in new[] { "SteamLibrary", "Steam", @"Program Files (x86)\Steam", @"Games\Steam" })
                Add(Path.Combine(drive.RootDirectory.FullName, sub));
        }

        // Every library lists the others in libraryfolders.vdf
        var extra = new List<string>();
        foreach (var l in libs)
        {
            var vdf = Path.Combine(l, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            try
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    extra.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch { }
        }
        foreach (var e in extra) Add(e);
        return libs;
    }
}
