using System;
using UnityEngine;

namespace NightRunnersMP.Sync;

public enum GameVariant { Unknown, Alpha, Prologue }

/// <summary>
/// Which Night Runners build we are running inside. The car/spawn/RCC API is identical across the
/// private alpha (Unity 2019.4, Mount Haruna) and the Steam Prologue (Unity 2018.4, C1 Tatsumi city),
/// but traffic, maps and the car list differ, so a few paths branch on this.
/// </summary>
public static class Game
{
    public static GameVariant Variant { get; private set; } = GameVariant.Unknown;
    public static string ProductName { get; private set; } = "";

    /// <summary>Short tag used in the protocol key so different builds never share a session.</summary>
    public static string Tag => Variant switch
    {
        GameVariant.Alpha => "alpha",
        GameVariant.Prologue => "prologue",
        _ => "unknown",
    };

    public static string DisplayName => Variant switch
    {
        GameVariant.Alpha => "private alpha",
        GameVariant.Prologue => "Prologue",
        _ => "unknown build",
    };

    public static void Detect()
    {
        try { ProductName = Application.productName ?? ""; } catch { ProductName = ""; }
        var n = ProductName.ToUpperInvariant();
        Variant = n.Contains("PROLOGUE") ? GameVariant.Prologue
                : n.Contains("ALPHA") ? GameVariant.Alpha
                : GameVariant.Unknown;
    }

    /// <summary>
    /// True for additively streamed sub-scenes (terrain tiles, tunnels, collider/building layers).
    /// Loading one of these does not mean the world changed, so ghosts must not be reset.
    /// </summary>
    public static bool IsStreamingSubScene(string sceneName)
    {
        var s = sceneName.ToUpperInvariant();
        return s.Contains("CHUNK") || s.Contains("_AREA_") || s.Contains("TUNNEL") || s.Contains("COLLIDERS") || s.Contains("BUILDINGS");
    }
}
