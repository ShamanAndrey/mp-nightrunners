namespace NightRunnersMP.Server;

/// <summary>
/// Wire format shared with the mod (src/Net/Packets.cs). Keep both in sync and bump Key together.
/// The server never interprets a car snapshot beyond its position; it forwards the raw bytes.
/// </summary>
public static class Protocol
{
    public const string Key = "NRMP-0.4";

    public const byte Hello = 1;        // client -> server: name (string), model (int)
    public const byte Welcome = 2;      // server -> client: yourId (int), count (int), PlayerInfo[count]
    public const byte PlayerJoined = 3; // server -> all: PlayerInfo
    public const byte PlayerLeft = 4;   // server -> all: id (int)
    public const byte State = 5;        // both: id (int), CarState (78 bytes)
    public const byte Settings = 6;     // server -> client: traffic (bool), collisions (bool)

    // CarState layout, little-endian floats unless noted:
    //   0 T | 4 Pos.xyz | 16 Rot.xyzw | 32 Vel.xyz | 44 AngVel.xyz
    //   56 Steer | 60 Gas | 64 Brake | 68 Handbrake | 72 Rpm | 76 Gear (sbyte) | 77 Flags (byte)
    public const int CarStateSize = 78;
    public const int PosOffset = 4;

    public const byte FlagLowBeam = 1, FlagHighBeam = 2, FlagIndLeft = 4, FlagIndRight = 8, FlagEngine = 16;
}

public readonly record struct Vec3(float X, float Y, float Z)
{
    public static float Distance(Vec3 a, Vec3 b)
    {
        var dx = a.X - b.X; var dy = a.Y - b.Y; var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public static Vec3 FromBytes(byte[] buf, int offset) =>
        new(BitConverter.ToSingle(buf, offset), BitConverter.ToSingle(buf, offset + 4), BitConverter.ToSingle(buf, offset + 8));
}
