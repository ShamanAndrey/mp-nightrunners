using System;
using System.Diagnostics;

namespace NightRunnersMP.Sync;

/// <summary>Distance-based update rates: far cars need far fewer snapshots to look right.</summary>
public static class SendRate
{
    /// <summary>(upper distance bound in metres, snapshots per second). Tier 0 uses the configured full rate.</summary>
    public static readonly (float Dist, float Hz)[] Tiers =
    {
        (50f, float.PositiveInfinity),
        (150f, 10f),
        (400f, 4f),
        (float.PositiveInfinity, 1f),
    };

    /// <summary>A pair must move this much past a boundary before dropping to a slower tier.</summary>
    public const float Hysteresis = 1.2f;

    public static int RawTier(float dist)
    {
        var t = 0;
        while (t < Tiers.Length - 1 && dist > Tiers[t].Dist) t++;
        return t;
    }

    public static float HzForTier(int tier, float fullHz) => Math.Min(fullHz, Tiers[tier].Hz);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    /// <summary>Monotonic seconds, usable off the Unity thread.</summary>
    public static float Now => (float)Clock.Elapsed.TotalSeconds;
}

/// <summary>Paces one stream of snapshots according to distance, with hysteresis between tiers.</summary>
public sealed class RateGate
{
    private int _tier;
    private float _nextSend = float.NegativeInfinity;

    public int Tier => _tier;
    public float HzFor(float fullHz) => SendRate.HzForTier(_tier, fullHz);

    public bool ShouldSend(float now, float dist, float fullHz)
    {
        var raw = SendRate.RawTier(dist);
        if (raw < _tier) _tier = raw;                                                   // closer: speed up at once
        else if (raw > _tier && dist > SendRate.Tiers[_tier].Dist * SendRate.Hysteresis) _tier = raw; // farther: only well past the line

        if (now + 0.001f < _nextSend) return false;
        _nextSend = now + 1f / HzFor(fullHz);
        return true;
    }
}
