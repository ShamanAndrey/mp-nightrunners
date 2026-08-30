using AssetsTools.NET;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace NightRunnersMP.MapImport;

/// <summary>Uploads a serialized Texture2D's compressed bytes straight to the GPU (DXT/BC need no decoding).</summary>
public static class TextureReader
{
    public static Texture2D? Read(PrologueData data, AssetTypeValueField bf, out string why)
    {
        why = "";
        var name = bf["m_Name"].AsString;
        var w = bf["m_Width"].AsInt;
        var h = bf["m_Height"].AsInt;
        var fmt = bf["m_TextureFormat"].AsInt;
        var mips = bf["m_MipCount"].IsDummy ? 1 : bf["m_MipCount"].AsInt;
        var colorSpace = bf["m_ColorSpace"].IsDummy ? 1 : bf["m_ColorSpace"].AsInt; // 0 linear, 1 sRGB

        var tf = fmt switch
        {
            1 => TextureFormat.Alpha8,
            2 => TextureFormat.ARGB4444,
            3 => TextureFormat.RGB24,
            4 => TextureFormat.RGBA32,
            5 => TextureFormat.ARGB32,
            7 => TextureFormat.RGB565,
            10 => TextureFormat.DXT1,
            12 => TextureFormat.DXT5,
            13 => TextureFormat.RGBA4444,
            24 => TextureFormat.BC6H,
            25 => TextureFormat.BC7,
            26 => TextureFormat.BC4,
            27 => TextureFormat.BC5,
            _ => (TextureFormat)0,
        };
        if ((int)tf == 0) { why = $"unsupported texture format {fmt} on '{name}'"; return null; }
        if (w <= 0 || h <= 0) { why = $"empty texture '{name}'"; return null; }

        var bytes = bf["image data"].AsByteArray;
        if (bytes == null || bytes.Length == 0)
        {
            var sd = bf["m_StreamData"];
            if (sd["path"].AsString.Length == 0) { why = $"no data for '{name}'"; return null; }
            bytes = data.ReadStream(sd["path"].AsString, sd["offset"].AsLong, sd["size"].AsLong);
        }

        var tex = new Texture2D(w, h, tf, mips > 1, colorSpace == 0) { name = name };
        tex.LoadRawTextureData(new Il2CppStructArray<byte>(bytes));
        tex.Apply(false, true);
        var wrap = bf["m_TextureSettings"]["m_WrapU"].IsDummy ? bf["m_TextureSettings"]["m_WrapMode"] : bf["m_TextureSettings"]["m_WrapU"];
        if (!wrap.IsDummy) tex.wrapMode = (TextureWrapMode)wrap.AsInt;
        var filter = bf["m_TextureSettings"]["m_FilterMode"];
        if (!filter.IsDummy) tex.filterMode = (FilterMode)filter.AsInt;
        var aniso = bf["m_TextureSettings"]["m_Aniso"];
        if (!aniso.IsDummy) tex.anisoLevel = aniso.AsInt;
        return tex;
    }
}
