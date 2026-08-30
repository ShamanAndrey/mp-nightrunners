using System;
using System.Collections.Generic;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using UnityEngine;

namespace NightRunnersMP.MapImport;

/// <summary>Rebuilds a serialized Material on one of the alpha's shaders (mostly Standard), copying the common properties.</summary>
public sealed class MaterialReader
{
    private readonly PrologueData _data;
    private readonly Action<string> _log;
    private readonly Dictionary<(string, long), Material> _materials = new();
    private readonly Dictionary<(string, long), Texture2D?> _textures = new();
    private readonly HashSet<string> _warnedShaders = new();
    private Material? _fallback;

    public int MaterialCount => _materials.Count;
    public int TextureCount => _textures.Count;

    public MaterialReader(PrologueData data, Action<string> log) { _data = data; _log = log; }

    /// <summary>Prologue shader → alpha shader. Anything unknown becomes Standard.</summary>
    private static string MapShader(string name) => name switch
    {
        "Bakery/Standard" => "Standard",
        "PolyboxStandard" => "Standard",
        "Autodesk Interactive" => "Standard",
        "Pro Car Paint Shader" => "Standard",
        "Hidden / Pro Car Paint Transparent Shader" => "Standard",
        _ => name,
    };

    public Material Get(AssetsFileInstance file, AssetTypeValueField pptr)
    {
        var ext = _data.Am.GetExtAsset(file, pptr);
        if (ext.baseField == null) return Fallback();
        var key = (ext.file.name, ext.info.PathId);
        if (_materials.TryGetValue(key, out var cached)) return cached;

        var bf = ext.baseField;
        var name = bf["m_Name"].AsString;
        var shaderExt = _data.Am.GetExtAsset(ext.file, bf["m_Shader"]);
        var shaderName = shaderExt.baseField != null ? shaderExt.baseField["m_ParsedForm"]["m_Name"].AsString : "Standard";
        var mapped = MapShader(shaderName);
        var shader = Shader.Find(mapped);
        if (shader == null)
        {
            if (_warnedShaders.Add(shaderName)) _log($"[city] shader '{shaderName}' not in this game; using Standard");
            shader = Shader.Find("Standard");
        }

        var mat = new Material(shader) { name = name };
        var props = bf["m_SavedProperties"];

        foreach (var c in props["m_Colors.Array"])
        {
            var pname = c["first"].AsString;
            var col = c["second"];
            if (mat.HasProperty(pname)) mat.SetColor(pname, new Color(col["r"].AsFloat, col["g"].AsFloat, col["b"].AsFloat, col["a"].AsFloat));
        }
        foreach (var f in props["m_Floats.Array"])
        {
            var pname = f["first"].AsString;
            if (mat.HasProperty(pname)) mat.SetFloat(pname, f["second"].AsFloat);
        }
        foreach (var t in props["m_TexEnvs.Array"])
        {
            var pname = t["first"].AsString;
            if (!mat.HasProperty(pname)) continue;
            var tex = GetTexture(ext.file, t["second"]["m_Texture"]);
            if (tex == null) continue;
            mat.SetTexture(pname, tex);
            var sc = t["second"]["m_Scale"]; var of = t["second"]["m_Offset"];
            mat.SetTextureScale(pname, new Vector2(sc["x"].AsFloat, sc["y"].AsFloat));
            mat.SetTextureOffset(pname, new Vector2(of["x"].AsFloat, of["y"].AsFloat));
        }

        foreach (var kw in bf["m_ShaderKeywords"].AsString.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            mat.EnableKeyword(kw);
        // The Prologue's custom 'Autodesk Interactive' (low-detail city blocks) is authored in cutout mode but its alpha
        // channel is not a coverage mask; Standard's alpha test would clip the geometry away. Draw it opaque.
        if (shaderName == "Autodesk Interactive" && mat.HasProperty("_Mode") && mat.GetFloat("_Mode") == 1f)
        {
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.SetFloat("_Mode", 0f);
            mat.renderQueue = -1;
            ForcedOpaque++;
        }
        if (mat.HasProperty("_EmissionMap") && mat.GetTexture("_EmissionMap") != null) mat.EnableKeyword("_EMISSION");
        if (mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") != null) mat.EnableKeyword("_NORMALMAP");
        if (mat.HasProperty("_MetallicGlossMap") && mat.GetTexture("_MetallicGlossMap") != null) mat.EnableKeyword("_METALLICGLOSSMAP");

        var queue = bf["m_CustomRenderQueue"].IsDummy ? -1 : bf["m_CustomRenderQueue"].AsInt;
        if (queue > 0) mat.renderQueue = queue;

        _materials[key] = mat;
        return mat;
    }

    private Texture2D? GetTexture(AssetsFileInstance file, AssetTypeValueField pptr)
    {
        var ext = _data.Am.GetExtAsset(file, pptr);
        if (ext.baseField == null || (AssetClassID)ext.info.TypeId != AssetClassID.Texture2D) return null;
        var key = (ext.file.name, ext.info.PathId);
        if (_textures.TryGetValue(key, out var cached)) return cached;
        Texture2D? tex = null;
        try
        {
            tex = TextureReader.Read(_data, ext.baseField, out var why);
            if (tex == null && why.Length > 0) _log($"[city] texture skipped: {why}");
        }
        catch (Exception e) { _log($"[city] texture failed: {e.GetType().Name} {e.Message}"); }
        _textures[key] = tex;
        return tex;
    }

    public int ForcedOpaque { get; private set; }

    public Material GetFallback() => Fallback();

    private Material Fallback()
    {
        if (_fallback == null) _fallback = new Material(Shader.Find("Standard")) { name = "NRMP_Fallback", color = new Color(0.5f, 0.5f, 0.55f) };
        return _fallback;
    }

    public void DestroyAll()
    {
        foreach (var m in _materials.Values) if (m != null) UnityEngine.Object.Destroy(m);
        foreach (var t in _textures.Values) if (t != null) UnityEngine.Object.Destroy(t);
        if (_fallback != null) UnityEngine.Object.Destroy(_fallback);
        _materials.Clear(); _textures.Clear(); _fallback = null;
    }
}
