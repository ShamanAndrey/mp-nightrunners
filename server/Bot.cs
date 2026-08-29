using LiteNetLib.Utils;

namespace NightRunnersMP.Server;

/// <summary>
/// A fake player that drives a circle around a real player. Exists so a single person can test
/// the server, the mod's ghost rendering and the rate scaling without a second human.
/// </summary>
public sealed class Bot
{
    private const float Radius = 12f;
    private const float AngularSpeed = 0.6f; // rad/s -> ~26 km/h on a 12 m circle

    private readonly float _phase;
    private float _lastYaw;
    private bool _hasYaw;

    public int Model { get; }

    public Bot(int index, int model)
    {
        _phase = index * 2.1f;
        Model = model;
    }

    /// <summary>Writes one CarState (78 bytes) for time t, orbiting the given centre.</summary>
    public void WriteState(NetDataWriter w, float t, Vec3 centre)
    {
        var theta = _phase + t * AngularSpeed;
        var (sin, cos) = MathF.SinCos(theta);

        var px = centre.X + Radius * cos;
        var py = centre.Y;
        var pz = centre.Z + Radius * sin;

        // Tangent of the circle = direction of travel; Unity yaw is measured around +Y from +Z.
        var dx = -sin; var dz = cos;
        var yaw = MathF.Atan2(dx, dz);
        var angVelY = _hasYaw ? WrapPi(yaw - _lastYaw) * (1f / 0.04f) : AngularSpeed;
        _lastYaw = yaw; _hasYaw = true;

        var speed = Radius * AngularSpeed;
        var (qs, qc) = MathF.SinCos(yaw * 0.5f);

        w.Put(t);
        w.Put(px); w.Put(py); w.Put(pz);          // Pos
        w.Put(0f); w.Put(qs); w.Put(0f); w.Put(qc); // Rot (x, y, z, w) — yaw about Y
        w.Put(dx * speed); w.Put(0f); w.Put(dz * speed); // Vel
        w.Put(0f); w.Put(angVelY); w.Put(0f);     // AngVel
        w.Put(-0.6f);                              // Steer (constant left-hand circle)
        w.Put(0.3f); w.Put(0f); w.Put(0f);         // Gas, Brake, Handbrake
        w.Put(3200f);                              // Rpm
        w.Put((sbyte)2);                           // Gear
        w.Put((byte)(Protocol.FlagEngine | Protocol.FlagLowBeam));
    }

    private static float WrapPi(float a)
    {
        while (a > MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }
}
