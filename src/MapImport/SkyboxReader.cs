using System;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace NightRunnersMP.MapImport;

/// <summary>
/// Rebuilds the Prologue's skybox for the alpha. The Prologue blends two DXT1 cubemaps with a custom shader
/// (Skybox/CubemapSkyboxBlend); the dominant one is uploaded face by face and shown through the built-in
/// Skybox/Cubemap shader with the same tint, exposure and rotation. Its lower hemisphere is what fills the
/// world below the expressway.
/// </summary>
public static class SkyboxReader
{
    public static Material? Read(PrologueData data, AssetsFileInstance file, AssetTypeValueField materialPptr, Action<string> log)
    {
        try
        {
            var me = data.Am.GetExtAsset(file, materialPptr);
            if (me.baseField == null) { log("[city] skybox: no material"); return null; }
            var mat = me.baseField;
            var props = mat["m_SavedProperties"];

            // Pick the cubemap with the larger blend weight (the shader lerps _Tex -> _Tex2 by _BlendCubemaps).
            var blend = 0f;
            foreach (var f in props["m_Floats.Array"]) if (f["first"].AsString == "_BlendCubemaps") blend = f["second"].AsFloat;
            var wanted = blend > 0.5f ? "_Tex2" : "_Tex";
            AssetTypeValueField? cube = null; AssetsFileInstance? cubeFile = null;
            foreach (var t in props["m_TexEnvs.Array"])
            {
                if (t["first"].AsString != wanted) continue;
                var te = data.Am.GetExtAsset(me.file, t["second"]["m_Texture"]);
                cube = te.baseField; cubeFile = te.file;
            }
            if (cube == null) { log($"[city] skybox: cubemap {wanted} missing"); return null; }

            var size = cube["m_Width"].AsInt;
            var fmt = cube["m_TextureFormat"].AsInt;
            var mips = cube["m_MipCount"].AsInt;
            var faces = cube["m_ImageCount"].IsDummy ? 6 : cube["m_ImageCount"].AsInt;
            var tf = fmt switch { 10 => TextureFormat.DXT1, 12 => TextureFormat.DXT5, 3 => TextureFormat.RGB24, 4 => TextureFormat.RGBA32, 25 => TextureFormat.BC7, _ => (TextureFormat)0 };
            if ((int)tf == 0 || faces != 6) { log($"[city] skybox: unsupported cubemap format {fmt} / {faces} faces"); return null; }

            var bytes = cube["image data"].AsByteArray;
            if (bytes == null || bytes.Length == 0)
            {
                var sd = cube["m_StreamData"];
                bytes = data.ReadStream(sd["path"].AsString, sd["offset"].AsLong, sd["size"].AsLong);
            }
            var faceSize = bytes.Length / 6;
            var cubemap = new Cubemap(size, tf, mips > 1) { name = cube["m_Name"].AsString };
            for (var face = 0; face < 6; face++)
            {
                var faceBytes = new byte[faceSize];
                Buffer.BlockCopy(bytes, face * faceSize, faceBytes, 0, faceSize);
                var tex = new Texture2D(size, size, tf, mips > 1, false);
                tex.LoadRawTextureData(new Il2CppStructArray<byte>(faceBytes));
                tex.Apply(false, true);
                Graphics.CopyTexture(tex, 0, cubemap, face);
                UnityEngine.Object.Destroy(tex);
            }

            var shader = Shader.Find("Skybox/Cubemap");
            if (shader == null) { log("[city] skybox: this game has no Skybox/Cubemap shader"); return null; }
            var sky = new Material(shader) { name = "NRMP_Sky_" + mat["m_Name"].AsString };
            sky.SetTexture("_Tex", cubemap);
            foreach (var c in props["m_Colors.Array"])
                if (c["first"].AsString == "_Tint") sky.SetColor("_Tint", new Color(c["second"]["r"].AsFloat, c["second"]["g"].AsFloat, c["second"]["b"].AsFloat, c["second"]["a"].AsFloat));
            foreach (var f in props["m_Floats.Array"])
            {
                var n = f["first"].AsString;
                if (n == "_Exposure") sky.SetFloat("_Exposure", f["second"].AsFloat);
                // The custom shader's rotation is a raw radian value; Skybox/Cubemap expects degrees.
                if (n == "_Rotation") sky.SetFloat("_Rotation", Mathf.Repeat(f["second"].AsFloat * Mathf.Rad2Deg, 360f));
            }
            log($"[city] skybox: '{mat["m_Name"].AsString}' -> {wanted} '{cubemap.name}' {size}px {tf} {mips} mips, exposure {sky.GetFloat("_Exposure"):F2}, rotation {sky.GetFloat("_Rotation"):F0}°");
            return sky;
        }
        catch (Exception e) { log($"[city] skybox failed: {e.GetType().Name} {e.Message}"); return null; }
    }
}
