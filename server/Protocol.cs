using System.Text;

namespace NightRunnersMP.Server;

/// <summary>
/// Wire format shared with the mod (src/Net/Packets.cs). Keep both in sync and bump Key together.
/// The server never interprets a car snapshot beyond validating it and reading its position;
/// it forwards the raw bytes.
/// </summary>
public static class Protocol
{
    public const string Key = "NRMP-0.5";

    public const byte Hello = 1;        // client -> server: name (string), model (int)
    public const byte Welcome = 2;      // server -> client: yourId (int), count (int), PlayerInfo[count]
    public const byte PlayerJoined = 3; // server -> all: PlayerInfo
    public const byte PlayerLeft = 4;   // server -> all: id (int)
    public const byte State = 5;        // both: id (int), CarState (78 bytes)
    public const byte Settings = 6;     // server -> client: traffic (bool), collisions (bool)
    public const byte Chat = 7;         // both: senderId (int, stamped by server; -1 = system notice), text (string)

    // CarState layout, little-endian floats unless noted:
    //   0 T | 4 Pos.xyz | 16 Rot.xyzw | 32 Vel.xyz | 44 AngVel.xyz
    //   56 Steer | 60 Gas | 64 Brake | 68 Handbrake | 72 Rpm | 76 Gear (sbyte) | 77 Flags (byte)
    public const int CarStateSize = 78;
    public const int PosOffset = 4;

    public const int MaxNameLength = 24;
    public const int MaxChatLength = 200;
    public const int SystemSenderId = -1;
    public const float MaxCoordinate = 100_000f;

    public const byte FlagLowBeam = 1, FlagHighBeam = 2, FlagIndLeft = 4, FlagIndRight = 8, FlagEngine = 16;

    /// <summary>Connection key: protocol version, plus the session password when one is set.</summary>
    public static string KeyFor(string? password) => string.IsNullOrEmpty(password) ? Key : $"{Key}|{password}";

    /// <summary>Strips control characters and rich-text brackets, trims, caps length. Never empty.</summary>
    public static string SanitizeName(string? raw) => SanitizeText(raw, MaxNameLength, "Player");

    /// <summary>Chat lines: same cleaning, longer cap; an empty result means "drop it".</summary>
    public static string SanitizeChat(string? raw) => SanitizeText(raw, MaxChatLength, "");

    public static string SanitizeText(string? raw, int maxLength, string fallback)
    {
        if (string.IsNullOrEmpty(raw)) return fallback;
        var sb = new StringBuilder(maxLength);
        foreach (var ch in raw)
        {
            if (ch < ' ' || ch == (char)127 || ch == '<' || ch == '>') continue;
            sb.Append(ch);
            if (sb.Length >= maxLength) break;
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? fallback : s;
    }

    /// <summary>All 19 floats finite, position and speeds within sane bounds, rotation roughly unit length.</summary>
    public static bool ValidateCarState(byte[] raw)
    {
        if (raw.Length != CarStateSize) return false;
        for (var off = 0; off < 76; off += 4)
            if (!float.IsFinite(BitConverter.ToSingle(raw, off))) return false;

        var pos = Vec3.FromBytes(raw, PosOffset);
        if (MathF.Abs(pos.X) > MaxCoordinate || MathF.Abs(pos.Y) > MaxCoordinate || MathF.Abs(pos.Z) > MaxCoordinate) return false;

        var vel = Vec3.FromBytes(raw, 32);
        if (vel.X * vel.X + vel.Y * vel.Y + vel.Z * vel.Z > 500f * 500f) return false;

        float qx = BitConverter.ToSingle(raw, 16), qy = BitConverter.ToSingle(raw, 20), qz = BitConverter.ToSingle(raw, 24), qw = BitConverter.ToSingle(raw, 28);
        var len = MathF.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
        return len is >= 0.5f and <= 2f;
    }
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
