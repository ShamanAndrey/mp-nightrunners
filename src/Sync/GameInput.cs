using System;
using Il2CppRewired;

namespace NightRunnersMP.Sync;

/// <summary>
/// The game reads all its controls through Rewired, which sees raw keyboard input even when a text
/// box has focus or the window is in the background. Disabling Rewired's keyboard controller is the
/// only way to guarantee that typing "hello" does not shift gears, pull the handbrake or open menus.
/// The mod's own hotkeys and IMGUI text fields use Unity's input path and are unaffected.
/// </summary>
public static class GameInput
{
    private static bool _suspended;
    private static bool _previousEnabled = true;
    private static bool _warned;

    public static bool Suspended => _suspended;

    /// <summary>Set the desired state each frame; changes are applied once, then re-asserted while suspended.</summary>
    public static void Set(bool suspend, Action<string> log)
    {
        if (suspend == _suspended)
        {
            if (_suspended) Enforce();
            return;
        }
        _suspended = suspend;
        try
        {
            if (!ReInput.isReady) return;
            var keyboard = ReInput.controllers.Keyboard;
            if (keyboard == null) return;
            if (suspend) { _previousEnabled = keyboard.enabled; keyboard.enabled = false; }
            else keyboard.enabled = _previousEnabled;
        }
        catch (Exception e)
        {
            if (!_warned) { _warned = true; log($"[input] could not toggle Rewired keyboard: {e.GetType().Name} — typing may reach the game"); }
        }
    }

    /// <summary>The game may re-enable its keyboard (scene loads, menus); keep it off while we need it off.</summary>
    private static void Enforce()
    {
        try
        {
            if (!ReInput.isReady) return;
            var keyboard = ReInput.controllers.Keyboard;
            if (keyboard != null && keyboard.enabled) keyboard.enabled = false;
        }
        catch { /* reported once in Set */ }
    }
}
