using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using NightRunnersMP.Sync;
using UnityEngine;

namespace NightRunnersMP.MapImport;

/// <summary>
/// The C1 Tatsumi city from the player's own Prologue install, rebuilt inside the alpha at a
/// far-away origin so it never overlaps Mount Haruna. Loads scene by scene across frames.
/// </summary>
public sealed class CityMap
{
    public enum State { Unloaded, Loading, Loaded, Failed }

    private readonly Action<string> _log;
    private readonly string? _configuredGameDir;
    private readonly string _classDataPath;

    private GameObject? _root;
    private PrologueData? _data;
    private MaterialReader? _materials;
    private SceneImporter? _importer;
    private object? _routine;

    public State Current { get; private set; } = State.Unloaded;
    public string Status { get; private set; } = "not loaded";
    public int ScenesDone { get; private set; }
    public int ScenesTotal { get; private set; }
    public bool TeleportWhenLoaded;

    /// <summary>World position of the Prologue's (0,0,0); far from Haruna. Y is aligned to the player at load time.</summary>
    public Vector3 Origin = new(30000f, 0f, 30000f);
    /// <summary>Where the player lands, in Prologue coordinates (replaced by the Start marker when found).</summary>
    public Vector3 SpawnPoint = new(3.7f, 20.5f, 25.4f);
    public float SpawnYaw;
    /// <summary>Name of the object in the Prologue's city scene whose position is the spawn.</summary>
    public string SpawnMarker = "Start";
    /// <summary>Collider scene whose surfaces host the default spawn when no marker exists (the Tatsumi PA area).</summary>
    public string SpawnScene = "C1_AREA_1_TATSUMI_COLLIDERS";

    public CityMap(string? configuredGameDir, string classDataPath, Action<string> log)
    {
        _configuredGameDir = configuredGameDir;
        _classDataPath = classDataPath;
        _log = log;
    }

    public bool IsLoaded => Current == State.Loaded;

    public void BeginLoad()
    {
        if (Current is State.Loading or State.Loaded) return;
        _routine = MelonCoroutines.Start(LoadRoutine());
    }

    private IEnumerator LoadRoutine()
    {
        Current = State.Loading;
        ScenesDone = 0; ScenesTotal = 0;
        Status = "locating your Prologue install…";
        yield return null;

        var dataDir = PrologueData.LocateDataDir(_configuredGameDir, _log);
        if (dataDir == null) { Fail("Prologue install not found — set PrologueDir in the config"); yield break; }
        if (!File.Exists(_classDataPath)) { Fail($"missing {_classDataPath} (reinstall the mod)"); yield break; }
        _log($"[city] reading {dataDir}");

        try { _data = new PrologueData(dataDir, _classDataPath); }
        catch (Exception e) { Fail($"could not open Prologue data: {e.GetType().Name} {e.Message}"); yield break; }

        // Keep the city at the player's current height: the alpha's fall-catcher resets anyone far below
        // the roads, and the shifted origin can put "world y = 0" hundreds of metres down.
        var rcc = LocalCar.Rcc; // also used to probe the road layer below
        if (rcc != null)
        {
            var playerWorldY = WorldOrigin.ToWorld(rcc.transform.position).y;
            Origin.y = playerWorldY - SpawnPoint.y;
            _log($"[city] origin set to {Origin} so the spawn matches your current height (world y {playerWorldY:F1})");
        }

        _root = new GameObject("NRMP_City");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        _root.transform.position = WorldOrigin.ToLocal(Origin);
        RegisterWithFloatingOrigin(_root);

        _materials = new MaterialReader(_data, _log);
        _importer = new SceneImporter(_data, _materials, _log) { Layer = DetectRoadLayer(rcc), ImportLightmaps = UseLightmaps, ImportSkybox = UseSkybox };
        foreach (var m in new[] { SpawnMarker, "meetSpot_start", "walkScene_player", "cinematic_playerStart", "TATSUMI_MEET_COLLIDER" }) _importer.MarkerNames.Add(m);

        // The driving world is C1_1 plus the streamed C1_AREA_* scenes. C1_TATSUMI is the walk-around meet-spot
        // scene: it carries its own low-detail copy of the whole city in a different coordinate frame.
        var scenes = new List<int>();
        for (var i = 0; i < _data.Scenes.Count; i++)
        {
            var n = PrologueData.SceneShortName(_data.Scenes[i]);
            if (n.Equals("C1_1", StringComparison.OrdinalIgnoreCase) || n.StartsWith("C1_AREA_", StringComparison.OrdinalIgnoreCase)) scenes.Add(i);
            else if (n.StartsWith("C1_", StringComparison.OrdinalIgnoreCase)) _log($"[city] skipping {n} (not part of the driving world)");
        }
        ScenesTotal = scenes.Count;

        var sw = Stopwatch.StartNew();
        foreach (var idx in scenes)
        {
            var name = PrologueData.SceneShortName(_data.Scenes[idx]);
            Status = $"loading {name} ({ScenesDone + 1}/{ScenesTotal})…";
            yield return null;
            try
            {
                _importer.IsRoadSurfaceScene = name.EndsWith("_COLLIDERS", StringComparison.OrdinalIgnoreCase);
                _importer.CurrentScene = name;
                var st = _importer.Import(idx, _root.transform);
                _log($"[city] {name}: {st.Objects} objects, {st.Renderers} renderers ({st.Lightmapped} lightmapped), {st.Colliders} colliders, {st.Lights} lights");
            }
            catch (Exception e)
            {
                _log($"[city] {name} FAILED: {e.GetType().Name} {e.Message}");
            }
            ScenesDone++;
        }

        foreach (var kv in _importer.Markers) _log($"[city] marker '{kv.Key}' at {kv.Value} (Prologue coords)");
        if (_importer.Markers.TryGetValue(SpawnMarker, out var marker))
        {
            // Re-align the origin so the marker sits at the height the spawn was planned for.
            Origin.y += SpawnPoint.y - marker.y;
            _root.transform.position = WorldOrigin.ToLocal(Origin);
            SpawnPoint = marker;
            _log($"[city] spawn = marker '{SpawnMarker}' {marker}; origin now {Origin}");
        }
        else
        {
            // No marker in the driving scenes: spawn in the Tatsumi PA area, on its most central drivable surface.
            var pa = new List<SceneImporter.RoadSurface>();
            foreach (var rs in _importer.RoadSurfaces) if (rs.Scene.StartsWith(SpawnScene, StringComparison.OrdinalIgnoreCase)) pa.Add(rs);
            if (pa.Count > 0)
            {
                var centroid = Vector3.zero;
                foreach (var rs in pa) centroid += rs.PrologueCenter;
                centroid /= pa.Count;
                SceneImporter.RoadSurface? pick = null; var best = float.MaxValue;
                foreach (var rs in pa) { var d = Vector3.Distance(rs.PrologueCenter, centroid); if (d < best) { best = d; pick = rs; } }
                Origin.y += SpawnPoint.y - pick!.PrologueCenter.y;
                _root.transform.position = WorldOrigin.ToLocal(Origin);
                SpawnPoint = pick.PrologueCenter;
                _log($"[city] spawn = centre of '{pick.Name}' in {pick.Scene} {SpawnPoint}; origin now {Origin}");
            }
            else _log($"[city] marker '{SpawnMarker}' not found and no {SpawnScene} surfaces; using configured spawn {SpawnPoint}");
        }

        // Prefer landing on an actual road: the drivable surface nearest the spawn point.
        _roadIndex = -1;
        if (_importer.RoadSurfaces.Count > 0)
        {
            var best = float.MaxValue;
            for (var i = 0; i < _importer.RoadSurfaces.Count; i++)
            {
                var d = Vector3.Distance(_importer.RoadSurfaces[i].PrologueCenter, SpawnPoint);
                if (d < best) { best = d; _roadIndex = i; }
            }
            _log($"[city] {_importer.RoadSurfaces.Count} drivable surfaces; nearest to the spawn is '{_importer.RoadSurfaces[_roadIndex].Name}' ({best:F0} m away). {_importer.TriggersSkipped} trigger volumes and {_importer.HiddenRenderers} camera-hidden renderers skipped.");
        }
        BuildTargets();
        LoadBookmarks();

        ApplyLightmaps();
        SetupSkydome();
        SetupGround();
        DiagnoseRenderers("PERMA_ISLAND");
        _log($"[city] materials forced opaque (Autodesk Interactive cutout): {_materials.ForcedOpaque}");
        if (_importer.Lighting != null) _log($"[city] Prologue scene lighting: {_importer.Lighting}");
        LogAlphaLighting();

        _log($"[city] enabled {_importer.ForcedActive} stored-inactive tunnel shells; skipped: {_importer.FarLodSkipped} far-LOD proxy renderers, {_importer.HiddenRenderers} hidden-layer renderers, {_importer.TriggersSkipped} trigger colliders");
        var t = _importer.Total;
        Status = $"loaded: {t.Renderers} renderers ({t.Lightmapped} lightmapped, {_importer.LightmapTextures.Count} lightmaps), {t.Colliders} colliders, {t.Lights} lights, {t.Vertices:N0} verts, {_materials.MaterialCount} materials, {_materials.TextureCount} textures in {sw.Elapsed.TotalSeconds:F0} s";
        _log($"[city] {Status}");
        Current = State.Loaded;
        _routine = null;
        if (TeleportWhenLoaded) { TeleportWhenLoaded = false; TeleportPlayer(); }
    }

    private void Fail(string why)
    {
        Status = why;
        _log($"[city] {why}");
        Current = State.Failed;
        _routine = null;
    }

    // ---- lighting ------------------------------------------------------------------------------

    private Il2CppReferenceArray<LightmapData>? _originalLightmaps;
    private (int mode, Color sky, Color equator, Color ground, float intensity, bool fog, Color fogColor, FogMode fogMode, float density, float start, float end, float reflection)? _originalRenderSettings;
    public bool UseLightmaps = true;
    public bool UseSceneLighting = true;
    public bool SceneLightingActive { get; private set; }

    /// <summary>Append the city's baked lightmaps to the game's and point the imported renderers at them.</summary>
    private void ApplyLightmaps()
    {
        if (!UseLightmaps || _importer == null || _importer.LightmapTextures.Count == 0) return;
        try
        {
            var existing = LightmapSettings.lightmaps;
            _originalLightmaps = existing;
            var baseIndex = existing != null ? existing.Length : 0;
            var alphaFormat = "none";
            if (existing != null) for (var i = 0; i < existing.Length; i++) { var c = existing[i]?.lightmapColor; if (c != null) { alphaFormat = $"{c.format} {c.width}x{c.height}"; break; } }
            var ours = _importer.LightmapTextures[0];
            _log($"[city] lightmap formats: alpha {alphaFormat}, Prologue {ours.format} {ours.width}x{ours.height}");
            var combined = new LightmapData[baseIndex + _importer.LightmapTextures.Count];
            for (var i = 0; i < baseIndex; i++) combined[i] = existing![i];
            for (var i = 0; i < _importer.LightmapTextures.Count; i++)
                combined[baseIndex + i] = new LightmapData { lightmapColor = _importer.LightmapTextures[i] };
            LightmapSettings.lightmaps = new Il2CppReferenceArray<LightmapData>(combined);
            foreach (var (renderer, index) in _importer.LightmapUsers)
                if (renderer != null) renderer.lightmapIndex = baseIndex + index;
            _log($"[city] lightmaps: {baseIndex} existing + {_importer.LightmapTextures.Count} imported; {_importer.LightmapUsers.Count} renderers use them (mode {LightmapSettings.lightmapsMode})");
        }
        catch (Exception e) { _log($"[city] lightmaps failed: {e.GetType().Name} {e.Message}"); }
    }

    // ---- 3D skybox -------------------------------------------------------------------------------

    private GameObject? _skydome;
    private float _skydomeMeshRadius = 25.7f;
    public bool UseSkydome = true;
    public float SkydomeRadius = 5000f;
    private float? _savedFarClip;
    private Material? _savedSkybox;
    private bool _skyboxSwapped;
    public bool UseSkybox = true;

    /// <summary>
    /// The Prologue renders C1_skybox_import - a shallow textured dome about 26 m across - with a second camera that
    /// copies only the main camera's rotation, i.e. an infinitely distant panorama. Reproduce it by scaling the dome up to
    /// just inside the far clip plane, centring it on the main camera every frame and drawing it unlit and transparent.
    /// </summary>
    private void SetupSkydome()
    {
        if (!UseSkydome || _importer == null || _importer.SkyboxRenderers.Count == 0) { _log("[city] no 3D skybox found"); return; }
        try
        {
            var sky = _importer.SkyboxRenderers[0];
            var mf = sky.GetComponent<MeshFilter>();
            var mr = sky.GetComponent<MeshRenderer>();
            if (mf == null || mr == null || mf.sharedMesh == null) return;
            // Extent as placed: the Prologue's dome parent carries scale 0.25, the dome itself ~1576.
            var parentScale = sky.transform.parent != null ? sky.transform.parent.localScale.x : 1f;
            var ext = mf.sharedMesh.bounds.extents;
            _skydomeMeshRadius = Mathf.Max(ext.x, ext.z) * sky.transform.localScale.x * parentScale;
            if (_skydomeMeshRadius < 1f) _skydomeMeshRadius = 25.7f;

            _skydome = new GameObject("NRMP_Skydome");
            UnityEngine.Object.DontDestroyOnLoad(_skydome);
            var keepLocalPos = sky.transform.localPosition * parentScale;
            var keepLocalRot = sky.transform.localRotation;
            var keepLocalScale = sky.transform.localScale * parentScale;
            sky.transform.SetParent(_skydome.transform, false);
            sky.transform.localPosition = keepLocalPos;
            sky.transform.localRotation = keepLocalRot;
            sky.transform.localScale = keepLocalScale;
            sky.layer = 0;
            sky.SetActive(true);

            var shader = Shader.Find("Unlit/Transparent") ?? Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default") ?? Shader.Find("Standard");
            var src = mr.sharedMaterial;
            var mat = new Material(shader) { name = "NRMP_Skydome" };
            if (src != null)
            {
                if (src.HasProperty("_MainTex")) mat.mainTexture = src.GetTexture("_MainTex");
                if (src.HasProperty("_Color") && mat.HasProperty("_Color")) mat.color = src.GetColor("_Color");
            }
            mat.renderQueue = 2999; // first of the transparents: opaque geometry in front of it wins the depth test
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightmapIndex = -1;
            UpdateSkydome(true);
            var wb = mr.bounds;
            _log($"[city] 3D skybox runtime: active={sky.activeInHierarchy} layer={sky.layer} bounds centre {wb.center} size {wb.size} (camera at {(Camera.main != null ? Camera.main.transform.position : Vector3.zero)}) mat queue {mat.renderQueue} tex {(mat.mainTexture is Texture2D t2 ? $"{t2.width}x{t2.height} {t2.format}" : "none")} vertexColors={(mf.sharedMesh.colors.Length > 0)}");
            _log($"[city] 3D skybox: dome mesh radius {_skydomeMeshRadius:F1} m scaled to {SkydomeRadius:F0} m, shader '{shader?.name}', texture {(mat.mainTexture == null ? "none" : mat.mainTexture.name)}");
        }
        catch (Exception e) { _log($"[city] skydome failed: {e.GetType().Name} {e.Message}"); }
    }

    public bool UseGround = true;
    /// <summary>Prologue-frame height of the ground plane: below the deepest tunnel so nothing pokes through it.</summary>
    public float GroundHeight = -40f;

    /// <summary>
    /// The Prologue has no ground mesh: below the horizon you see its sky cubemap. When that sky cannot be shown
    /// (no cubemap shader in this game), lay a flat plane far below the city in the sky's lower-hemisphere tone.
    /// </summary>
    private void SetupGround()
    {
        if (!UseGround || _root == null || _importer?.Lighting == null) return;
        if (_importer.Lighting.Skybox != null) return; // the real sky covers the lower hemisphere
        var tone = _importer.Lighting.GroundColor ?? new Color(0.06f, 0.07f, 0.09f, 1f);
        try
        {
            var go = new GameObject("NRMP_Ground");
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = new Vector3(1200f, GroundHeight, -1350f); // centre of the C1 driving world
            const float half = 12000f;
            var mesh = new Mesh { name = "NRMP_Ground" };
            mesh.vertices = new Il2CppStructArray<Vector3>(new[] { new Vector3(-half, 0, -half), new Vector3(-half, 0, half), new Vector3(half, 0, half), new Vector3(half, 0, -half) });
            mesh.normals = new Il2CppStructArray<Vector3>(new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
            mesh.triangles = new Il2CppStructArray<int>(new[] { 0, 1, 2, 0, 2, 3 });
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            var mat = new Material(Shader.Find("Standard")) { name = "NRMP_Ground", color = Color.black };
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", tone);
            mat.SetFloat("_Glossiness", 0f);
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _log($"[city] ground plane at Prologue y {GroundHeight:F0} in tone {tone}");
        }
        catch (Exception e) { _log($"[city] ground plane failed: {e.GetType().Name} {e.Message}"); }
    }

    /// <summary>Keep the dome centred on the camera and sized to the far clip plane. Call every LateUpdate.</summary>
    public void UpdateSkydome(bool force = false)
    {
        if (_skydome == null) return;
        var cam = Camera.main;
        if (cam == null) return;
        var radius = Mathf.Min(SkydomeRadius, cam.farClipPlane * 0.9f);
        var k = radius / _skydomeMeshRadius;
        if (force || Mathf.Abs(_skydome.transform.localScale.x - k) > 0.01f) _skydome.transform.localScale = Vector3.one * k;
        _skydome.transform.position = cam.transform.position;
    }

    public void SetSkydome(bool on)
    {
        if (_skydome != null) _skydome.SetActive(on);
    }

    private void ApplyFarClip(bool on)
    {
        var cam = Camera.main;
        if (cam == null) return;
        if (on && _savedFarClip == null)
        {
            _savedFarClip = cam.farClipPlane;
            if (cam.farClipPlane < SkydomeRadius * 1.15f) cam.farClipPlane = SkydomeRadius * 1.15f;
            _log($"[city] camera far clip {_savedFarClip:F0} -> {cam.farClipPlane:F0}");
        }
        else if (!on && _savedFarClip != null)
        {
            cam.farClipPlane = _savedFarClip.Value;
            _savedFarClip = null;
        }
    }

    /// <summary>Log the runtime state of imported renderers whose name contains the filter (why is X not visible?).</summary>
    private void DiagnoseRenderers(string filter)
    {
        if (_root == null) return;
        try
        {
            var count = 0;
            foreach (var mr in _root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (mr == null || !mr.gameObject.name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                var mat = mr.sharedMaterial;
                var mf = mr.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                var b = mr.bounds;
                _log($"[city] diag '{mr.gameObject.name}': activeInHierarchy={mr.gameObject.activeInHierarchy} enabled={mr.enabled} layer={mr.gameObject.layer} probes={mr.lightProbeUsage}/{mr.reflectionProbeUsage} lightmap={mr.lightmapIndex} mesh={(mesh == null ? "null" : $"{mesh.vertexCount} verts, {mesh.subMeshCount} sub")} mats={mr.sharedMaterials.Length} shader={(mat == null ? "null" : mat.shader.name)} queue={(mat == null ? -1 : mat.renderQueue)} keywords=[{(mat == null ? "" : string.Join(' ', mat.shaderKeywords))}] mainTex={(mat == null || mat.mainTexture == null ? "none" : mat.mainTexture.name)} color={(mat == null ? Color.clear : mat.color)} world bounds centre {WorldOrigin.ToWorld(b.center)} size {b.size} localPos {mr.transform.position}");
                if (++count >= 6) break;
            }
            if (count == 0) _log($"[city] diag: no renderer named like '{filter}'");
        }
        catch (Exception e) { _log($"[city] diag failed: {e.GetType().Name} {e.Message}"); }
    }

    private void RestoreLightmaps()
    {
        if (_originalLightmaps == null) return;
        try { LightmapSettings.lightmaps = _originalLightmaps; } catch { }
        _originalLightmaps = null;
    }

    private void LogAlphaLighting()
    {
        try
        {
            var suns = "";
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                if (l != null && l.type == LightType.Directional && l.enabled) suns += $" '{l.gameObject.name}' intensity {l.intensity:F2} color {l.color};";
            _log($"[city] alpha lighting now: ambientMode={RenderSettings.ambientMode} sky={RenderSettings.ambientSkyColor} intensity={RenderSettings.ambientIntensity:F2} fog={RenderSettings.fog} fogMode={RenderSettings.fogMode} density={RenderSettings.fogDensity:F4}; directional lights:{suns}");
        }
        catch (Exception e) { _log($"[city] could not read alpha lighting: {e.GetType().Name}"); }
    }

    /// <summary>Use the Prologue's ambient light and fog while in the city; restore the alpha's when leaving.</summary>
    public void SetSceneLighting(bool on)
    {
        if (!UseSceneLighting || _importer?.Lighting == null) return;
        try
        {
            if (on && !SceneLightingActive)
            {
                _originalRenderSettings = (
                    (int)RenderSettings.ambientMode, RenderSettings.ambientSkyColor, RenderSettings.ambientEquatorColor, RenderSettings.ambientGroundColor,
                    RenderSettings.ambientIntensity, RenderSettings.fog, RenderSettings.fogColor, RenderSettings.fogMode, RenderSettings.fogDensity,
                    RenderSettings.fogStartDistance, RenderSettings.fogEndDistance, RenderSettings.reflectionIntensity);
                var s = _importer.Lighting;
                RenderSettings.reflectionIntensity = s.ReflectionIntensity;
                // Skybox ambient would sample the alpha's sky; use the Prologue's sky colour as a flat ambient instead.
                RenderSettings.ambientMode = s.AmbientMode == 0 ? UnityEngine.Rendering.AmbientMode.Flat : (UnityEngine.Rendering.AmbientMode)s.AmbientMode;
                RenderSettings.ambientSkyColor = s.AmbientSky;
                RenderSettings.ambientLight = s.AmbientSky;
                RenderSettings.ambientEquatorColor = s.AmbientEquator;
                RenderSettings.ambientGroundColor = s.AmbientGround;
                RenderSettings.ambientIntensity = s.AmbientIntensity;
                RenderSettings.fog = s.Fog;
                RenderSettings.fogColor = s.FogColor;
                RenderSettings.fogMode = (FogMode)s.FogMode;
                RenderSettings.fogDensity = s.FogDensity;
                RenderSettings.fogStartDistance = s.FogStart;
                RenderSettings.fogEndDistance = s.FogEnd;
                SceneLightingActive = true;
                ApplyFarClip(true);
                SetSkydome(true);
                if (UseSkybox && s.Skybox != null && !_skyboxSwapped)
                {
                    _savedSkybox = RenderSettings.skybox;
                    RenderSettings.skybox = s.Skybox;
                    _skyboxSwapped = true;
                    _log($"[city] skybox swapped: '{(_savedSkybox == null ? "none" : _savedSkybox.name)}' -> '{s.Skybox.name}'");
                }
                _log($"[city] applied the Prologue's ambient light, reflections ({s.ReflectionIntensity:F2}) and fog (alpha reflection intensity was {_originalRenderSettings.Value.reflection:F2})");
            }
            else if (!on && SceneLightingActive && _originalRenderSettings != null)
            {
                var o = _originalRenderSettings.Value;
                RenderSettings.ambientMode = (UnityEngine.Rendering.AmbientMode)o.mode;
                RenderSettings.ambientSkyColor = o.sky; RenderSettings.ambientEquatorColor = o.equator; RenderSettings.ambientGroundColor = o.ground;
                RenderSettings.ambientIntensity = o.intensity;
                RenderSettings.fog = o.fog; RenderSettings.fogColor = o.fogColor; RenderSettings.fogMode = o.fogMode; RenderSettings.fogDensity = o.density;
                RenderSettings.fogStartDistance = o.start; RenderSettings.fogEndDistance = o.end;
                RenderSettings.reflectionIntensity = o.reflection;
                SceneLightingActive = false;
                _originalRenderSettings = null;
                ApplyFarClip(false);
                SetSkydome(false);
                if (_skyboxSwapped) { RenderSettings.skybox = _savedSkybox; _skyboxSwapped = false; }
                _log("[city] restored the alpha's ambient light and fog");
            }
        }
        catch (Exception e) { _log($"[city] scene lighting failed: {e.GetType().Name} {e.Message}"); }
    }

    public void Unload()
    {
        SetSceneLighting(false);
        RestoreLightmaps();
        if (_skydome != null) { UnityEngine.Object.Destroy(_skydome); _skydome = null; }
        if (_routine != null) { try { MelonCoroutines.Stop(_routine); } catch { } _routine = null; }
        if (_root != null) UnityEngine.Object.Destroy(_root);
        _importer?.DestroyAll();
        _materials?.DestroyAll();
        _data?.Dispose();
        _root = null; _importer = null; _materials = null; _data = null;
        Current = State.Unloaded;
        Status = "not loaded";
        _log("[city] unloaded");
    }

    private int _roadIndex = -1;

    /// <summary>Move to the next/previous drivable surface (chat: /tp next, /tp prev).</summary>
    public bool TeleportToNextRoad(int step = 1)
    {
        if (_importer == null || _importer.RoadSurfaces.Count == 0) return false;
        var n = _importer.RoadSurfaces.Count;
        _roadIndex = ((_roadIndex + step) % n + n) % n;
        return TeleportPlayer();
    }

    /// <summary>A point on the chosen road surface, in local coordinates, or null if no surface is usable.</summary>
    private Vector3? RoadSpawn(out string where, out Quaternion facing)
    {
        where = ""; facing = Quaternion.identity;
        if (_importer == null || _roadIndex < 0 || _roadIndex >= _importer.RoadSurfaces.Count) return null;
        var rs = _importer.RoadSurfaces[_roadIndex];
        if (rs.Object == null || rs.Mesh == null) return null;

        // The vertex nearest the mesh centre is on the surface by construction; confirm with a raycast.
        var verts = rs.Mesh.vertices;
        var center = rs.Mesh.bounds.center;
        var bestV = center; var bestD = float.MaxValue;
        for (var i = 0; i < verts.Length; i++)
        {
            var d = (verts[i] - center).sqrMagnitude;
            if (d < bestD) { bestD = d; bestV = verts[i]; }
        }
        var world = rs.Object.transform.TransformPoint(bestV);
        var mask = 1 << rs.Object.layer;
        if (Physics.Raycast(world + Vector3.up * 3f, Vector3.down, out var hit, 20f, mask, QueryTriggerInteraction.Ignore))
        {
            where = $"'{rs.Name}' ({_roadIndex + 1}/{_importer.RoadSurfaces.Count})";
            // Face along the road: use the surface's longest horizontal bounds axis.
            var size = rs.Mesh.bounds.size;
            var along = rs.Object.transform.TransformDirection(size.x >= size.z ? Vector3.right : Vector3.forward);
            along.y = 0f;
            facing = along.sqrMagnitude > 0.01f ? Quaternion.LookRotation(along.normalized, Vector3.up) : Quaternion.identity;
            return hit.point + Vector3.up * 1.0f;
        }
        return null;
    }

    public bool TeleportPlayer()
    {
        if (!IsLoaded) return false;
        var target = WorldOrigin.ToLocal(Origin + SpawnPoint) + Vector3.up * 1.5f;
        var rot = Quaternion.Euler(0f, SpawnYaw, 0f);
        var road = RoadSpawn(out var where, out var facing);
        if (road != null) { target = road.Value; rot = facing; _log($"[city] spawning on road {where}"); }
        else _log("[city] no usable road surface; spawning at the marker");
        return TeleportToLocal(target, rot);
    }

    // ---- teleport control ----------------------------------------------------------------------

    public sealed class Target
    {
        public string Label = "";
        public string Hint = "";
        public List<int> Surfaces = new();
        public int Cursor;
    }

    public List<Target> Targets { get; } = new();
    public Dictionary<string, (Vector3 prologue, float yaw)> Bookmarks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string BookmarkFile = "";

    private (Vector3 world, Quaternion rot)? _returnPose; // where the player was on Mount Haruna

    private void BuildTargets()
    {
        Targets.Clear();
        if (_importer == null) return;
        var byScene = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _importer.RoadSurfaces.Count; i++)
        {
            var scene = _importer.RoadSurfaces[i].Scene;
            if (!byScene.TryGetValue(scene, out var t))
            {
                byScene[scene] = t = new Target { Label = PrettyScene(scene) };
                Targets.Add(t);
            }
            t.Surfaces.Add(i);
        }
        foreach (var t in Targets) t.Hint = $"{t.Surfaces.Count} road piece{(t.Surfaces.Count == 1 ? "" : "s")}";
        Targets.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
    }

    private static string PrettyScene(string scene)
    {
        var s = scene;
        if (s.StartsWith("C1_", StringComparison.OrdinalIgnoreCase)) s = s[3..];
        if (s.EndsWith("_COLLIDERS", StringComparison.OrdinalIgnoreCase)) s = s[..^10];
        return s.Replace('_', ' ');
    }

    /// <summary>Teleport into a tile; repeated calls walk through its road pieces.</summary>
    public bool TeleportToTarget(Target t)
    {
        if (t.Surfaces.Count == 0) return false;
        _roadIndex = t.Surfaces[t.Cursor % t.Surfaces.Count];
        t.Cursor++;
        return TeleportPlayer();
    }

    public Target? FindTarget(string query)
    {
        var q = query.Trim().Replace('_', ' ');
        return Targets.Find(t => t.Label.Equals(q, StringComparison.OrdinalIgnoreCase))
            ?? Targets.Find(t => t.Label.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    public bool TeleportToPrologueCoords(Vector3 prologue, float yaw)
    {
        if (!IsLoaded) return false;
        return TeleportToLocal(WorldOrigin.ToLocal(Origin + prologue) + Vector3.up * 1f, Quaternion.Euler(0f, yaw, 0f));
    }

    public bool TeleportBack()
    {
        var rcc = LocalCar.Rcc;
        if (rcc == null || _returnPose == null) return false;
        var (world, rot) = _returnPose.Value;
        var ok = TeleportToLocal(WorldOrigin.ToLocal(world), rot, remember: false);
        if (ok) { _returnPose = null; SetSceneLighting(false); _log("[city] back on Mount Haruna"); }
        return ok;
    }

    public bool HasReturnPose => _returnPose != null;

    /// <summary>Current position in Prologue coordinates (for bookmarks).</summary>
    public (Vector3 prologue, float yaw)? CurrentProloguePose()
    {
        var rcc = LocalCar.Rcc;
        if (rcc == null || !IsLoaded) return null;
        return (WorldOrigin.ToWorld(rcc.transform.position) - Origin, rcc.transform.eulerAngles.y);
    }

    public bool SaveBookmark(string name)
    {
        var pose = CurrentProloguePose();
        if (pose == null) return false;
        Bookmarks[name] = pose.Value;
        try
        {
            var lines = new List<string> { "# name=x,y,z,yaw  (Prologue coordinates)" };
            foreach (var kv in Bookmarks) lines.Add($"{kv.Key}={kv.Value.prologue.x:F2},{kv.Value.prologue.y:F2},{kv.Value.prologue.z:F2},{kv.Value.yaw:F1}");
            File.WriteAllLines(BookmarkFile, lines);
        }
        catch (Exception e) { _log($"[city] could not save bookmarks: {e.Message}"); }
        return true;
    }

    private void LoadBookmarks()
    {
        Bookmarks.Clear();
        if (BookmarkFile.Length == 0 || !File.Exists(BookmarkFile)) return;
        try
        {
            foreach (var raw in File.ReadAllLines(BookmarkFile))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var parts = line[(eq + 1)..].Split(',');
                if (parts.Length < 3) continue;
                if (!float.TryParse(parts[0], out var x) || !float.TryParse(parts[1], out var y) || !float.TryParse(parts[2], out var z)) continue;
                var yaw = parts.Length > 3 && float.TryParse(parts[3], out var w) ? w : 0f;
                Bookmarks[line[..eq].Trim()] = (new Vector3(x, y, z), yaw);
            }
            if (Bookmarks.Count > 0) _log($"[city] {Bookmarks.Count} bookmark(s) loaded");
        }
        catch (Exception e) { _log($"[city] could not read bookmarks: {e.Message}"); }
    }

    private bool TeleportToLocal(Vector3 target, Quaternion rot, bool remember = true)
    {
        var rcc = LocalCar.Rcc;
        if (rcc == null) return false;

        // First jump into the city: remember where the player was, so /tp back works.
        if (remember && _returnPose == null)
        {
            var hereWorld = WorldOrigin.ToWorld(rcc.transform.position);
            if (Vector3.Distance(new Vector3(hereWorld.x, 0f, hereWorld.z), new Vector3(Origin.x, 0f, Origin.z)) > 5000f)
                _returnPose = (hereWorld, rcc.transform.rotation);
        }

        var rb = rcc.rigid;
        if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        rcc.transform.SetPositionAndRotation(target, rot);
        if (rb != null) { rb.position = target; rb.rotation = rot; }
        _log($"[city] teleported to local {target} = world {WorldOrigin.ToWorld(target)} (origin offset {WorldOrigin.Offset})");
        if (remember) SetSceneLighting(true);
        MelonCoroutines.Start(PostTeleportDiagnostics(rcc, target));
        return true;
    }

    /// <summary>Which layer does the game put its own drivable surfaces on? Look under the player.</summary>
    private int DetectRoadLayer(RCC_CarControllerV3? rcc)
    {
        // The game's drivable surfaces are on "Road Physics"; that is the one layer the car's wheels are
        // known to collide with (verified in play). Only if that layer does not exist do we probe.
        for (var l = 0; l < 32; l++)
        {
            if (LayerMask.LayerToName(l).Equals("Road Physics", StringComparison.OrdinalIgnoreCase))
            {
                _log($"[city] city colliders on layer {l} (Road Physics)");
                return l;
            }
        }

        if (rcc == null) return 0;
        var wheel = rcc.GetComponentInChildren<WheelCollider>();
        var wheelLayer = wheel != null ? wheel.gameObject.layer : rcc.gameObject.layer;
        var carRoot = rcc.transform.root;
        try
        {
            // The car carries colliders of its own (location probes etc.): skip anything under the car.
            var origin = rcc.transform.position + Vector3.up * 1f;
            var hits = Physics.RaycastAll(origin, Vector3.down, 50f, ~0, QueryTriggerInteraction.Ignore);
            RaycastHit? best = null;
            foreach (var h in hits)
            {
                if (h.collider == null || h.collider.transform.root == carRoot) continue;
                if (best == null || h.distance < best.Value.distance) best = h;
            }
            if (best != null)
            {
                // Whatever the game's own drivable surface uses is, by definition, a layer these wheels collide with.
                // (Physics.GetIgnoreLayerCollision reports "ignored" for every layer through the interop, so it is not consulted.)
                var layer = best.Value.collider.gameObject.layer;
                _log($"[city] road under player: '{best.Value.collider.gameObject.name}' layer {layer} ({LayerMask.LayerToName(layer)}), {best.Value.distance:F1} m down — city colliders will use it (wheels are on {wheelLayer} {LayerMask.LayerToName(wheelLayer)})");
                return layer;
            }

            // No road under the car: look for a layer named like one.
            for (var l = 0; l < 32; l++)
                if (LayerMask.LayerToName(l).ToLowerInvariant().Contains("road physics")) { _log($"[city] no road under the player; using layer {l} ({LayerMask.LayerToName(l)}) by name"); return l; }
            _log("[city] no road under the player and no 'Road Physics' layer; using layer 0");
            return 0;
        }
        catch (Exception e) { _log($"[city] layer probe failed: {e.GetType().Name} {e.Message}; using layer 0"); }
        return 0;
    }

    /// <summary>What is actually under and around the spawn, and is the car still there a moment later?</summary>
    private IEnumerator PostTeleportDiagnostics(RCC_CarControllerV3 rcc, Vector3 target)
    {
        yield return null; // let physics see the new position
        try
        {
            var wheel = rcc.GetComponentInChildren<WheelCollider>();
            var wheelLayer = wheel != null ? wheel.gameObject.layer : rcc.gameObject.layer;
            var cityLayer = _importer?.Layer ?? 0;
            _log($"[city] car body layer {rcc.gameObject.layer} ({LayerMask.LayerToName(rcc.gameObject.layer)}), wheel layer {wheelLayer} ({LayerMask.LayerToName(wheelLayer)}), city layer {cityLayer}; ignore wheel↔city = {Physics.GetIgnoreLayerCollision(wheelLayer, cityLayer)}");

            var carRoot = rcc.transform.root;
            RaycastHit? below = null;
            foreach (var h in Physics.RaycastAll(target + Vector3.up * 5f, Vector3.down, 500f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (h.collider == null || h.collider.transform.root == carRoot) continue;
                if (below == null || h.distance < below.Value.distance) below = h;
            }
            if (below != null)
                _log($"[city] below spawn (ignoring the car): '{below.Value.collider.gameObject.name}' (layer {below.Value.collider.gameObject.layer} {LayerMask.LayerToName(below.Value.collider.gameObject.layer)}) at {below.Value.point}, {below.Value.distance - 5f:F1} m under the spawn, root '{RootName(below.Value.collider.transform)}'");
            else
                _log("[city] below spawn (ignoring the car): NOTHING within 500 m");

            var near = Physics.OverlapSphere(target, 40f, ~0, QueryTriggerInteraction.Collide);
            var count = 0; var sample = new System.Text.StringBuilder();
            foreach (var c in near)
            {
                if (c == null || c.transform.root != _root?.transform) continue;
                count++;
                if (count <= 6) sample.Append($" '{c.gameObject.name}'@{c.bounds.center}{(c.isTrigger ? "(trigger)" : "")};");
            }
            _log($"[city] city colliders within 40 m of spawn: {count}.{sample}");
        }
        catch (Exception e) { _log($"[city] diagnostics failed: {e.GetType().Name} {e.Message}"); }

        for (var i = 1; i <= 3; i++)
        {
            yield return new WaitForSeconds(1f);
            if (rcc == null) yield break;
            var p = rcc.transform.position;
            var v = rcc.rigid != null ? rcc.rigid.velocity : Vector3.zero;
            _log($"[city] +{i}s: car at local {p}, velocity {v.magnitude:F1} m/s (y {v.y:F1}), {Vector3.Distance(p, target):F1} m from spawn");
        }
    }

    private static string RootName(Transform t)
    {
        while (t.parent != null) t = t.parent;
        return t.name;
    }

    /// <summary>The alpha shifts its origin as the player drives; a FloatingOriginRoot moves with it.</summary>
    private void RegisterWithFloatingOrigin(GameObject root)
    {
        if (Game.Variant != GameVariant.Alpha) return;
        try { AddFloatingRoot(root); }
        catch (Exception e) { _log($"[city] floating-origin registration failed: {e.GetType().Name} — the city will drift if the origin shifts"); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddFloatingRoot(GameObject root) => root.AddComponent<Il2CppPlanetJem.Core.FloatingOrigin.FloatingOriginRoot>();
}
