using System.Text;
using LiteNetLib.Utils;
using UnityEngine;

namespace NightRunnersMP.Net;

public enum PacketType : byte
{
    Hello = 1,        // client -> host: name, car model
    Welcome = 2,      // host -> client: your id + everyone already here
    PlayerJoined = 3, // host -> all
    PlayerLeft = 4,   // host -> all
    State = 5,        // both directions; host stamps the sender id when relaying
    Settings = 6,     // host -> client: session rules (traffic, collisions); sent on join and on change
}

/// <summary>Limits and sanitisers applied to everything that arrives from the network.</summary>
public static class Wire
{
    public const string Protocol = "NRMP-0.4";
    public const int MaxNameLength = 24;
    public const int MaxPlayers = 32;
    public const float MaxCoordinate = 100_000f; // metres; the map is a few km across

    /// <summary>Connection key: protocol version, plus the session password when one is set.</summary>
    public static string KeyFor(string? password) => string.IsNullOrEmpty(password) ? Protocol : $"{Protocol}|{password}";

    /// <summary>Strips control characters and rich-text brackets, trims, caps length. Never empty.</summary>
    public static string SanitizeName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "Player";
        var sb = new StringBuilder(MaxNameLength);
        foreach (var ch in raw)
        {
            if (ch < ' ' || ch == '' || ch == '<' || ch == '>') continue;
            sb.Append(ch);
            if (sb.Length >= MaxNameLength) break;
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "Player" : s;
    }

    public static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    public static bool IsFinite(Quaternion q) => float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w);
}

public struct PlayerInfo
{
    public int Id;
    public string Name;
    public int Model; // car_carOrigin.ModelType as int, 0 = unknown

    public void Write(NetDataWriter w) { w.Put(Id); w.Put(Name); w.Put(Model); }

    public static PlayerInfo Read(NetDataReader r) => new()
    {
        Id = r.GetInt(),
        Name = Wire.SanitizeName(r.GetString(Wire.MaxNameLength * 4)),
        Model = r.GetInt(),
    };
}

/// <summary>One snapshot of a car, taken in the sender's FixedUpdate. Exactly Size bytes on the wire.</summary>
public struct CarState
{
    public const int Size = 78;

    public float T;           // sender's physics time (Time.fixedTime)
    public Vector3 Pos;
    public Quaternion Rot;
    public Vector3 Vel;
    public Vector3 AngVel;    // world space, rad/s
    public float Steer, Gas, Brake, Handbrake, Rpm;
    public sbyte Gear;
    public byte Flags;

    public const byte FlagLowBeam = 1, FlagHighBeam = 2, FlagIndLeft = 4, FlagIndRight = 8, FlagEngine = 16;

    public void Write(NetDataWriter w)
    {
        w.Put(T);
        w.Put(Pos.x); w.Put(Pos.y); w.Put(Pos.z);
        w.Put(Rot.x); w.Put(Rot.y); w.Put(Rot.z); w.Put(Rot.w);
        w.Put(Vel.x); w.Put(Vel.y); w.Put(Vel.z);
        w.Put(AngVel.x); w.Put(AngVel.y); w.Put(AngVel.z);
        w.Put(Steer); w.Put(Gas); w.Put(Brake); w.Put(Handbrake); w.Put(Rpm);
        w.Put(Gear); w.Put(Flags);
    }

    public static CarState Read(NetDataReader r) => new()
    {
        T = r.GetFloat(),
        Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
        Rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat()),
        Vel = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
        AngVel = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()),
        Steer = r.GetFloat(), Gas = r.GetFloat(), Brake = r.GetFloat(), Handbrake = r.GetFloat(), Rpm = r.GetFloat(),
        Gear = r.GetSByte(), Flags = r.GetByte(),
    };

    /// <summary>Rejects NaN/Infinity and absurd values; normalises the rotation. False = drop the packet.</summary>
    public bool Validate()
    {
        if (!float.IsFinite(T) || !Wire.IsFinite(Pos) || !Wire.IsFinite(Rot) || !Wire.IsFinite(Vel) || !Wire.IsFinite(AngVel)) return false;
        if (Mathf.Abs(Pos.x) > Wire.MaxCoordinate || Mathf.Abs(Pos.y) > Wire.MaxCoordinate || Mathf.Abs(Pos.z) > Wire.MaxCoordinate) return false;
        if (Vel.sqrMagnitude > 500f * 500f || AngVel.sqrMagnitude > 100f * 100f) return false;
        var len = Mathf.Sqrt(Rot.x * Rot.x + Rot.y * Rot.y + Rot.z * Rot.z + Rot.w * Rot.w);
        if (len < 0.5f || len > 2f) return false;
        Rot = new Quaternion(Rot.x / len, Rot.y / len, Rot.z / len, Rot.w / len);
        Steer = Mathf.Clamp(float.IsFinite(Steer) ? Steer : 0f, -1f, 1f);
        Gas = Mathf.Clamp(float.IsFinite(Gas) ? Gas : 0f, 0f, 1f);
        Brake = Mathf.Clamp(float.IsFinite(Brake) ? Brake : 0f, 0f, 1f);
        Handbrake = Mathf.Clamp(float.IsFinite(Handbrake) ? Handbrake : 0f, 0f, 1f);
        Rpm = Mathf.Clamp(float.IsFinite(Rpm) ? Rpm : 0f, 0f, 20000f);
        return true;
    }

    /// <summary>Blends the non-pose channels; pose is handled by the interpolator.</summary>
    public static CarState Lerp(in CarState a, in CarState b, float t) => new()
    {
        T = Mathf.Lerp(a.T, b.T, t),
        Pos = Vector3.Lerp(a.Pos, b.Pos, t),
        Rot = Quaternion.Slerp(a.Rot, b.Rot, t),
        Vel = Vector3.Lerp(a.Vel, b.Vel, t),
        AngVel = Vector3.Lerp(a.AngVel, b.AngVel, t),
        Steer = Mathf.Lerp(a.Steer, b.Steer, t),
        Gas = b.Gas, Brake = b.Brake, Handbrake = b.Handbrake,
        Rpm = Mathf.Lerp(a.Rpm, b.Rpm, t),
        Gear = b.Gear, Flags = b.Flags,
    };
}
