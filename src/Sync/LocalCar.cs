using Il2Cpp;
using NightRunnersMP.Net;
using UnityEngine;

namespace NightRunnersMP.Sync;

/// <summary>Reads the local player's car. All lookups go through the game's own singletons.</summary>
public static class LocalCar
{
    public static RCC_CarControllerV3? Rcc
    {
        get
        {
            var sm = RCC_SceneManager.Instance;
            if (sm == null) return null;
            var v = sm.activePlayerVehicle;
            return v != null ? v : null;
        }
    }

    public static int ModelOf(RCC_CarControllerV3 rcc)
    {
        var local = rcc.GetComponent<CarLocalCustom>();
        if (local == null) local = rcc.GetComponentInParent<CarLocalCustom>();
        if (local == null) local = rcc.GetComponentInChildren<CarLocalCustom>();
        if (local == null) return 0;
        var origin = local.carOrigin;
        return origin != null ? (int)origin.modelType : 0;
    }

    /// <summary>Call from FixedUpdate so the pose and timestamp come from the same physics step.</summary>
    public static CarState Sample(RCC_CarControllerV3 rcc, float physicsTime)
    {
        var rb = rcc.rigid;
        var t = rcc.transform;

        byte flags = 0;
        if (rcc.lowBeamHeadLightsOn) flags |= CarState.FlagLowBeam;
        if (rcc.highBeamHeadLightsOn) flags |= CarState.FlagHighBeam;
        var ind = (int)rcc.indicatorsOn; // 0 off, 1 right, 2 left, 3 all
        if (ind == 2 || ind == 3) flags |= CarState.FlagIndLeft;
        if (ind == 1 || ind == 3) flags |= CarState.FlagIndRight;
        if (rcc.engineRunning) flags |= CarState.FlagEngine;

        return new CarState
        {
            T = physicsTime,
            Pos = WorldOrigin.ToWorld(rb != null ? rb.position : t.position), // true world coordinates on the wire
            Rot = rb != null ? rb.rotation : t.rotation,
            Vel = rb != null ? rb.velocity : Vector3.zero,
            AngVel = rb != null ? rb.angularVelocity : Vector3.zero,
            Steer = rcc.steerInput,
            Gas = rcc.gasInput,
            Brake = rcc.brakeInput,
            Handbrake = rcc.handbrakeInput,
            Rpm = rcc.engineRPM,
            Gear = (sbyte)rcc.currentGear,
            Flags = flags,
        };
    }
}
