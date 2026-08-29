using System.Diagnostics;

namespace NightRunnersMP.Server;

/// <summary>Distance-based update rates, identical to the mod's Sync/SendRate.cs.</summary>
public static class SendRate
{
    public static readonly (float Dist, float Hz)[] Tiers =
    {
        (50f, float.PositiveInfinity),
        (150f, 10f),
        (400f, 4f),
        (float.PositiveInfinity, 1f),
    };

    public const float Hysteresis = 1.2f;

    public static int RawTier(float dist)
    {
        var t = 0;
        while (t < Tiers.Length - 1 && dist > Tiers[t].Dist) t++;
        return t;
    }

    public static float HzForTier(int tier, float fullHz) => Math.Min(fullHz, Tiers[tier].Hz);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static float Now => (float)Clock.Elapsed.TotalSeconds;
}

public sealed class RateGate
{
    private int _tier;
    private float _nextSend = float.NegativeInfinity;

    public int Tier => _tier;

    public bool ShouldSend(float now, float dist, float fullHz)
    {
        var raw = SendRate.RawTier(dist);
        if (raw < _tier) _tier = raw;
        else if (raw > _tier && dist > SendRate.Tiers[_tier].Dist * SendRate.Hysteresis) _tier = raw;

        if (now + 0.001f < _nextSend) return false;
        _nextSend = now + 1f / SendRate.HzForTier(_tier, fullHz);
        return true;
    }
}
