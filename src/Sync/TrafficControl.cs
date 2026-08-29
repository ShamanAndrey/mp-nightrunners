using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2Cpp;
using Il2CppPlanetJem.Roads.Traffic;

namespace NightRunnersMP.Sync;

/// <summary>
/// One switch for two traffic systems.
/// - Alpha: TrafficCoordinator (public API). The methods that touch it are kept NoInlining so the
///   type is only resolved when they run — on the Prologue it does not exist.
/// - Prologue: GodConstant.trafficManager (sceneManager_traffic) via reflection, because the mod is
///   compiled against the alpha's assemblies, which do not contain that class.
/// </summary>
public static class TrafficControl
{
    public static bool Available => Game.Variant switch
    {
        GameVariant.Alpha => AlphaAvailable(),
        GameVariant.Prologue => PrologueManager() != null,
        _ => false,
    };

    public static bool IsEnabled => Game.Variant switch
    {
        GameVariant.Alpha => AlphaIsEnabled(),
        GameVariant.Prologue => PrologueIsEnabled(),
        _ => true,
    };

    public static int ActiveCount => Game.Variant switch
    {
        GameVariant.Alpha => AlphaActiveCount(),
        GameVariant.Prologue => PrologueActiveCount(),
        _ => 0,
    };

    public static void Set(bool on, bool clearActive)
    {
        switch (Game.Variant)
        {
            case GameVariant.Alpha: AlphaSet(on, clearActive); break;
            case GameVariant.Prologue: PrologueSet(on, clearActive); break;
        }
    }

    // ---- alpha ---------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool AlphaAvailable() => TrafficCoordinator.Instance != null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool AlphaIsEnabled()
    {
        var t = TrafficCoordinator.Instance;
        return t != null && t.IsTrafficEnabled;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AlphaActiveCount()
    {
        var t = TrafficCoordinator.Instance;
        if (t == null) return 0;
        var list = t.ActiveVehicles?.TryCast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<TrafficVehicle>>();
        return list != null ? list.Count : 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AlphaSet(bool on, bool clearActive)
    {
        var t = TrafficCoordinator.Instance;
        if (t != null) t.SetTrafficEnabled(on, clearActive);
    }

    // ---- prologue (reflection) -------------------------------------------------------------------

    private static PropertyInfo? _trafficManagerProp;
    private static PropertyInfo? _holdSpawningProp;
    private static PropertyInfo? _activeListProp;
    private static MethodInfo? _resetMethod;
    private static bool _reflected;

    private static object? PrologueManager()
    {
        var god = GodConstant.Instance;
        if (god == null) return null;
        if (!_reflected)
        {
            _reflected = true;
            _trafficManagerProp = typeof(GodConstant).GetProperty("trafficManager");
            var tmType = _trafficManagerProp?.PropertyType;
            _holdSpawningProp = tmType?.GetProperty("holdSpawning");
            _activeListProp = tmType?.GetProperty("ActiveTrafficCars");
            _resetMethod = tmType?.GetMethod("Reset_Traffic", Type.EmptyTypes);
        }
        var tm = _trafficManagerProp?.GetValue(god);
        return tm is UnityEngine.Object uo && uo == null ? null : tm;
    }

    private static bool PrologueIsEnabled()
    {
        var tm = PrologueManager();
        if (tm == null || _holdSpawningProp == null) return true;
        return !(bool)(_holdSpawningProp.GetValue(tm) ?? false);
    }

    private static int PrologueActiveCount()
    {
        var tm = PrologueManager();
        var list = tm != null ? _activeListProp?.GetValue(tm) : null;
        if (list == null) return 0;
        var count = list.GetType().GetProperty("Count")?.GetValue(list);
        return count is int n ? n : 0;
    }

    private static void PrologueSet(bool on, bool clearActive)
    {
        var tm = PrologueManager();
        if (tm == null) return;
        _holdSpawningProp?.SetValue(tm, !on);
        if (!on && clearActive) _resetMethod?.Invoke(tm, null);
    }
}
