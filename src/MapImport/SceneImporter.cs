using System;
using System.Collections.Generic;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace NightRunnersMP.MapImport;

/// <summary>
/// Imports the static world of one scene file — mesh renderers, mesh colliders, real-time lights,
/// baked lightmaps — rebuilding the original parent hierarchy so Unity composes transforms exactly
/// as the Prologue did (flattening breaks children of rotated, non-uniformly scaled parents).
/// Cars, UI, scripts, trigger volumes and LOD levels above 0 are skipped.
/// </summary>
public sealed class SceneImporter
{
    public sealed class Stats { public int Objects, Renderers, Colliders, Lights, Skipped, Lightmapped; public long Vertices; }

    private readonly PrologueData _data;
    private readonly MaterialReader _materials;
    private readonly Action<string> _log;
    private readonly Dictionary<(string, long), Mesh?> _meshes = new();
    private readonly List<Texture2D> _lightmapTextures = new();
    private readonly Dictionary<(string, long), int> _lightmapIndexByAsset = new();
    private readonly List<(MeshRenderer renderer, int index)> _lightmapUsers = new();
    private int _meshFailures;

    public Stats Total { get; } = new();
    public int MeshCount => _meshes.Count;

    /// <summary>Object names whose world position (Prologue coordinates) should be recorded.</summary>
    public HashSet<string> MarkerNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Vector3> Markers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Physics layer for imported colliders; the game's "Road Physics" layer.</summary>
    public int Layer { get; set; }

    /// <summary>Culling mask of the Prologue's driving camera (mainCam_0): renderers outside it were never visible.</summary>
    public const uint PrologueVisibleLayers = 0x28C5CF37;
    public int HiddenRenderers { get; private set; }
    /// <summary>The 3D-skybox panorama dome (drawn by the Prologue with a rotation-only camera); imported on layer 0.</summary>
    public string SkyboxObject { get; set; } = "C1_skybox_import";
    public List<GameObject> SkyboxRenderers { get; } = new();
    public int TriggersSkipped { get; private set; }
    /// <summary>Renderers under a zone's far-LOD proxy ("full lod"): the game hides them once the detailed area scenes stream in.</summary>
    public int FarLodSkipped { get; private set; }
    /// <summary>Objects stored inactive that the game enables at runtime (tunnel shells).</summary>
    public int ForcedActive { get; private set; }
    private static bool IsFarLodName(string name) => name.Contains("full lod", StringComparison.OrdinalIgnoreCase) || name.Contains("full_lod", StringComparison.OrdinalIgnoreCase) || name.Contains("farlod", StringComparison.OrdinalIgnoreCase);

    /// <summary>True while importing a *_COLLIDERS scene: its colliders are the drivable surfaces.</summary>
    public bool IsRoadSurfaceScene { get; set; }
    public string CurrentScene { get; set; } = "";
    public bool ImportLightmaps { get; set; } = true;
    public bool ImportSkybox { get; set; } = true;

    public sealed class RoadSurface
    {
        public string Name = "";
        public string Scene = "";
        public GameObject Object = null!;
        public Mesh Mesh = null!;
        public Vector3 PrologueCenter;
    }
    public List<RoadSurface> RoadSurfaces { get; } = new();

    /// <summary>Baked lightmaps collected across scenes, and the renderers that use them (local indices).</summary>
    public IReadOnlyList<Texture2D> LightmapTextures => _lightmapTextures;
    public IReadOnlyList<(MeshRenderer renderer, int index)> LightmapUsers => _lightmapUsers;

    /// <summary>Ambient/fog settings of the last imported scene that had them.</summary>
    public SceneLighting? Lighting { get; private set; }
    public sealed class SceneLighting
    {
        public int AmbientMode; public Color AmbientSky, AmbientEquator, AmbientGround; public float AmbientIntensity;
        public bool Fog; public Color FogColor; public int FogMode; public float FogDensity, FogStart, FogEnd;
        public float ReflectionIntensity = 1f; public int ReflectionMode; public string Scene = "";
        public Material? Skybox;
        public override string ToString() => $"from {Scene}: ambientMode={AmbientMode} sky={AmbientSky} equator={AmbientEquator} ground={AmbientGround} intensity={AmbientIntensity:F2} reflectionIntensity={ReflectionIntensity:F2} reflectionMode={ReflectionMode} fog={Fog} fogMode={FogMode} density={FogDensity:F4} color={FogColor}";
    }

    public SceneImporter(PrologueData data, MaterialReader materials, Action<string> log)
    {
        _data = data; _materials = materials; _log = log;
    }

    private sealed class Node
    {
        public AssetTypeValueField Go = null!;
        public AssetTypeValueField? Transform;
        public long TransformId, FatherId;
        public bool Active;
        public AssetTypeValueField? MeshFilter, MeshRenderer, MeshCollider, Light;
        public bool HasDynamicBody, HasWheel;
        public GameObject? Created;
        public bool FarLod;
        public Vector3 WorldPos; public Quaternion WorldRot; public Vector3 WorldScale; public bool WorldReady, WorldActive;
        public string Name => Go["m_Name"].AsString;
    }

    public Stats Import(int levelIndex, Transform parent)
    {
        var stats = new Stats();
        var inst = _data.LoadLevel(levelIndex);
        var am = _data.Am;

        // ---- index the scene -------------------------------------------------------------------
        var nodes = new Dictionary<long, Node>(); // by transform path id
        foreach (var info in inst.file.GetAssetsOfType(AssetClassID.GameObject))
        {
            var go = am.GetBaseField(inst, info);
            var node = new Node { Go = go, Active = go["m_IsActive"].AsBool };
            // Tunnel shells (_TUNNEL_OG_*) are stored inactive and switched on by the game at runtime.
            if (!node.Active && go["m_Name"].AsString.StartsWith("_TUNNEL_OG", StringComparison.OrdinalIgnoreCase)) { node.Active = true; ForcedActive++; }
            foreach (var c in go["m_Component.Array"])
            {
                var p = c["component"];
                var ext = am.GetExtAsset(inst, p);
                if (ext.baseField == null) continue;
                switch ((AssetClassID)ext.info.TypeId)
                {
                    case AssetClassID.Transform:
                    case AssetClassID.RectTransform:
                        node.Transform = ext.baseField; node.TransformId = p["m_PathID"].AsLong;
                        node.FatherId = ext.baseField["m_Father"]["m_PathID"].AsLong;
                        break;
                    case AssetClassID.MeshFilter: node.MeshFilter = ext.baseField; break;
                    case AssetClassID.MeshRenderer: node.MeshRenderer = ext.baseField; break;
                    case AssetClassID.MeshCollider: node.MeshCollider = ext.baseField; break;
                    case AssetClassID.Light: node.Light = ext.baseField; break;
                    case AssetClassID.Rigidbody: node.HasDynamicBody = !ext.baseField["m_IsKinematic"].AsBool; break;
                    case AssetClassID.WheelCollider: node.HasWheel = true; break;
                }
            }
            if (node.Transform != null) nodes[node.TransformId] = node;
        }

        var lodSkip = new HashSet<long>();
        foreach (var info in inst.file.GetAssetsOfType(AssetClassID.LODGroup))
        {
            var lods = am.GetBaseField(inst, info)["m_LODs.Array"].Children;
            for (var i = 1; i < lods.Count; i++)
                foreach (var r in lods[i]["renderers.Array"]) lodSkip.Add(r["renderer"]["m_PathID"].AsLong);
        }

        var lightmapMap = ImportLightmaps ? ReadLightmaps(inst) : new List<int>();
        ReadRenderSettings(inst);

        // ---- world transforms (for markers / vehicle detection / road surface centres) ----------
        var vehicleRoots = new HashSet<long>();
        foreach (var n in nodes.Values) if (n.HasDynamicBody || n.HasWheel) vehicleRoots.Add(n.TransformId);

        void Resolve(Node n)
        {
            if (n.WorldReady) return;
            n.WorldReady = true;
            var t = n.Transform!;
            var lp = t["m_LocalPosition"]; var lr = t["m_LocalRotation"]; var ls = t["m_LocalScale"];
            var localPos = new Vector3(lp["x"].AsFloat, lp["y"].AsFloat, lp["z"].AsFloat);
            var localRot = new Quaternion(lr["x"].AsFloat, lr["y"].AsFloat, lr["z"].AsFloat, lr["w"].AsFloat);
            var localScale = new Vector3(ls["x"].AsFloat, ls["y"].AsFloat, ls["z"].AsFloat);
            n.FarLod = IsFarLodName(n.Name);
            if (n.FatherId != 0 && nodes.TryGetValue(n.FatherId, out var father))
            {
                Resolve(father);
                n.FarLod |= father.FarLod;
                n.WorldPos = father.WorldPos + father.WorldRot * Vector3.Scale(father.WorldScale, localPos);
                n.WorldRot = father.WorldRot * localRot;
                n.WorldScale = Vector3.Scale(father.WorldScale, localScale);
                n.WorldActive = father.WorldActive && n.Active;
                if (vehicleRoots.Contains(father.TransformId)) vehicleRoots.Add(n.TransformId);
            }
            else { n.WorldPos = localPos; n.WorldRot = localRot; n.WorldScale = localScale; n.WorldActive = n.Active; }
        }

        // ---- GameObject creation with the original hierarchy ------------------------------------
        GameObject Create(Node n)
        {
            if (n.Created != null) return n.Created;
            Transform parentT = parent;
            if (n.FatherId != 0 && nodes.TryGetValue(n.FatherId, out var father)) parentT = Create(father).transform;
            var go = new GameObject(n.Name);
            go.transform.SetParent(parentT, false);
            var t = n.Transform!;
            var lp = t["m_LocalPosition"]; var lr = t["m_LocalRotation"]; var ls = t["m_LocalScale"];
            go.transform.localPosition = new Vector3(lp["x"].AsFloat, lp["y"].AsFloat, lp["z"].AsFloat);
            go.transform.localRotation = new Quaternion(lr["x"].AsFloat, lr["y"].AsFloat, lr["z"].AsFloat, lr["w"].AsFloat);
            go.transform.localScale = new Vector3(ls["x"].AsFloat, ls["y"].AsFloat, ls["z"].AsFloat);
            go.layer = 0;
            go.isStatic = true;
            if (!n.Active) go.SetActive(false);
            n.Created = go;
            stats.Objects++;
            return go;
        }

        foreach (var n in nodes.Values)
        {
            Resolve(n);
            var nodeName = n.Name;
            if (MarkerNames.Contains(nodeName) && !Markers.ContainsKey(nodeName)) Markers[nodeName] = n.WorldPos;
            if (!n.WorldActive || vehicleRoots.Contains(n.TransformId)) { stats.Skipped++; continue; }
            if (n.FarLod) { if (n.MeshRenderer != null) FarLodSkipped++; stats.Skipped++; continue; }

            var originalLayer = (int)n.Go["m_Layer"].AsUInt;
            var wantsRenderer = n.MeshRenderer != null && n.MeshFilter != null && n.MeshRenderer["m_Enabled"].AsBool;
            var isSkybox = wantsRenderer && nodeName.Equals(SkyboxObject, StringComparison.OrdinalIgnoreCase);
            if (wantsRenderer && !isSkybox && ((PrologueVisibleLayers >> originalLayer) & 1u) == 0) { wantsRenderer = false; HiddenRenderers++; }
            if (wantsRenderer && IsLodSkipped(n, lodSkip)) wantsRenderer = false;
            var wantsCollider = n.MeshCollider != null && n.MeshCollider["m_Enabled"].AsBool;
            if (wantsCollider && n.MeshCollider!["m_IsTrigger"].AsBool) { wantsCollider = false; TriggersSkipped++; }
            var wantsLight = n.Light != null && n.Light["m_Enabled"].AsBool && n.Light["m_Type"].AsInt != 1
                             && (n.Light["m_Lightmapping"].IsDummy || n.Light["m_Lightmapping"].AsInt != 2);
            if (!wantsRenderer && !wantsCollider && !wantsLight) { stats.Skipped++; continue; }

            var go = Create(n);

            Mesh? renderMesh = null;
            if (wantsRenderer)
            {
                renderMesh = GetMesh(inst, n.MeshFilter!["m_Mesh"]);
                if (renderMesh != null)
                {
                    go.layer = isSkybox ? 0 : originalLayer;
                    if (isSkybox) SkyboxRenderers.Add(go);
                    go.AddComponent<MeshFilter>().sharedMesh = renderMesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    var mats = new List<Material>();
                    foreach (var m in n.MeshRenderer!["m_Materials.Array"]) mats.Add(_materials.Get(inst, m));
                    if (mats.Count == 0) mats.Add(_materials.GetFallback());
                    mr.sharedMaterials = new Il2CppReferenceArray<Material>(mats.ToArray());
                    mr.shadowCastingMode = n.MeshRenderer["m_CastShadows"].AsInt switch
                    {
                        0 => UnityEngine.Rendering.ShadowCastingMode.Off,
                        2 => UnityEngine.Rendering.ShadowCastingMode.TwoSided,
                        3 => UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly,
                        _ => UnityEngine.Rendering.ShadowCastingMode.On,
                    };
                    mr.receiveShadows = n.MeshRenderer["m_ReceiveShadows"].AsBool;
                    // The alpha's baked light/reflection probes only cover Mount Haruna; far outside that volume they
                    // evaluate to black, which is what non-lightmapped city surfaces were getting as their ambient.
                    mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                    mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

                    var lmIndex = n.MeshRenderer["m_LightmapIndex"].AsInt;
                    if (lmIndex >= 0 && lmIndex < lightmapMap.Count && lightmapMap[lmIndex] >= 0)
                    {
                        var so = n.MeshRenderer["m_LightmapTilingOffset"];
                        mr.lightmapScaleOffset = new Vector4(so["x"].AsFloat, so["y"].AsFloat, so["z"].AsFloat, so["w"].AsFloat);
                        _lightmapUsers.Add((mr, lightmapMap[lmIndex]));
                        stats.Lightmapped++;
                    }
                    stats.Renderers++;
                }
            }
            if (wantsCollider)
            {
                var cm = GetMesh(inst, n.MeshCollider!["m_Mesh"]) ?? renderMesh;
                if (cm != null)
                {
                    var colGo = new GameObject("collider");
                    colGo.transform.SetParent(go.transform, false);
                    colGo.layer = Layer;
                    colGo.isStatic = true;
                    var mc = colGo.AddComponent<MeshCollider>();
                    mc.sharedMesh = cm;
                    mc.convex = n.MeshCollider["m_Convex"].AsBool;
                    stats.Colliders++;
                    if (IsRoadSurfaceScene)
                    {
                        var worldCenter = colGo.transform.TransformPoint(cm.bounds.center);
                        RoadSurfaces.Add(new RoadSurface { Name = nodeName, Scene = CurrentScene, Object = colGo, Mesh = cm, PrologueCenter = parent.InverseTransformPoint(worldCenter) });
                    }
                }
            }
            if (wantsLight)
            {
                var l = n.Light!;
                var light = go.AddComponent<Light>();
                light.type = l["m_Type"].AsInt == 0 ? LightType.Spot : LightType.Point;
                var c = l["m_Color"];
                light.color = new Color(c["r"].AsFloat, c["g"].AsFloat, c["b"].AsFloat, c["a"].AsFloat);
                light.intensity = l["m_Intensity"].AsFloat;
                light.range = l["m_Range"].AsFloat;
                light.spotAngle = l["m_SpotAngle"].AsFloat;
                light.shadows = LightShadows.None;
                stats.Lights++;
            }
        }

        Total.Objects += stats.Objects; Total.Renderers += stats.Renderers; Total.Colliders += stats.Colliders; Total.Lights += stats.Lights;
        Total.Skipped += stats.Skipped; Total.Lightmapped += stats.Lightmapped;
        return stats;
    }

    /// <summary>Returns this scene's lightmap slots mapped to global (deduplicated) texture indices.</summary>
    private List<int> ReadLightmaps(AssetsFileInstance inst)
    {
        var map = new List<int>();
        foreach (var info in inst.file.GetAssetsOfType(AssetClassID.LightmapSettings))
        {
            foreach (var lm in _data.Am.GetBaseField(inst, info)["m_Lightmaps.Array"].Children)
            {
                var ext = _data.Am.GetExtAsset(inst, lm["m_Lightmap"]);
                if (ext.baseField == null) { map.Add(-1); continue; }
                var key = (ext.file.name, ext.info.PathId);
                if (!_lightmapIndexByAsset.TryGetValue(key, out var index))
                {
                    Texture2D? tex = null;
                    try { tex = TextureReader.Read(_data, ext.baseField, out var why); if (tex == null && why.Length > 0) _log($"[city] lightmap skipped: {why}"); }
                    catch (Exception e) { _log($"[city] lightmap failed: {e.GetType().Name} {e.Message}"); }
                    index = tex == null ? -1 : _lightmapTextures.Count;
                    if (tex != null) _lightmapTextures.Add(tex);
                    _lightmapIndexByAsset[key] = index;
                }
                map.Add(index);
            }
            break;
        }
        return map;
    }

    /// <summary>Scene whose render settings drive the city (the main road scene); others are ignored once it is seen.</summary>
    public string LightingScene { get; set; } = "C1_1";

    private void ReadRenderSettings(AssetsFileInstance inst)
    {
        if (Lighting != null && Lighting.Scene == LightingScene) return;
        if (Lighting != null && CurrentScene != LightingScene) return;
        foreach (var info in inst.file.GetAssetsOfType(AssetClassID.RenderSettings))
        {
            var bf = _data.Am.GetBaseField(inst, info);
            Color C(AssetTypeValueField f) => f.IsDummy ? Color.black : new Color(f["r"].AsFloat, f["g"].AsFloat, f["b"].AsFloat, f["a"].AsFloat);
            Lighting = new SceneLighting
            {
                AmbientMode = bf["m_AmbientMode"].IsDummy ? 0 : bf["m_AmbientMode"].AsInt,
                AmbientSky = C(bf["m_AmbientSkyColor"]),
                AmbientEquator = C(bf["m_AmbientEquatorColor"]),
                AmbientGround = C(bf["m_AmbientGroundColor"]),
                AmbientIntensity = bf["m_AmbientIntensity"].IsDummy ? 1f : bf["m_AmbientIntensity"].AsFloat,
                Fog = !bf["m_Fog"].IsDummy && bf["m_Fog"].AsBool,
                FogColor = C(bf["m_FogColor"]),
                FogMode = bf["m_FogMode"].IsDummy ? 3 : bf["m_FogMode"].AsInt,
                FogDensity = bf["m_FogDensity"].IsDummy ? 0f : bf["m_FogDensity"].AsFloat,
                FogStart = bf["m_LinearFogStart"].IsDummy ? 0f : bf["m_LinearFogStart"].AsFloat,
                FogEnd = bf["m_LinearFogEnd"].IsDummy ? 300f : bf["m_LinearFogEnd"].AsFloat,
                ReflectionIntensity = bf["m_ReflectionIntensity"].IsDummy ? 1f : bf["m_ReflectionIntensity"].AsFloat,
                ReflectionMode = bf["m_DefaultReflectionMode"].IsDummy ? 0 : bf["m_DefaultReflectionMode"].AsInt,
                Scene = CurrentScene,
            };
            if (ImportSkybox && !bf["m_SkyboxMaterial"].IsDummy) Lighting.Skybox = SkyboxReader.Read(_data, inst, bf["m_SkyboxMaterial"], _log);
            break;
        }
    }

    private static bool IsLodSkipped(Node n, HashSet<long> lodSkip)
    {
        foreach (var c in n.Go["m_Component.Array"])
            if (lodSkip.Contains(c["component"]["m_PathID"].AsLong)) return true;
        return false;
    }

    private Mesh? GetMesh(AssetsFileInstance file, AssetTypeValueField pptr)
    {
        var ext = _data.Am.GetExtAsset(file, pptr);
        if (ext.baseField == null) return null;
        var key = (ext.file.name, ext.info.PathId);
        if (_meshes.TryGetValue(key, out var cached)) return cached;
        Mesh? mesh = null;
        try
        {
            mesh = MeshReader.Read(_data, ext.baseField, out var verts);
            Total.Vertices += verts;
        }
        catch (Exception e)
        {
            if (_meshFailures++ < 3) _log($"[city] mesh '{ext.baseField["m_Name"].AsString}' failed:\n{e}");
            else if (_meshFailures < 20) _log($"[city] mesh '{ext.baseField["m_Name"].AsString}' failed: {e.GetType().Name} {e.Message}");
            else if (_meshFailures == 20) _log("[city] further mesh failures suppressed");
        }
        _meshes[key] = mesh;
        return mesh;
    }

    public void DestroyAll()
    {
        foreach (var m in _meshes.Values) if (m != null) UnityEngine.Object.Destroy(m);
        foreach (var t in _lightmapTextures) if (t != null) UnityEngine.Object.Destroy(t);
        _meshes.Clear();
        _lightmapTextures.Clear();
        _lightmapIndexByAsset.Clear();
        _lightmapUsers.Clear();
    }
}
