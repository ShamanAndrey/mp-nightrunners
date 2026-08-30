using System.Runtime.CompilerServices;
using Il2Cpp;
using Il2CppPlanetJem.Core.FloatingOrigin;
using UnityEngine;

namespace NightRunnersMP.Sync;

/// <summary>
/// The alpha shifts its coordinate origin as the player drives (FloatingOriginManager), so a
/// transform's position is only meaningful together with the accumulated offset. Everything that
/// crosses the network is converted to true world coordinates here; the Prologue has no shifting,
/// so its offset is always zero. Convention: world = local + AccumulatedOriginOffset.
/// </summary>
public static class WorldOrigin
{
    public static Vector3 Offset => Game.Variant == GameVariant.Alpha ? AlphaOffset() : Vector3.zero;

    public static Vector3 ToWorld(Vector3 local) => local + Offset;
    public static Vector3 ToLocal(Vector3 world) => world - Offset;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector3 AlphaOffset()
    {
        try
        {
            var m = FloatingOriginManager.Instance;
            return m != null ? m.AccumulatedOriginOffset : Vector3.zero;
        }
        catch { return Vector3.zero; }
    }
}
