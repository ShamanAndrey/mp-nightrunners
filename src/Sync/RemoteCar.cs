using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NightRunnersMP.Net;
using UnityEngine;

namespace NightRunnersMP.Sync;

/// <summary>
/// A remote player's car: a game-spawned vehicle with physics switched off, driven by
/// snapshot interpolation on the sender's clock.
///
/// - Snapshots carry the sender's physics time; a clock-offset estimator maps them onto
///   our clock, so network scheduling noise does not become motion noise.
/// - We render Delay seconds behind the newest snapshot (adaptive: 2 x send interval + jitter).
/// - Between snapshots: cubic Hermite on position using the transmitted velocities.
/// - Past the newest snapshot: dead-reckoning from velocity and angular velocity (prediction).
/// - When new data contradicts what we already showed, the difference is dissolved over
///   CorrectionTau seconds instead of snapping.
///
/// Two movement modes:
/// - Collisions off: solid colliders disabled, pose written to the transform in LateUpdate.
/// - Collisions on: colliders enabled, pose fed to the kinematic rigidbody with MovePosition in
///   FixedUpdate (+ rigidbody interpolation), so PhysX knows the car's velocity and contacts
///   with the player transfer momentum like a moving car rather than a teleporting wall.
/// </summary>
public sealed class RemoteCar
{
    // Base limits at full rate; all of them stretch with the measured snapshot interval so a
    // far car receiving 1 Hz is predicted further ahead and corrected more gently.
    private const float BaseMaxExtrapolate = 0.30f;
    private const float BaseMaxDelay = 0.30f;
    private const float BaseCorrectionTau = 0.08f;
    private const float BaseSnapDistance = 5f; // corrections bigger than this are teleports: snap
    private const float StaleAfter = 5f;       // seconds without packets before the car is hidden
    private const int MaxSamples = 64;

    private readonly List<CarState> _buf = new(); // ordered by T
    private readonly List<Collider> _solid = new();

    private bool _hasOffset;
    private float _offset;              // ourClock - senderClock (includes one-way latency)
    private float _jitter;              // EMA of |offset sample - offset|
    private float _interval = 0.04f;    // EMA of gap between sender timestamps
    private float _lastT = float.NegativeInfinity;
    private float _lastArrival = float.NegativeInfinity;

    private Vector3 _errPos;
    private Quaternion _errRot = Quaternion.identity;

    private Il2CppArrayBase<RCC_WheelCollider>? _wheels;
    private Rigidbody? _rb;
    private bool _hidden;
    private bool _collisions;

    public PlayerInfo Info;
    public GameObject? Root { get; private set; }
    public RCC_CarControllerV3? Rcc { get; private set; }
    public Vector3 LocalOffset;       // dev aid: shifts the loopback ghost sideways
    public float MinDelay = 0.08f;    // floor for the adaptive interpolation delay

    public bool IsSpawned => Rcc != null;
    public bool IsHidden => _hidden;
    public bool Collisions => _collisions;
    public bool HasSnapshot => _buf.Count > 0;
    /// <summary>Newest snapshot position in true world coordinates (see WorldOrigin).</summary>
    public Vector3 LastKnownPos => _buf.Count > 0 ? _buf[^1].Pos : Vector3.zero;
    public Quaternion LastKnownRot => _buf.Count > 0 ? _buf[^1].Rot : Quaternion.identity;
    public float LastSnapshotAge => _buf.Count > 0 ? Time.realtimeSinceStartup - _lastArrival : float.NaN;
    public Vector3 RenderedPos { get; private set; }

    private float MaxDelay => Mathf.Max(BaseMaxDelay, 2f * _interval);
    private float MaxExtrapolate => Mathf.Max(BaseMaxExtrapolate, 1.5f * _interval);
    private float CorrectionTau => Mathf.Max(BaseCorrectionTau, 0.5f * _interval);
    private float SnapDistance => Mathf.Max(BaseSnapDistance, 20f * _interval);

    public float Delay => Mathf.Clamp(2f * _interval + 3f * _jitter, MinDelay, MaxDelay);
    public float Jitter => _jitter;
    public float ReceiveHz => _interval > 1e-3f ? 1f / _interval : 0f;
    public string Mode { get; private set; } = "-";

    public RemoteCar(PlayerInfo info, bool collisions)
    {
        Info = info;
        _collisions = collisions;
    }

    public void Attach(GameObject root)
    {
        Root = root;
        Rcc = root.GetComponentInChildren<RCC_CarControllerV3>();
        if (Rcc == null) return;

        Rcc.canControl = false;
        Rcc.externalController = true;
        Rcc.engineRunning = true;

        _rb = Rcc.rigid;
        if (_rb != null)
        {
            _rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            _rb.isKinematic = true;
            _rb.useGravity = false;
        }

        _solid.Clear();
        foreach (var c in root.GetComponentsInChildren<Collider>(true))
        {
            if (c.TryCast<WheelCollider>() != null) continue; // wheel colliders position the wheel meshes
            _solid.Add(c);
        }
        ApplyCollisionMode();

        _wheels = Rcc.GetComponentsInChildren<RCC_WheelCollider>();
        Root.name = $"NRMP_{Info.Name}_{Info.Id}";
        _errPos = Vector3.zero;
        _errRot = Quaternion.identity;
        _hidden = false;
    }

    public void SetCollisions(bool on)
    {
        if (_collisions == on) return;
        _collisions = on;
        if (IsSpawned) ApplyCollisionMode();
    }

    private void ApplyCollisionMode()
    {
        foreach (var c in _solid) if (c != null) c.enabled = _collisions;
        if (_rb != null) _rb.interpolation = _collisions ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
    }

    public void Push(in CarState s)
    {
        var now = Time.realtimeSinceStartup;
        if (s.T <= _lastT) return; // duplicate or out of order

        // Clock offset: chase the smallest observed value quickly (that is the true latency floor),
        // creep upwards slowly if latency genuinely grows.
        var o = now - s.T;
        if (!_hasOffset) { _offset = o; _hasOffset = true; }
        else if (o < _offset) _offset = Mathf.Lerp(_offset, o, 0.5f);
        else _offset = Mathf.Lerp(_offset, o, 0.02f);
        _jitter = Mathf.Lerp(_jitter, Mathf.Abs(o - _offset), 0.1f);
        if (!float.IsNegativeInfinity(_lastT))
        {
            // Rate drops (sender moved far away) must be noticed within a packet or two;
            // rate rises can settle gradually.
            var gap = s.T - _lastT;
            _interval = Mathf.Lerp(_interval, gap, gap > _interval ? 0.5f : 0.1f);
        }
        _lastT = s.T;
        _lastArrival = now;

        // If this snapshot changes what we are showing right now, absorb the jump into the
        // correction offset so it can be dissolved smoothly.
        var renderT = RenderTime;
        var pBefore = Vector3.zero;
        var rBefore = Quaternion.identity;
        var had = IsSpawned && !_hidden && Evaluate(renderT, out pBefore, out rBefore, out _);

        _buf.Add(s);
        if (_buf.Count > MaxSamples) _buf.RemoveAt(0);

        if (had && Evaluate(renderT, out var pAfter, out var rAfter, out _))
        {
            _errPos += pBefore - pAfter;
            _errRot = _errRot * rBefore * Quaternion.Inverse(rAfter);
        }
    }

    private float RenderTime => Time.realtimeSinceStartup - _offset - Delay;

    /// <summary>Pose at sender-time t: Hermite between bracketing snapshots, or prediction past the last one.</summary>
    private bool Evaluate(float t, out Vector3 pos, out Quaternion rot, out CarState state)
    {
        while (_buf.Count >= 2 && _buf[1].T <= t) _buf.RemoveAt(0);
        if (_buf.Count == 0) { pos = default; rot = Quaternion.identity; state = default; return false; }

        var a = _buf[0];
        if (_buf.Count >= 2)
        {
            var b = _buf[1];
            var h = b.T - a.T;
            var u = h > 1e-4f ? Mathf.Clamp01((t - a.T) / h) : 1f;
            pos = Hermite(a.Pos, a.Vel * h, b.Pos, b.Vel * h, u);
            rot = Quaternion.Slerp(a.Rot, b.Rot, u);
            state = CarState.Lerp(a, b, u);
            Mode = "interp";
        }
        else
        {
            var d = Mathf.Clamp(t - a.T, 0f, MaxExtrapolate);
            pos = a.Pos + a.Vel * d;
            rot = Integrate(a.Rot, a.AngVel, d);
            state = a;
            Mode = d > 0.001f ? $"predict {d * 1000f:F0}ms" : "hold";
        }
        return true;
    }

    /// <summary>Current display pose in local (shifted) coordinates: interpolated truth plus the decaying correction.</summary>
    private bool CurrentPose(out Vector3 pos, out Quaternion rot, out CarState state)
    {
        if (!Evaluate(RenderTime, out var p, out var r, out state)) { pos = default; rot = Quaternion.identity; return false; }
        rot = _errRot * r;
        pos = WorldOrigin.ToLocal(p + _errPos) + rot * LocalOffset;
        return true;
    }

    /// <summary>LateUpdate: visibility, correction decay, inputs/lights/wheels, and the pose when collisions are off.</summary>
    public void Update()
    {
        if (!IsSpawned || _buf.Count == 0) return;

        // Hide cars whose owner stopped sending (garage, menu, connection trouble).
        var stale = LastSnapshotAge > StaleAfter;
        if (stale != _hidden)
        {
            _hidden = stale;
            Root!.SetActive(!stale);
            if (!stale) { _errPos = Vector3.zero; _errRot = Quaternion.identity; }
        }
        if (_hidden) return;

        // Dissolve any accumulated correction.
        var dt = Time.deltaTime;
        var k = 1f - Mathf.Exp(-dt / CorrectionTau);
        if (_errPos.sqrMagnitude > SnapDistance * SnapDistance) { _errPos = Vector3.zero; _errRot = Quaternion.identity; }
        _errPos = Vector3.Lerp(_errPos, Vector3.zero, k);
        _errRot = Quaternion.Slerp(_errRot, Quaternion.identity, k);

        if (!CurrentPose(out var pos, out var rot, out var s)) return;
        RenderedPos = pos;
        if (!_collisions) Rcc!.transform.SetPositionAndRotation(pos, rot);
        ApplyInputs(s, rot, dt);
    }

    /// <summary>FixedUpdate: when collisions are on, move the kinematic body through the physics engine.</summary>
    public void FixedUpdate()
    {
        if (!_collisions || !IsSpawned || _hidden || _rb == null || _buf.Count == 0) return;
        if (!CurrentPose(out var pos, out var rot, out _)) return;
        _rb.MovePosition(pos);
        _rb.MoveRotation(rot);
    }

    private void ApplyInputs(in CarState s, Quaternion rot, float dt)
    {
        var rcc = Rcc!;
        rcc.steerInput = s.Steer;
        rcc.gasInput = s.Gas;
        rcc.brakeInput = s.Brake;
        rcc.handbrakeInput = s.Handbrake;
        rcc.engineRPM = s.Rpm;
        rcc.currentGear = s.Gear;

        rcc.lowBeamHeadLightsOn = (s.Flags & CarState.FlagLowBeam) != 0;
        rcc.highBeamHeadLightsOn = (s.Flags & CarState.FlagHighBeam) != 0;
        var left = (s.Flags & CarState.FlagIndLeft) != 0;
        var right = (s.Flags & CarState.FlagIndRight) != 0;
        rcc.indicatorsOn = (RCC_CarControllerV3.IndicatorsOn)(left && right ? 3 : left ? 2 : right ? 1 : 0);
        rcc.engineRunning = (s.Flags & CarState.FlagEngine) != 0;

        // Wheels: the kinematic body reports 0 rpm, so advance RCC's accumulated spin angle ourselves.
        if (_wheels != null)
        {
            var forwardSpeed = Vector3.Dot(s.Vel, rot * Vector3.forward); // m/s along the car
            foreach (var w in _wheels)
            {
                if (w == null) continue;
                var wc = w.wheelCollider;
                var radius = wc != null ? Mathf.Max(0.05f, wc.radius) : 0.33f;
                w.wheelRotation += forwardSpeed / radius * Mathf.Rad2Deg * dt;
            }
        }
    }

    private static Vector3 Hermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float u)
    {
        var u2 = u * u;
        var u3 = u2 * u;
        return (2f * u3 - 3f * u2 + 1f) * p0
             + (u3 - 2f * u2 + u) * m0
             + (-2f * u3 + 3f * u2) * p1
             + (u3 - u2) * m1;
    }

    private static Quaternion Integrate(Quaternion r, Vector3 angVel, float dt)
    {
        var speed = angVel.magnitude;
        if (speed * dt < 1e-5f) return r;
        return Quaternion.AngleAxis(speed * dt * Mathf.Rad2Deg, angVel / speed) * r;
    }

    /// <summary>Forget the spawned object (scene unloaded it) but keep identity and snapshots.</summary>
    public void Detach() { Root = null; Rcc = null; _rb = null; _wheels = null; _solid.Clear(); _hidden = false; }

    public void Destroy()
    {
        if (Root != null) Object.Destroy(Root);
        Detach();
    }
}
