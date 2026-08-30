// Inspects a Unity player build's scene files (no typetrees) with AssetsTools.NET.
//   nrmp-mapinspect <GameData dir> scenes                 list build scenes -> level files
//   nrmp-mapinspect <GameData dir> level <N|sceneName>    summarise one scene: types, meshes, textures, materials, lights
using AssetsTools.NET;
using AssetsTools.NET.Extra;

if (args.Length < 2) { Console.WriteLine("usage: nrmp-mapinspect <GameData dir> scenes | level <N|name>"); return 2; }
var data = args[0];
var cmd = args[1];

var am = new AssetsManager();
am.LoadClassPackage(Path.Combine(AppContext.BaseDirectory, "classdata.tpk"));
var ggm = am.LoadAssetsFile(Path.Combine(data, "globalgamemanagers"), false);
am.LoadClassDatabaseFromPackage(ggm.file.Metadata.UnityVersion);
Console.WriteLine($"unity {ggm.file.Metadata.UnityVersion}");

var scenes = new List<string>();
foreach (var info in ggm.file.GetAssetsOfType(AssetClassID.BuildSettings))
{
    var bf = am.GetBaseField(ggm, info);
    foreach (var s in bf["scenes.Array"]) scenes.Add(s.AsString);
}

if (cmd == "scenes")
{
    for (var i = 0; i < scenes.Count; i++)
    {
        var lvl = Path.Combine(data, $"level{i}");
        var size = File.Exists(lvl) ? new FileInfo(lvl).Length / 1024 : -1;
        Console.WriteLine($"level{i,-3} {size,8} KB  {scenes[i]}");
    }
    return 0;
}

if (cmd == "level" && args.Length > 2)
{
    var which = args[2];
    var idx = int.TryParse(which, out var n) ? n : scenes.FindIndex(s => s.Contains(which, StringComparison.OrdinalIgnoreCase));
    if (idx < 0) { Console.WriteLine("scene not found"); return 1; }
    Console.WriteLine($"== level{idx}: {scenes[idx]}");
    var inst = am.LoadAssetsFile(Path.Combine(data, $"level{idx}"), true);

    // type histogram
    var counts = new Dictionary<string, int>();
    foreach (var info in inst.file.AssetInfos)
    {
        var name = ((AssetClassID)info.TypeId).ToString();
        counts[name] = counts.GetValueOrDefault(name) + 1;
    }
    Console.WriteLine("-- object types --");
    foreach (var kv in counts.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");

    // referenced meshes / textures / materials live mostly in sharedassets; walk renderers
    var meshFormats = new Dictionary<string, int>();
    var texFormats = new Dictionary<string, int>();
    var shaders = new Dictionary<string, int>();
    var lightmapped = 0; var renderers = 0; var lights = new Dictionary<string, int>(); var colliders = 0;
    long verts = 0;
    var seenMesh = new HashSet<(int, long)>(); var seenTex = new HashSet<(int, long)>(); var seenMat = new HashSet<(int, long)>();

    void VisitMesh(AssetsFileInstance file, AssetTypeValueField pptr)
    {
        var ext = am.GetExtAsset(file, pptr);
        if (ext.baseField == null) return;
        var key = (ext.file.file.GetHashCode(), ext.info.PathId);
        if (!seenMesh.Add(key)) return;
        var bf = ext.baseField;
        var vc = bf["m_VertexData"]["m_VertexCount"].AsInt;
        var compressed = bf["m_CompressedMesh"]["m_Vertices"]["m_NumItems"].AsInt > 0;
        var streamed = bf["m_StreamData"]["path"].AsString.Length > 0;
        var channels = bf["m_VertexData"]["m_Channels.Array"].Children.Count(c => c["dimension"].AsInt > 0);
        var idxFmt = bf["m_IndexFormat"].IsDummy ? "?" : bf["m_IndexFormat"].AsInt == 0 ? "u16" : "u32";
        verts += vc;
        var k = $"{(compressed ? "COMPRESSED" : "raw")} {(streamed ? "resS" : "inline")} idx={idxFmt} ch={channels}";
        meshFormats[k] = meshFormats.GetValueOrDefault(k) + 1;
    }
    void VisitTexture(AssetsFileInstance file, AssetTypeValueField pptr)
    {
        var ext = am.GetExtAsset(file, pptr);
        if (ext.baseField == null || (AssetClassID)ext.info.TypeId != AssetClassID.Texture2D) return;
        var key = (ext.file.file.GetHashCode(), ext.info.PathId);
        if (!seenTex.Add(key)) return;
        var bf = ext.baseField;
        var fmt = bf["m_TextureFormat"].AsInt switch { 1 => "Alpha8", 3 => "RGB24", 4 => "RGBA32", 5 => "ARGB32", 7 => "RGB565", 10 => "DXT1", 12 => "DXT5", 25 => "BC7", 26 => "BC4", 27 => "BC5", 24 => "BC6H", var x => $"fmt{x}" };
        var k = $"{fmt} {(bf["m_StreamData"]["path"].AsString.Length > 0 ? "resS" : "inline")}";
        texFormats[k] = texFormats.GetValueOrDefault(k) + 1;
    }
    void VisitMaterial(AssetsFileInstance file, AssetTypeValueField pptr)
    {
        var ext = am.GetExtAsset(file, pptr);
        if (ext.baseField == null) return;
        var key = (ext.file.file.GetHashCode(), ext.info.PathId);
        if (!seenMat.Add(key)) return;
        var bf = ext.baseField;
        var sh = am.GetExtAsset(ext.file, bf["m_Shader"]);
        var shName = sh.baseField != null ? sh.baseField["m_ParsedForm"]["m_Name"].AsString : "(missing shader)";
        shaders[shName] = shaders.GetValueOrDefault(shName) + 1;
        foreach (var te in bf["m_SavedProperties"]["m_TexEnvs.Array"]) VisitTexture(ext.file, te["second"]["m_Texture"]);
    }

    foreach (var info in inst.file.AssetInfos)
    {
        var type = (AssetClassID)info.TypeId;
        switch (type)
        {
            case AssetClassID.MeshRenderer:
            {
                renderers++;
                var bf = am.GetBaseField(inst, info);
                if (bf["m_LightmapIndex"].AsInt is >= 0 and < 65534) lightmapped++;
                foreach (var m in bf["m_Materials.Array"]) VisitMaterial(inst, m);
                break;
            }
            case AssetClassID.MeshFilter:
                VisitMesh(inst, am.GetBaseField(inst, info)["m_Mesh"]);
                break;
            case AssetClassID.MeshCollider:
            case AssetClassID.BoxCollider:
                colliders++;
                break;
            case AssetClassID.Light:
            {
                var bf = am.GetBaseField(inst, info);
                var k = $"type={bf["m_Type"].AsInt} mode={bf["m_Lightmapping"].AsInt}";
                lights[k] = lights.GetValueOrDefault(k) + 1;
                break;
            }
            case AssetClassID.LightmapSettings:
            {
                var bf = am.GetBaseField(inst, info);
                Console.WriteLine($"-- lightmaps: {bf["m_Lightmaps.Array"].Children.Count} baked lightmap(s) --");
                break;
            }
        }
    }

    Console.WriteLine($"-- renderers: {renderers} ({lightmapped} lightmapped), colliders: {colliders}, unique meshes: {seenMesh.Count} ({verts:N0} verts), textures: {seenTex.Count}, materials: {seenMat.Count} --");
    Console.WriteLine("-- mesh storage --"); foreach (var kv in meshFormats.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
    Console.WriteLine("-- texture formats --"); foreach (var kv in texFormats.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
    Console.WriteLine("-- shaders --"); foreach (var kv in shaders.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
    Console.WriteLine("-- lights (type 0=spot 1=directional 2=point; mode 4=realtime 1=mixed 2=baked) --"); foreach (var kv in lights) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
    return 0;
}

if (cmd == "names" && args.Length > 3)
{
    var idx = int.TryParse(args[2], out var n) ? n : scenes.FindIndex(s => s.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    var inst = am.LoadAssetsFile(Path.Combine(data, $"level{idx}"), true);
    var pat = args[3];
    var transforms = new Dictionary<long, AssetTypeValueField>();
    foreach (var info in inst.file.AssetInfos)
        if ((AssetClassID)info.TypeId is AssetClassID.Transform or AssetClassID.RectTransform) transforms[info.PathId] = am.GetBaseField(inst, info);
    var goOfTransform = new Dictionary<long, string>();
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.GameObject))
    {
        var bf = am.GetBaseField(inst, info);
        foreach (var c in bf["m_Component.Array"]) { var id = c["component"]["m_PathID"].AsLong; if (transforms.ContainsKey(id)) goOfTransform[id] = bf["m_Name"].AsString; }
    }
    (float x, float y, float z) World(long tid)
    {
        if (!transforms.TryGetValue(tid, out var t)) return (0, 0, 0);
        var p = t["m_LocalPosition"]; var lp = (p["x"].AsFloat, p["y"].AsFloat, p["z"].AsFloat);
        var f = t["m_Father"]["m_PathID"].AsLong;
        if (f == 0 || !transforms.ContainsKey(f)) return lp;
        var fw = World(f); return (fw.x + lp.Item1, fw.y + lp.Item2, fw.z + lp.Item3); // ignores parent rotation/scale: good enough for spawn hunting
    }
    foreach (var kv in goOfTransform)
    {
        if (!kv.Value.Contains(pat, StringComparison.OrdinalIgnoreCase)) continue;
        var w = World(kv.Key);
        var father = transforms[kv.Key]["m_Father"]["m_PathID"].AsLong;
        Console.WriteLine($"  {kv.Value,-48} world≈({w.x:F1}, {w.y:F1}, {w.z:F1})  parent={(father != 0 && goOfTransform.TryGetValue(father, out var pn) ? pn : "-")}");
    }
    return 0;
}

if (cmd == "mesh" && args.Length > 2)
{
    // Decode positions with the same stream/stride arithmetic the mod uses, as a sanity check.
    var idx = int.TryParse(args[2], out var n) ? n : scenes.FindIndex(s => s.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    var inst = am.LoadAssetsFile(Path.Combine(data, $"level{idx}"), true);
    var shown = 0;
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MeshFilter))
    {
        var ext = am.GetExtAsset(inst, am.GetBaseField(inst, info)["m_Mesh"]);
        if (ext.baseField == null) continue;
        var bf = ext.baseField;
        var vd = bf["m_VertexData"];
        var count = vd["m_VertexCount"].AsInt;
        if (args.Length > 3 && !bf["m_Name"].AsString.Contains(args[3], StringComparison.OrdinalIgnoreCase)) continue;
        if (args.Length <= 3 && count < 1000) continue;
        var channels = vd["m_Channels.Array"].Children.Select(c => (stream: c["stream"].AsInt, offset: c["offset"].AsInt, format: c["format"].AsInt, dim: c["dimension"].AsInt & 0xF)).ToList();
        var bytes = vd["m_DataSize"].AsByteArray;
        int Size(int f) => f switch { 0 => 4, 1 => 2, 2 => 1, 3 => 1, 4 => 2, 5 => 2, 6 => 1, 7 => 1, 8 => 2, 9 => 2, 10 => 4, 11 => 4, _ => 4 };
        var streams = channels.Where(c => c.dim > 0).Max(c => c.stream) + 1;
        var stride = new int[streams];
        foreach (var c in channels) if (c.dim > 0) stride[c.stream] += c.dim * Size(c.format);
        var start = new int[streams]; var p = 0;
        for (var s = 0; s < streams; s++) { start[s] = p; p += stride[s] * count; p = (p + 15) & ~15; }
        var pc = channels[0];
        Console.WriteLine($"      channels: {string.Join(" ", channels.Select((c, i) => c.dim > 0 ? $"[{i}]s{c.stream}@{c.offset} f{c.format}x{c.dim}" : ""))}");
        if (channels.Count > 1 && channels[1].dim >= 3)
        {
            var nc = channels[1]; var nnan = 0; var nzero = 0; double nlen = 0;
            for (var v = 0; v < count; v++)
            {
                var o = start[nc.stream] + v * stride[nc.stream] + nc.offset;
                float Rd(int k) => nc.format switch { 0 => BitConverter.ToSingle(bytes, o + 4 * k), 1 => (float)BitConverter.ToHalf(bytes, o + 2 * k), 2 => bytes[o + k] / 255f, 3 => (sbyte)bytes[o + k] / 127f, _ => BitConverter.ToSingle(bytes, o + 4 * k) };
                float nx = Rd(0), ny = Rd(1), nz = Rd(2);
                if (float.IsNaN(nx) || float.IsNaN(ny) || float.IsNaN(nz)) { nnan++; continue; }
                var l = Math.Sqrt(nx * nx + ny * ny + nz * nz); nlen += l; if (l < 0.01) nzero++;
            }
            // Which way do the normals point in mesh space (Blender exports: mesh Z is 'up' before the -90 X parent)?
            double sx = 0, sy = 0, sz = 0;
            for (var v = 0; v < count; v++)
            {
                var o = start[nc.stream] + v * stride[nc.stream] + nc.offset;
                if (nc.format != 0) break;
                sx += BitConverter.ToSingle(bytes, o); sy += BitConverter.ToSingle(bytes, o + 4); sz += BitConverter.ToSingle(bytes, o + 8);
            }
            Console.WriteLine($"      normals: nan={nnan} zero={nzero} meanLen={nlen / Math.Max(1, count - nnan):F3} meanDir=({sx / count:F2},{sy / count:F2},{sz / count:F2})");
            // Winding vs normals: Unity front faces are clockwise; geometric normal = cross(b-a, c-a) (left-handed).
            var ib = bf["m_IndexBuffer.Array"].AsByteArray; var use32 = !bf["m_IndexFormat"].IsDummy && bf["m_IndexFormat"].AsInt == 1;
            int agree = 0, disagree = 0;
            float[] P(int vi) { var o = start[pc.stream] + vi * stride[pc.stream] + pc.offset; return new[] { BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8) }; }
            float[] N(int vi) { var o = start[nc.stream] + vi * stride[nc.stream] + nc.offset; return new[] { BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8) }; }
            foreach (var sm in bf["m_SubMeshes.Array"])
            {
                if (sm["topology"].AsInt != 0) continue;
                var first = sm["firstByte"].AsInt; var icount = sm["indexCount"].AsInt; var bv = sm["baseVertex"].IsDummy ? 0 : sm["baseVertex"].AsInt;
                for (var t = 0; t + 2 < icount; t += 3)
                {
                    int Idx(int k) => use32 ? BitConverter.ToInt32(ib, first + 4 * (t + k)) + bv : BitConverter.ToUInt16(ib, first + 2 * (t + k)) + bv;
                    var a = P(Idx(0)); var b = P(Idx(1)); var c = P(Idx(2));
                    var e1 = new[] { b[0] - a[0], b[1] - a[1], b[2] - a[2] }; var e2 = new[] { c[0] - a[0], c[1] - a[1], c[2] - a[2] };
                    var g = new[] { e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0] };
                    var na = N(Idx(0)); var nb = N(Idx(1)); var ncc = N(Idx(2));
                    var d = g[0] * (na[0] + nb[0] + ncc[0]) + g[1] * (na[1] + nb[1] + ncc[1]) + g[2] * (na[2] + nb[2] + ncc[2]);
                    if (d > 0) agree++; else if (d < 0) disagree++;
                }
            }
            Console.WriteLine($"      winding: {agree} tris agree with normals, {disagree} inverted");
        }
        float minx = float.MaxValue, maxx = float.MinValue, miny = float.MaxValue, maxy = float.MinValue, minz = float.MaxValue, maxz = float.MinValue; var nan = 0;
        for (var v = 0; v < count; v++)
        {
            var o = start[pc.stream] + v * stride[pc.stream] + pc.offset;
            float x = BitConverter.ToSingle(bytes, o), y = BitConverter.ToSingle(bytes, o + 4), z = BitConverter.ToSingle(bytes, o + 8);
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) { nan++; continue; }
            minx = Math.Min(minx, x); maxx = Math.Max(maxx, x); miny = Math.Min(miny, y); maxy = Math.Max(maxy, y); minz = Math.Min(minz, z); maxz = Math.Max(maxz, z);
        }
        var aabb = bf["m_LocalAABB"];
        var idxFmt = bf["m_IndexFormat"].IsDummy ? -1 : bf["m_IndexFormat"].AsInt;
        var subs = bf["m_SubMeshes.Array"].Children.Count;
        var sd = bf["m_StreamData"];
        Console.WriteLine($"  {bf["m_Name"].AsString,-40} verts={count,7} bytes={bytes.Length,9} strides=[{string.Join(",", stride)}] posfmt={pc.format} dim={pc.dim} nan={nan} indexFormat={idxFmt} indexBytes={bf["m_IndexBuffer.Array"].AsByteArray?.Length} submeshes={subs} stream='{sd["path"].AsString}' compression={bf["m_MeshCompression"].AsInt}");
        foreach (var sm in bf["m_SubMeshes.Array"]) Console.WriteLine($"      submesh topology={sm["topology"].AsInt} firstByte={sm["firstByte"].AsInt} indexCount={sm["indexCount"].AsInt} firstVertex={sm["firstVertex"].AsInt} vertexCount={sm["vertexCount"].AsInt}");
        Console.WriteLine($"      decoded bounds  x[{minx:F1},{maxx:F1}] y[{miny:F1},{maxy:F1}] z[{minz:F1},{maxz:F1}]");
        Console.WriteLine($"      stored  AABB    center=({aabb["m_Center"]["x"].AsFloat:F1},{aabb["m_Center"]["y"].AsFloat:F1},{aabb["m_Center"]["z"].AsFloat:F1}) extent=({aabb["m_Extent"]["x"].AsFloat:F1},{aabb["m_Extent"]["y"].AsFloat:F1},{aabb["m_Extent"]["z"].AsFloat:F1})");
        if (++shown >= (args.Length > 3 ? 20 : 4)) break;
    }
    return 0;
}

if (cmd == "parseall" && args.Length > 2)
{
    // Exercise every field access the mod's MeshReader performs, on every mesh of a scene, without Unity.
    var idx = int.TryParse(args[2], out var n) ? n : scenes.FindIndex(s => s.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    var inst = am.LoadAssetsFile(Path.Combine(data, $"level{idx}"), true);
    int ok = 0, failed = 0; var firstTrace = "";
    foreach (var info in inst.file.AssetInfos)
    {
        if ((AssetClassID)info.TypeId is not (AssetClassID.MeshFilter or AssetClassID.MeshCollider)) continue;
        var ext = am.GetExtAsset(inst, am.GetBaseField(inst, info)["m_Mesh"]);
        if (ext.baseField == null) continue;
        try
        {
            var bf = ext.baseField;
            var vd = bf["m_VertexData"];
            var count = vd["m_VertexCount"].AsInt;
            foreach (var c in vd["m_Channels.Array"]) { _ = c["stream"].AsInt; _ = c["offset"].AsInt; _ = c["format"].AsInt; _ = c["dimension"].AsInt & 0xF; }
            var bytes = vd["m_DataSize"].AsByteArray;
            var sd = bf["m_StreamData"];
            if ((bytes == null || bytes.Length == 0) && sd["path"].AsString.Length > 0) { _ = sd["offset"].AsLong; _ = sd["size"].AsLong; }
            var indexBytes = bf["m_IndexBuffer.Array"].AsByteArray; // vector<UInt8>: the bytes hang off the Array child
            if (indexBytes == null) throw new Exception("m_IndexBuffer.AsByteArray is null");
            var fmt = bf["m_IndexFormat"];
            if (fmt == null) throw new Exception("m_IndexFormat indexer returned null");
            _ = fmt.IsDummy ? 0 : fmt.AsInt;
            foreach (var sm in bf["m_SubMeshes.Array"])
            {
                _ = sm["topology"].AsInt; _ = sm["firstByte"].AsInt; _ = sm["indexCount"].AsInt;
                var bv = sm["baseVertex"];
                if (bv == null) throw new Exception("baseVertex indexer returned null");
                _ = bv.IsDummy ? 0 : bv.AsInt;
            }
            if (count > 0 && (bytes == null || bytes.Length == 0)) throw new Exception("no vertex bytes");
            ok++;
        }
        catch (Exception e) { failed++; if (firstTrace.Length == 0) firstTrace = e.ToString(); }
    }
    Console.WriteLine($"parsed ok={ok} failed={failed}");
    if (firstTrace.Length > 0) Console.WriteLine(firstTrace);
    return 0;
}

if (cmd == "rendersettings" && args.Length > 2)
{
    var idx = int.TryParse(args[2], out var n) ? n : scenes.FindIndex(s => s.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    var inst = am.LoadAssetsFile(Path.Combine(data, $"level{idx}"), true);
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.RenderSettings))
    {
        var bf = am.GetBaseField(inst, info);
        foreach (var f in bf.Children)
        {
            if (f.FieldName == "m_AmbientProbe") { Console.WriteLine($"  m_AmbientProbe L0 = ({f["sh[ 0]"].AsFloat:F3}, {f["sh[ 9]"].AsFloat:F3}, {f["sh[18]"].AsFloat:F3})"); continue; }
            if (f.Children.Count == 0) { Console.WriteLine($"  {f.FieldName} = {f.AsString}"); continue; }
            if (f.FieldName.EndsWith("Color")) Console.WriteLine($"  {f.FieldName} = ({f["r"].AsFloat:F3}, {f["g"].AsFloat:F3}, {f["b"].AsFloat:F3})");
            else if (f["m_PathID"] is { IsDummy: false })
            {
                var ext = am.GetExtAsset(inst, f);
                Console.WriteLine($"  {f.FieldName} -> {(ext.baseField == null ? "null" : ext.baseField["m_Name"].AsString)}");
            }
        }
    }
    var dirLights = 0;
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Light))
    {
        var bf = am.GetBaseField(inst, info);
        if (bf["m_Type"].AsInt == 1) { dirLights++; var c = bf["m_Color"]; Console.WriteLine($"  directional light: intensity {bf["m_Intensity"].AsFloat:F2} color ({c["r"].AsFloat:F2},{c["g"].AsFloat:F2},{c["b"].AsFloat:F2}) mode {bf["m_Lightmapping"].AsInt}"); }
    }
    Console.WriteLine($"  directional lights: {dirLights}");
    return 0;
}

if (cmd == "lmformat" && args.Length > 2)
{
    // Lightmap texture formats inside asset bundles (Addressables builds keep lightmaps there).
    var formats = new Dictionary<string, int>(); var files = 0; var texTotal = 0; var lightNames = new HashSet<string>();
    foreach (var path in Directory.EnumerateFiles(args[2], "*.bundle", SearchOption.AllDirectories))
    {
        BundleFileInstance bun;
        try { bun = am.LoadBundleFile(path, true); } catch (Exception e) { Console.WriteLine($"  {Path.GetFileName(path)}: {e.GetType().Name}"); continue; }
        files++;
        for (var i = 0; i < bun.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
        {
            var dir = bun.file.BlockAndDirInfo.DirectoryInfos[i];
            if ((dir.Flags & 4) == 0) continue;
            AssetsFileInstance inst;
            try { inst = am.LoadAssetsFileFromBundle(bun, i, true); } catch { continue; }
            foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Texture2D))
            {
                AssetTypeValueField bf;
                try { bf = am.GetBaseField(inst, info); } catch { continue; }
                var name = bf["m_Name"].AsString; texTotal++;
                if (name.Contains("light", StringComparison.OrdinalIgnoreCase) && lightNames.Count < 40) lightNames.Add($"{name} fmt {bf["m_TextureFormat"].AsInt} {bf["m_Width"].AsInt}x{bf["m_Height"].AsInt}");
                if (!name.StartsWith("Lightmap", StringComparison.OrdinalIgnoreCase)) continue;
                var k = $"format {bf["m_TextureFormat"].AsInt} {bf["m_Width"].AsInt}x{bf["m_Height"].AsInt} colorSpace {bf["m_ColorSpace"].AsInt}  e.g. {name} ({Path.GetFileName(path)})";
                formats[k] = formats.GetValueOrDefault(k) + 1;
            }
        }
        am.UnloadBundleFile(path);
    }
    // Loose serialized files next to globalgamemanagers (level*, sharedassets*.assets)
    var loose = 0;
    foreach (var path in Directory.EnumerateFiles(data))
    {
        var fn = Path.GetFileName(path);
        if (!(fn.StartsWith("level") || fn.EndsWith(".assets"))) continue;
        AssetsFileInstance inst;
        try { inst = am.LoadAssetsFile(path, false); } catch { continue; }
        loose++;
        foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Texture2D))
        {
            AssetTypeValueField bf;
            try { bf = am.GetBaseField(inst, info); } catch { continue; }
            var name = bf["m_Name"].AsString;
            if (!name.StartsWith("Lightmap", StringComparison.OrdinalIgnoreCase)) continue;
            var k = $"format {bf["m_TextureFormat"].AsInt} {bf["m_Width"].AsInt}x{bf["m_Height"].AsInt} colorSpace {bf["m_ColorSpace"].AsInt}  e.g. {name} ({fn})";
            formats[k] = formats.GetValueOrDefault(k) + 1;
        }
        am.UnloadAssetsFile(path);
    }
    Console.WriteLine($"  scanned {files} bundles, {loose} loose files, {texTotal} textures in bundles");
    foreach (var n2 in lightNames) Console.WriteLine($"    light-ish: {n2}");
    foreach (var kv in formats.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
    return 0;
}

if (cmd == "tree" && args.Length > 3)
{
    // Print the subtree under every object whose name matches: active flag, layer, components, local TRS.
    var idx = int.TryParse(args[2], out var n) ? n : scenes.FindIndex(s => s.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    var inst = am.LoadAssetsFile(Path.Combine(data, $"level{idx}"), true);
    var maxDepth = args.Length > 4 ? int.Parse(args[4]) : 3;
    var transforms = new Dictionary<long, AssetTypeValueField>();
    foreach (var info in inst.file.AssetInfos)
        if ((AssetClassID)info.TypeId is AssetClassID.Transform or AssetClassID.RectTransform) transforms[info.PathId] = am.GetBaseField(inst, info);
    var goOf = new Dictionary<long, AssetTypeValueField>();
    var comps = new Dictionary<long, string>();
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.GameObject))
    {
        var bf = am.GetBaseField(inst, info);
        long tid = 0; var kinds = new List<string>();
        foreach (var c in bf["m_Component.Array"])
        {
            var id = c["component"]["m_PathID"].AsLong;
            if (transforms.ContainsKey(id)) { tid = id; continue; }
            var ext = am.GetExtAsset(inst, c["component"]);
            if (ext.info == null) continue;
            var k = ((AssetClassID)ext.info.TypeId).ToString();
            if (k == "MonoBehaviour" && ext.baseField != null)
            {
                var sc = am.GetExtAsset(inst, ext.baseField["m_Script"]);
                k = "MB:" + (sc.baseField != null ? sc.baseField["m_Name"].AsString : "?");
            }
            else if (k == "MeshRenderer" && ext.baseField != null) k += ext.baseField["m_Enabled"].AsBool ? "" : "(off)";
            else if (k == "MeshCollider" && ext.baseField != null) k += ext.baseField["m_IsTrigger"].AsBool ? "(trigger)" : "";
            kinds.Add(k);
        }
        if (tid != 0) { goOf[tid] = bf; comps[tid] = string.Join(",", kinds); }
    }
    var children = new Dictionary<long, List<long>>();
    foreach (var kv in transforms)
    {
        var f = kv.Value["m_Father"]["m_PathID"].AsLong;
        if (!children.TryGetValue(f, out var l)) children[f] = l = new List<long>();
        l.Add(kv.Key);
    }
    void Print(long tid, int depth)
    {
        if (!goOf.TryGetValue(tid, out var go)) return;
        var t = transforms[tid];
        var p = t["m_LocalPosition"]; var r = t["m_LocalRotation"]; var sc = t["m_LocalScale"];
        var rot = $"({r["x"].AsFloat:F2},{r["y"].AsFloat:F2},{r["z"].AsFloat:F2},{r["w"].AsFloat:F2})";
        var scale = $"({sc["x"].AsFloat:F2},{sc["y"].AsFloat:F2},{sc["z"].AsFloat:F2})";
        var kids = children.TryGetValue(tid, out var l) ? l.Count : 0;
        Console.WriteLine($"{new string(' ', depth * 2)}{(go["m_IsActive"].AsBool ? "+" : "-")} {go["m_Name"].AsString}  L{go["m_Layer"].AsUInt} [{comps[tid]}] pos=({p["x"].AsFloat:F1},{p["y"].AsFloat:F1},{p["z"].AsFloat:F1}) rot={rot} scale={scale} kids={kids}");
        if (depth >= maxDepth || kids == 0) return;
        var shown = 0;
        foreach (var c in l!) { Print(c, depth + 1); if (++shown >= 40) { Console.WriteLine($"{new string(' ', (depth + 1) * 2)}... {kids - shown} more"); break; } }
    }
    foreach (var kv in goOf)
        if (kv.Value["m_Name"].AsString.Equals(args[3], StringComparison.OrdinalIgnoreCase) || (args[3] == "*" && transforms[kv.Key]["m_Father"]["m_PathID"].AsLong == 0)) Print(kv.Key, 0);
    return 0;
}

if (cmd == "bounds" && args.Length > 2)
{
    // World-space AABB of every active renderer (full matrix composition), plus the scene's total extent.
    //   bounds <scene> [nameFilter|*] [max]
    var idx = int.TryParse(args[2], out var n) ? n : scenes.FindIndex(s => s.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    var inst = am.LoadAssetsFile(Path.Combine(data, $"level{idx}"), true);
    var filter = args.Length > 3 ? args[3] : "*";
    var max = args.Length > 4 ? int.Parse(args[4]) : 60;
    var transforms = new Dictionary<long, AssetTypeValueField>();
    foreach (var info in inst.file.AssetInfos)
        if ((AssetClassID)info.TypeId is AssetClassID.Transform or AssetClassID.RectTransform) transforms[info.PathId] = am.GetBaseField(inst, info);
    var goOf = new Dictionary<long, AssetTypeValueField>(); var filterOf = new Dictionary<long, AssetTypeValueField>(); var rendererOf = new Dictionary<long, (long id, AssetTypeValueField bf)>();
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.GameObject))
    {
        var bf = am.GetBaseField(inst, info); long tid = 0;
        foreach (var c in bf["m_Component.Array"])
        {
            var id = c["component"]["m_PathID"].AsLong;
            if (transforms.ContainsKey(id)) { tid = id; continue; }
            var ext = am.GetExtAsset(inst, c["component"]);
            if (ext.info == null || ext.baseField == null) continue;
            if ((AssetClassID)ext.info.TypeId == AssetClassID.MeshFilter) filterOf[id] = ext.baseField; // keyed by component id; resolved below
            if ((AssetClassID)ext.info.TypeId == AssetClassID.MeshRenderer) rendererOf[id] = (id, ext.baseField);
        }
        if (tid != 0) goOf[tid] = bf;
    }
    var lodSkip = new HashSet<long>();
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.LODGroup))
    {
        var lods = am.GetBaseField(inst, info)["m_LODs.Array"].Children;
        for (var i = 1; i < lods.Count; i++) foreach (var r in lods[i]["renderers.Array"]) lodSkip.Add(r["renderer"]["m_PathID"].AsLong);
    }
    System.Numerics.Matrix4x4 Local(AssetTypeValueField t)
    {
        var p = t["m_LocalPosition"]; var r = t["m_LocalRotation"]; var sc = t["m_LocalScale"];
        return System.Numerics.Matrix4x4.CreateScale(sc["x"].AsFloat, sc["y"].AsFloat, sc["z"].AsFloat)
             * System.Numerics.Matrix4x4.CreateFromQuaternion(new System.Numerics.Quaternion(r["x"].AsFloat, r["y"].AsFloat, r["z"].AsFloat, r["w"].AsFloat))
             * System.Numerics.Matrix4x4.CreateTranslation(p["x"].AsFloat, p["y"].AsFloat, p["z"].AsFloat);
    }
    var worldCache = new Dictionary<long, System.Numerics.Matrix4x4>();
    System.Numerics.Matrix4x4 World(long tid)
    {
        if (worldCache.TryGetValue(tid, out var m)) return m;
        var t = transforms[tid]; var f = t["m_Father"]["m_PathID"].AsLong;
        m = f != 0 && transforms.ContainsKey(f) ? Local(t) * World(f) : Local(t);
        worldCache[tid] = m; return m;
    }
    bool Active(long tid)
    {
        if (!goOf.TryGetValue(tid, out var go) || !go["m_IsActive"].AsBool) return false;
        var f = transforms[tid]["m_Father"]["m_PathID"].AsLong;
        return f == 0 || !transforms.ContainsKey(f) || Active(f);
    }
    string Chain(long tid, int depth)
    {
        var f = transforms[tid]["m_Father"]["m_PathID"].AsLong;
        return f != 0 && transforms.ContainsKey(f) && depth > 0 && goOf.TryGetValue(f, out var pg) ? Chain(f, depth - 1) + "/" + pg["m_Name"].AsString : "";
    }
    float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
    var shown = 0; var total = 0;
    foreach (var kv in goOf)
    {
        var go = kv.Value; AssetTypeValueField? mf = null; long rid = 0; AssetTypeValueField? mr = null;
        foreach (var c in go["m_Component.Array"]) { var id = c["component"]["m_PathID"].AsLong; if (filterOf.TryGetValue(id, out var x)) mf = x; if (rendererOf.TryGetValue(id, out var y)) { rid = y.id; mr = y.bf; } }
        if (mf == null || mr == null || !mr["m_Enabled"].AsBool || !Active(kv.Key)) continue;
        var ext = am.GetExtAsset(inst, mf["m_Mesh"]); if (ext.baseField == null) continue;
        var aabb = ext.baseField["m_LocalAABB"]; var c0 = aabb["m_Center"]; var e = aabb["m_Extent"];
        var m = World(kv.Key);
        float bx0 = float.MaxValue, by0 = float.MaxValue, bz0 = float.MaxValue, bx1 = float.MinValue, by1 = float.MinValue, bz1 = float.MinValue;
        for (var i = 0; i < 8; i++)
        {
            var corner = new System.Numerics.Vector3(c0["x"].AsFloat + ((i & 1) == 0 ? -1 : 1) * e["x"].AsFloat, c0["y"].AsFloat + ((i & 2) == 0 ? -1 : 1) * e["y"].AsFloat, c0["z"].AsFloat + ((i & 4) == 0 ? -1 : 1) * e["z"].AsFloat);
            var w = System.Numerics.Vector3.Transform(corner, m);
            bx0 = Math.Min(bx0, w.X); by0 = Math.Min(by0, w.Y); bz0 = Math.Min(bz0, w.Z); bx1 = Math.Max(bx1, w.X); by1 = Math.Max(by1, w.Y); bz1 = Math.Max(bz1, w.Z);
        }
        total++;
        minX = Math.Min(minX, bx0); minY = Math.Min(minY, by0); minZ = Math.Min(minZ, bz0); maxX = Math.Max(maxX, bx1); maxY = Math.Max(maxY, by1); maxZ = Math.Max(maxZ, bz1);
        var name = go["m_Name"].AsString;
        if (filter != "*" && filter != "flat" && !name.Contains(filter, StringComparison.OrdinalIgnoreCase) && !Chain(kv.Key, 3).Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
        if (shown++ >= max) continue;
        if (filter == "flat" && !((bx1 - bx0) > 300 && (bz1 - bz0) > 300 && (by1 - by0) < 40)) continue;
        Console.WriteLine($"  {name,-40} L{go["m_Layer"].AsUInt,-2} {(lodSkip.Contains(rid) ? "LOD1+" : "     ")} x[{bx0,8:F1},{bx1,8:F1}] y[{by0,7:F1},{by1,7:F1}] z[{bz0,8:F1},{bz1,8:F1}]  {Chain(kv.Key, 3)}");
    }
    Console.WriteLine($"  == {total} active renderers; extent x[{minX:F1},{maxX:F1}] y[{minY:F1},{maxY:F1}] z[{minZ:F1},{maxZ:F1}]");
    return 0;
}

if (cmd == "mats" && args.Length > 3)
{
    // Materials of renderers whose object name matches: shader, colours, floats, textures, keywords.
    var idx = int.TryParse(args[2], out var n) ? n : scenes.FindIndex(s => s.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    var inst = am.LoadAssetsFile(Path.Combine(data, $"level{idx}"), true);
    var rendererGo = new Dictionary<long, string>();
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.GameObject))
    {
        var bf = am.GetBaseField(inst, info);
        foreach (var c in bf["m_Component.Array"]) rendererGo[c["component"]["m_PathID"].AsLong] = bf["m_Name"].AsString;
    }
    var shown = 0;
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MeshRenderer))
    {
        if (!rendererGo.TryGetValue(info.PathId, out var goName) || !goName.Contains(args[3], StringComparison.OrdinalIgnoreCase)) continue;
        var mr = am.GetBaseField(inst, info);
        Console.WriteLine($"  {goName}  enabled={mr["m_Enabled"].AsBool} castShadows={mr["m_CastShadows"].AsInt} lightmapIndex={mr["m_LightmapIndex"].AsInt}");
        foreach (var m in mr["m_Materials.Array"])
        {
            var ext = am.GetExtAsset(inst, m);
            if (ext.baseField == null) { Console.WriteLine("      material: <null>"); continue; }
            var mat = ext.baseField;
            var sh = am.GetExtAsset(ext.file, mat["m_Shader"]);
            var shaderName = sh.baseField == null ? "?" : sh.baseField["m_ParsedForm"]["m_Name"].AsString;
            Console.WriteLine($"      material '{mat["m_Name"].AsString}' shader '{shaderName}' keywords '{mat["m_ShaderKeywords"].AsString}' renderQueue={mat["m_CustomRenderQueue"].AsInt}");
            foreach (var c in mat["m_SavedProperties"]["m_Colors.Array"]) Console.WriteLine($"          color {c["first"].AsString} = ({c["second"]["r"].AsFloat:F2},{c["second"]["g"].AsFloat:F2},{c["second"]["b"].AsFloat:F2},{c["second"]["a"].AsFloat:F2})");
            foreach (var f in mat["m_SavedProperties"]["m_Floats.Array"]) Console.WriteLine($"          float {f["first"].AsString} = {f["second"].AsFloat:F3}");
            foreach (var t in mat["m_SavedProperties"]["m_TexEnvs.Array"])
            {
                var tex = am.GetExtAsset(ext.file, t["second"]["m_Texture"]);
                Console.WriteLine($"          tex {t["first"].AsString} = {(tex.baseField == null ? "none" : tex.baseField["m_Name"].AsString + " fmt " + tex.baseField["m_TextureFormat"].AsInt)}");
            }
        }
        if (++shown >= 3) break;
    }
    return 0;
}

if (cmd == "texalpha" && args.Length > 3)
{
    // Alpha statistics of a DXT5/BC3 texture (from its block endpoints): what fraction survives a cutout threshold.
    var idx = int.TryParse(args[2], out var n) ? n : scenes.FindIndex(s => s.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    var inst = am.LoadAssetsFile(Path.Combine(data, $"level{idx}"), true);
    var seen = new HashSet<(string, long)>();
    var matRefs = new List<(AssetsFileInstance file, AssetTypeValueField mat)>();
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MeshRenderer))
        foreach (var m in am.GetBaseField(inst, info)["m_Materials.Array"]) { var me = am.GetExtAsset(inst, m); if (me.baseField != null) matRefs.Add((me.file, me.baseField)); }
    foreach (var (mfile, mat) in matRefs)
    {
        foreach (var t in mat["m_SavedProperties"]["m_TexEnvs.Array"])
        {
            var ext = am.GetExtAsset(mfile, t["second"]["m_Texture"]);
            if (ext.baseField == null || !seen.Add((ext.file.name, ext.info.PathId))) continue;
            var bf = ext.baseField;
            var name = bf["m_Name"].AsString;
            if (!name.Contains(args[3], StringComparison.OrdinalIgnoreCase)) continue;
            var fmt = bf["m_TextureFormat"].AsInt; var w = bf["m_Width"].AsInt; var h = bf["m_Height"].AsInt;
            byte[] bytes = bf["image data"].AsByteArray;
            var sd = bf["m_StreamData"];
            if ((bytes == null || bytes.Length == 0) && sd["path"].AsString.Length > 0)
            {
                var path = Path.Combine(data, Path.GetFileName(sd["path"].AsString));
                using var fs = File.OpenRead(path); fs.Seek(sd["offset"].AsLong, SeekOrigin.Begin);
                bytes = new byte[sd["size"].AsLong]; fs.Read(bytes, 0, bytes.Length);
            }
            Console.WriteLine($"  {name}: format {fmt} {w}x{h} bytes {bytes?.Length}");
            if (fmt != 12 || bytes == null) { Console.WriteLine("      (not DXT5; alpha is 1.0 for DXT1)"); continue; }
            var blocks = (w / 4) * (h / 4); var opaque = 0; var clear = 0; long sum = 0;
            for (var b = 0; b < blocks && b * 16 + 1 < bytes.Length; b++)
            {
                int a0 = bytes[b * 16], a1 = bytes[b * 16 + 1];
                sum += (a0 + a1) / 2;
                if (Math.Min(a0, a1) >= 191) opaque++; else if (Math.Max(a0, a1) < 191) clear++;
            }
            Console.WriteLine($"      mip0 blocks: {opaque * 100.0 / blocks:F1}% fully >=0.75, {clear * 100.0 / blocks:F1}% fully <0.75, mean alpha {sum / (double)blocks / 255:F2}");
        }
    }
    return 0;
}

if (cmd == "texdump" && args.Length > 4)
{
    // Decode mip 0 of a DXT1/DXT5 texture used by the scene's renderers and write <out>.png (colour) and <out>-alpha.png.
    var idx = int.TryParse(args[2], out var n) ? n : scenes.FindIndex(s => s.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    var inst = am.LoadAssetsFile(Path.Combine(data, $"level{idx}"), true);
    var seen = new HashSet<(string, long)>();
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MeshRenderer))
    foreach (var m in am.GetBaseField(inst, info)["m_Materials.Array"])
    {
        var me = am.GetExtAsset(inst, m); if (me.baseField == null) continue;
        foreach (var t in me.baseField["m_SavedProperties"]["m_TexEnvs.Array"])
        {
            var ext = am.GetExtAsset(me.file, t["second"]["m_Texture"]);
            if (ext.baseField == null || !seen.Add((ext.file.name, ext.info.PathId))) continue;
            var bf = ext.baseField;
            var name = bf["m_Name"].AsString;
            if (!name.Equals(args[3], StringComparison.OrdinalIgnoreCase)) continue;
            var fmt = bf["m_TextureFormat"].AsInt; var w = bf["m_Width"].AsInt; var h = bf["m_Height"].AsInt;
            byte[] bytes = bf["image data"].AsByteArray;
            var sd = bf["m_StreamData"];
            if ((bytes == null || bytes.Length == 0) && sd["path"].AsString.Length > 0)
            {
                using var fs = File.OpenRead(Path.Combine(data, Path.GetFileName(sd["path"].AsString))); fs.Seek(sd["offset"].AsLong, SeekOrigin.Begin);
                bytes = new byte[sd["size"].AsLong]; fs.Read(bytes, 0, bytes.Length);
            }
            if (fmt != 10 && fmt != 12) { Console.WriteLine($"  {name}: format {fmt} not DXT1/DXT5"); return 1; }
            var rgba = new byte[w * h * 4];
            var bw = w / 4; var bh = h / 4; var bs = fmt == 12 ? 16 : 8;
            for (var by = 0; by < bh; by++) for (var bx = 0; bx < bw; bx++)
            {
                var o = (by * bw + bx) * bs;
                var alpha = new byte[16];
                if (fmt == 12)
                {
                    int a0 = bytes[o], a1 = bytes[o + 1];
                    ulong bits = 0; for (var i = 0; i < 6; i++) bits |= (ulong)bytes[o + 2 + i] << (8 * i);
                    for (var i = 0; i < 16; i++)
                    {
                        var code = (int)((bits >> (3 * i)) & 7);
                        alpha[i] = (byte)(code == 0 ? a0 : code == 1 ? a1 : a0 > a1 ? (a0 * (8 - code) + a1 * (code - 1)) / 7 : code == 6 ? 0 : code == 7 ? 255 : (a0 * (6 - code) + a1 * (code - 1)) / 5);
                    }
                    o += 8;
                }
                else for (var i = 0; i < 16; i++) alpha[i] = 255;
                int c0 = bytes[o] | (bytes[o + 1] << 8), c1 = bytes[o + 2] | (bytes[o + 3] << 8);
                uint cbits = (uint)(bytes[o + 4] | (bytes[o + 5] << 8) | (bytes[o + 6] << 16) | (bytes[o + 7] << 24));
                (int r, int g, int b) C(int c) => (((c >> 11) & 31) * 255 / 31, ((c >> 5) & 63) * 255 / 63, (c & 31) * 255 / 31);
                var p0 = C(c0); var p1 = C(c1);
                var pal = new (int r, int g, int b)[4] { p0, p1, ((2 * p0.r + p1.r) / 3, (2 * p0.g + p1.g) / 3, (2 * p0.b + p1.b) / 3), ((p0.r + 2 * p1.r) / 3, (p0.g + 2 * p1.g) / 3, (p0.b + 2 * p1.b) / 3) };
                if (fmt == 10 && c0 <= c1) { pal[2] = ((p0.r + p1.r) / 2, (p0.g + p1.g) / 2, (p0.b + p1.b) / 2); pal[3] = (0, 0, 0); }
                for (var i = 0; i < 16; i++)
                {
                    var px = bx * 4 + (i & 3); var py = by * 4 + (i >> 2);
                    var pc = pal[(cbits >> (2 * i)) & 3];
                    var q = ((h - 1 - py) * w + px) * 4; // Unity stores bottom-up
                    rgba[q] = (byte)pc.r; rgba[q + 1] = (byte)pc.g; rgba[q + 2] = (byte)pc.b; rgba[q + 3] = alpha[i];
                }
            }
            // downscale 2x per requested factor to keep files small
            var factor = args.Length > 5 ? int.Parse(args[5]) : 2;
            var ow = w / factor; var oh = h / factor;
            byte[] Pack(bool alphaOnly)
            {
                var raw = new byte[oh * (ow * 3 + 1)];
                for (var y = 0; y < oh; y++)
                {
                    raw[y * (ow * 3 + 1)] = 0;
                    for (var x = 0; x < ow; x++)
                    {
                        var q = ((y * factor) * w + x * factor) * 4;
                        var d = y * (ow * 3 + 1) + 1 + x * 3;
                        if (alphaOnly) { raw[d] = raw[d + 1] = raw[d + 2] = rgba[q + 3]; }
                        else { raw[d] = rgba[q]; raw[d + 1] = rgba[q + 1]; raw[d + 2] = rgba[q + 2]; }
                    }
                }
                return raw;
            }
            void WritePng(string path, byte[] raw)
            {
                using var ms = new MemoryStream();
                void Chunk(string type, byte[] body)
                {
                    var len = BitConverter.GetBytes(body.Length); Array.Reverse(len); ms.Write(len);
                    var tb = System.Text.Encoding.ASCII.GetBytes(type); ms.Write(tb); ms.Write(body);
                    uint crc = 0xFFFFFFFF; foreach (var b in tb.Concat(body)) { crc ^= b; for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1; }
                    var cb = BitConverter.GetBytes(~crc); Array.Reverse(cb); ms.Write(cb);
                }
                ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
                var ihdr = new byte[13]; var wb = BitConverter.GetBytes(ow); var hb = BitConverter.GetBytes(oh); Array.Reverse(wb); Array.Reverse(hb);
                Array.Copy(wb, 0, ihdr, 0, 4); Array.Copy(hb, 0, ihdr, 4, 4); ihdr[8] = 8; ihdr[9] = 2;
                Chunk("IHDR", ihdr);
                using var z = new MemoryStream();
                using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Fastest, true)) zs.Write(raw);
                Chunk("IDAT", z.ToArray());
                Chunk("IEND", Array.Empty<byte>());
                File.WriteAllBytes(path, ms.ToArray());
            }
            WritePng(args[4], Pack(false));
            WritePng(Path.ChangeExtension(args[4], null) + "-alpha.png", Pack(true));
            Console.WriteLine($"  wrote {args[4]} ({ow}x{oh}) from {name} fmt {fmt} {w}x{h}");
            return 0;
        }
    }
    Console.WriteLine("texture not found");
    return 1;
}

if (cmd == "skybox" && args.Length > 2)
{
    var idx = int.TryParse(args[2], out var n) ? n : scenes.FindIndex(s => s.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    var inst = am.LoadAssetsFile(Path.Combine(data, $"level{idx}"), true);
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.RenderSettings))
    {
        var rs = am.GetBaseField(inst, info);
        foreach (var field in new[] { "m_SkyboxMaterial", "m_CustomReflection" })
        {
            var me = am.GetExtAsset(inst, rs[field]);
            if (me.baseField == null) { Console.WriteLine($"  {field}: null"); continue; }
            var mat = me.baseField;
            if ((AssetClassID)me.info.TypeId != AssetClassID.Material) { Console.WriteLine($"  {field}: {(AssetClassID)me.info.TypeId} '{mat["m_Name"].AsString}' fmt {mat["m_TextureFormat"].AsInt} {mat["m_Width"].AsInt}x{mat["m_Height"].AsInt}"); continue; }
            var sh = am.GetExtAsset(me.file, mat["m_Shader"]);
            Console.WriteLine($"  {field}: material '{mat["m_Name"].AsString}' shader '{(sh.baseField == null ? "?" : sh.baseField["m_ParsedForm"]["m_Name"].AsString)}' keywords '{mat["m_ShaderKeywords"].AsString}'");
            foreach (var c in mat["m_SavedProperties"]["m_Colors.Array"]) Console.WriteLine($"      color {c["first"].AsString} = ({c["second"]["r"].AsFloat:F2},{c["second"]["g"].AsFloat:F2},{c["second"]["b"].AsFloat:F2},{c["second"]["a"].AsFloat:F2})");
            foreach (var f in mat["m_SavedProperties"]["m_Floats.Array"]) Console.WriteLine($"      float {f["first"].AsString} = {f["second"].AsFloat:F3}");
            foreach (var t in mat["m_SavedProperties"]["m_TexEnvs.Array"])
            {
                var te = am.GetExtAsset(me.file, t["second"]["m_Texture"]);
                if (te.baseField == null) { Console.WriteLine($"      tex {t["first"].AsString} = none"); continue; }
                var tb = te.baseField;
                var sd = tb["m_StreamData"];
                Console.WriteLine($"      tex {t["first"].AsString} = {(AssetClassID)te.info.TypeId} '{tb["m_Name"].AsString}' fmt {tb["m_TextureFormat"].AsInt} {tb["m_Width"].AsInt}x{tb["m_Height"].AsInt} mips {tb["m_MipCount"].AsInt} colorSpace {tb["m_ColorSpace"].AsInt} imageCount {(tb["m_ImageCount"].IsDummy ? -1 : tb["m_ImageCount"].AsInt)} dim {(tb["m_TextureDimension"].IsDummy ? -1 : tb["m_TextureDimension"].AsInt)} data {tb["image data"].AsByteArray?.Length} stream '{sd["path"].AsString}' size {sd["size"].AsLong}");
            }
        }
    }
    return 0;
}

if (cmd == "layers" && args.Length > 2)
{
    // Layer and shadow-mode distribution of renderers/colliders: which layers were meant to be seen or driven on?
    var idx = int.TryParse(args[2], out var n) ? n : scenes.FindIndex(s => s.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    var inst = am.LoadAssetsFile(Path.Combine(data, $"level{idx}"), true);
    var goLayer = new Dictionary<long, int>();
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.GameObject)) goLayer[info.PathId] = (int)am.GetBaseField(inst, info)["m_Layer"].AsUInt;
    var rend = new Dictionary<string, int>(); var col = new Dictionary<string, int>();
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MeshRenderer))
    {
        var bf = am.GetBaseField(inst, info);
        var layer = goLayer.GetValueOrDefault(bf["m_GameObject"]["m_PathID"].AsLong, -1);
        var k = $"layer {layer,2}  castShadows={bf["m_CastShadows"].AsInt} enabled={bf["m_Enabled"].AsBool}";
        rend[k] = rend.GetValueOrDefault(k) + 1;
    }
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MeshCollider))
    {
        var bf = am.GetBaseField(inst, info);
        var layer = goLayer.GetValueOrDefault(bf["m_GameObject"]["m_PathID"].AsLong, -1);
        var k = $"layer {layer,2}  trigger={bf["m_IsTrigger"].AsBool} enabled={bf["m_Enabled"].AsBool}";
        col[k] = col.GetValueOrDefault(k) + 1;
    }
    Console.WriteLine("-- renderers --"); foreach (var kv in rend.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
    Console.WriteLine("-- mesh colliders --"); foreach (var kv in col.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Camera))
    {
        var bf = am.GetBaseField(inst, info);
        var go = am.GetExtAsset(inst, bf["m_GameObject"]);
        var bg = bf["m_BackGroundColor"];
        Console.WriteLine($"-- camera '{(go.baseField != null ? go.baseField["m_Name"].AsString : "?")}' cullingMask=0x{(uint)bf["m_CullingMask"]["m_Bits"].AsUInt:X8} clearFlags={bf["m_ClearFlags"].AsUInt} bg=({bg["r"].AsFloat:F2},{bg["g"].AsFloat:F2},{bg["b"].AsFloat:F2}) near={bf["near clip plane"].AsFloat} far={bf["far clip plane"].AsFloat} depth={bf["m_Depth"].AsFloat}");
    }
    return 0;
}

if (cmd == "lods" && args.Length > 2)
{
    // Do LOD groups list the same renderer in several levels? (Skipping "any LOD1+" would then delete LOD0 geometry.)
    var idx = int.TryParse(args[2], out var n) ? n : scenes.FindIndex(s => s.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    var inst = am.LoadAssetsFile(Path.Combine(data, $"level{idx}"), true);
    var lod0 = new HashSet<long>(); var lodN = new HashSet<long>(); var groups = 0; var levelsHist = new Dictionary<int, int>();
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.LODGroup))
    {
        groups++;
        var lods = am.GetBaseField(inst, info)["m_LODs.Array"].Children;
        levelsHist[lods.Count] = levelsHist.GetValueOrDefault(lods.Count) + 1;
        for (var i = 0; i < lods.Count; i++)
            foreach (var r in lods[i]["renderers.Array"]) { var id = r["renderer"]["m_PathID"].AsLong; if (i == 0) lod0.Add(id); else lodN.Add(id); }
    }
    var both = lod0.Intersect(lodN).Count();
    Console.WriteLine($"LOD groups: {groups}; levels histogram: {string.Join(", ", levelsHist.OrderBy(k => k.Key).Select(k => $"{k.Key} levels x{k.Value}"))}");
    Console.WriteLine($"renderers in LOD0: {lod0.Count}, in LOD1+: {lodN.Count}, in BOTH: {both}  <- these would be wrongly skipped");
    return 0;
}

if (cmd == "textures" && args.Length > 2)
{
    // Colour-space flag distribution with example names: tells us which value means sRGB.
    var idx = int.TryParse(args[2], out var n) ? n : scenes.FindIndex(s => s.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    var inst = am.LoadAssetsFile(Path.Combine(data, $"level{idx}"), true);
    var seen = new HashSet<(string, long)>(); var byCs = new Dictionary<int, List<string>>();
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MeshRenderer))
    {
        foreach (var m in am.GetBaseField(inst, info)["m_Materials.Array"])
        {
            var mat = am.GetExtAsset(inst, m); if (mat.baseField == null) continue;
            foreach (var te in mat.baseField["m_SavedProperties"]["m_TexEnvs.Array"])
            {
                var t = am.GetExtAsset(mat.file, te["second"]["m_Texture"]);
                if (t.baseField == null || (AssetClassID)t.info.TypeId != AssetClassID.Texture2D || !seen.Add((t.file.name, t.info.PathId))) continue;
                var cs = t.baseField["m_ColorSpace"].IsDummy ? -1 : t.baseField["m_ColorSpace"].AsInt;
                if (!byCs.ContainsKey(cs)) byCs[cs] = new List<string>();
                byCs[cs].Add($"{te["first"].AsString}:{t.baseField["m_Name"].AsString}");
            }
        }
    }
    foreach (var kv in byCs) Console.WriteLine($"m_ColorSpace={kv.Key}: {kv.Value.Count} textures, e.g. {string.Join(" | ", kv.Value.Take(6))}");
    return 0;
}

if (cmd == "lightmaps" && args.Length > 2)
{
    var idx = int.TryParse(args[2], out var n) ? n : scenes.FindIndex(s => s.Contains(args[2], StringComparison.OrdinalIgnoreCase));
    var inst = am.LoadAssetsFile(Path.Combine(data, $"level{idx}"), true);
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.LightmapSettings))
    {
        var bf = am.GetBaseField(inst, info);
        Console.WriteLine($"lightmapsMode={bf["m_LightmapsMode"].AsInt} count={bf["m_Lightmaps.Array"].Children.Count}");
        var shown = 0;
        foreach (var lm in bf["m_Lightmaps.Array"])
        {
            foreach (var field in new[] { "m_Lightmap", "m_DirLightmap", "m_ShadowMask" })
            {
                var t = am.GetExtAsset(inst, lm[field]);
                if (t.baseField == null) continue;
                Console.WriteLine($"  {field}: '{t.baseField["m_Name"].AsString}' {t.baseField["m_Width"].AsInt}x{t.baseField["m_Height"].AsInt} format={t.baseField["m_TextureFormat"].AsInt} colorSpace={t.baseField["m_ColorSpace"].AsInt} mips={t.baseField["m_MipCount"].AsInt}");
            }
            if (++shown >= 3) break;
        }
    }
    var lit = 0; var unlit = 0;
    foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MeshRenderer))
    {
        var bf = am.GetBaseField(inst, info);
        if (bf["m_LightmapIndex"].AsInt is >= 0 and < 65534) lit++; else unlit++;
    }
    Console.WriteLine($"renderers lightmapped: {lit}, not: {unlit}");
    return 0;
}

Console.WriteLine("unknown command");
return 2;
