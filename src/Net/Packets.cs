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

public struct PlayerInfo
{
    public int Id;
    public string Name;
    public int Model; // car_carOrigin.ModelType as int, 0 = unknown

    public void Write(NetDataWriter w) { w.Put(Id); w.Put(Name); w.Put(Model); }
    public static PlayerInfo Read(NetDataReader r) => new() { Id = r.GetInt(), Name = r.GetString(), Model = r.GetInt() };
}

/// <summary>One snapshot of a car, taken in the sender's FixedUpdate. ~78 bytes on the wire.</summary>
public struct CarState
{
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
