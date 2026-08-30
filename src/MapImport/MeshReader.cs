using System;
using System.Collections.Generic;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace NightRunnersMP.MapImport;

/// <summary>Turns a serialized Unity 2018 Mesh (raw vertex streams, u16/u32 indices) into a live Mesh.</summary>
public static class MeshReader
{
    private struct Channel { public int Stream, Offset, Format, Dim; }

    // Unity 2018+ VertexFormat: Float, Float16, UNorm8, SNorm8, UNorm16, SNorm16, UInt8, SInt8, UInt16, SInt16, UInt32, SInt32
    private static int FormatSize(int f) => f switch { 0 => 4, 1 => 2, 2 => 1, 3 => 1, 4 => 2, 5 => 2, 6 => 1, 7 => 1, 8 => 2, 9 => 2, 10 => 4, 11 => 4, _ => 4 };

    /// <param name="keepSubmeshes">When set, only these sub-meshes get triangles (statically batched meshes: one slice per renderer).</param>
    public static Mesh? Read(PrologueData data, AssetTypeValueField bf, out int vertexCount, ISet<int>? keepSubmeshes = null)
    {
        vertexCount = 0;
        var name = bf["m_Name"].AsString;
        var vd = bf["m_VertexData"];
        vertexCount = vd["m_VertexCount"].AsInt;
        if (vertexCount <= 0) return null;
        var count = vertexCount; // local functions cannot capture an out parameter

        var channels = new List<Channel>();
        foreach (var c in vd["m_Channels.Array"])
            channels.Add(new Channel { Stream = c["stream"].AsInt, Offset = c["offset"].AsInt, Format = c["format"].AsInt, Dim = c["dimension"].AsInt & 0xF });

        var bytes = vd["m_DataSize"].AsByteArray;
        var sd = bf["m_StreamData"];
        if ((bytes == null || bytes.Length == 0) && sd["path"].AsString.Length > 0)
            bytes = data.ReadStream(sd["path"].AsString, sd["offset"].AsLong, sd["size"].AsLong);
        if (bytes == null || bytes.Length == 0) return null;

        // Stream layout: streams are stored one after another, each 16-byte aligned.
        var streamCount = 0;
        foreach (var c in channels) if (c.Dim > 0) streamCount = Math.Max(streamCount, c.Stream + 1);
        var stride = new int[streamCount];
        foreach (var c in channels) if (c.Dim > 0) stride[c.Stream] += c.Dim * FormatSize(c.Format);
        var streamStart = new int[streamCount];
        var pos = 0;
        for (var s = 0; s < streamCount; s++)
        {
            streamStart[s] = pos;
            pos += stride[s] * count;
            pos = (pos + 15) & ~15;
        }

        float[]? ReadChannel(int index, int wantDim)
        {
            if (index >= channels.Count) return null;
            var c = channels[index];
            if (c.Dim == 0) return null;
            var dim = Math.Min(c.Dim, wantDim);
            var result = new float[count * wantDim];
            var fs = FormatSize(c.Format);
            for (var v = 0; v < count; v++)
            {
                var baseOff = streamStart[c.Stream] + v * stride[c.Stream] + c.Offset;
                for (var d = 0; d < dim; d++)
                    result[v * wantDim + d] = ReadComponent(bytes, baseOff + d * fs, c.Format);
            }
            return result;
        }

        var positions = ReadChannel(0, 3);
        if (positions == null) return null;
        var normals = ReadChannel(1, 3);
        var tangents = ReadChannel(2, 4);
        var colors = ReadChannel(3, 4);
        var uv0 = ReadChannel(4, 2);
        var uv1 = ReadChannel(5, 2);

        // Indices
        var indexBytes = bf["m_IndexBuffer.Array"].AsByteArray; // vector<UInt8>: the bytes hang off the Array child
        var use32 = !bf["m_IndexFormat"].IsDummy && bf["m_IndexFormat"].AsInt == 1;
        var subs = new List<int[]>();
        var subIndex = -1;
        foreach (var sm in bf["m_SubMeshes.Array"])
        {
            subIndex++;
            var topology = sm["topology"].AsInt;
            var firstByte = sm["firstByte"].AsInt;
            var indexCount = sm["indexCount"].AsInt;
            var baseVertex = sm["baseVertex"].IsDummy ? 0 : sm["baseVertex"].AsInt;
            if (topology != 0 || (keepSubmeshes != null && !keepSubmeshes.Contains(subIndex))) { subs.Add(Array.Empty<int>()); continue; } // only triangle lists, only wanted slices
            var tris = new int[indexCount];
            for (var i = 0; i < indexCount; i++)
                tris[i] = (use32 ? (int)BitConverter.ToUInt32(indexBytes, firstByte + i * 4) : BitConverter.ToUInt16(indexBytes, firstByte + i * 2)) + baseVertex;
            subs.Add(tris);
        }

        // Unity mesh
        var mesh = new Mesh { name = name };
        if (vertexCount > 65535) mesh.indexFormat = IndexFormat.UInt32;

        var verts = new Vector3[vertexCount];
        for (var v = 0; v < vertexCount; v++) verts[v] = new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);
        mesh.vertices = new Il2CppStructArray<Vector3>(verts);

        if (normals != null)
        {
            var n = new Vector3[vertexCount];
            for (var v = 0; v < vertexCount; v++) n[v] = new Vector3(normals[v * 3], normals[v * 3 + 1], normals[v * 3 + 2]);
            mesh.normals = new Il2CppStructArray<Vector3>(n);
        }
        if (tangents != null)
        {
            var t = new Vector4[vertexCount];
            for (var v = 0; v < vertexCount; v++) t[v] = new Vector4(tangents[v * 4], tangents[v * 4 + 1], tangents[v * 4 + 2], tangents[v * 4 + 3]);
            mesh.tangents = new Il2CppStructArray<Vector4>(t);
        }
        if (uv0 != null)
        {
            var u = new Vector2[vertexCount];
            for (var v = 0; v < vertexCount; v++) u[v] = new Vector2(uv0[v * 2], uv0[v * 2 + 1]);
            mesh.uv = new Il2CppStructArray<Vector2>(u);
        }
        if (uv1 != null)
        {
            var u = new Vector2[vertexCount];
            for (var v = 0; v < vertexCount; v++) u[v] = new Vector2(uv1[v * 2], uv1[v * 2 + 1]);
            mesh.uv2 = new Il2CppStructArray<Vector2>(u);
        }
        if (colors != null)
        {
            var c = new Color[vertexCount];
            for (var v = 0; v < vertexCount; v++) c[v] = new Color(colors[v * 4], colors[v * 4 + 1], colors[v * 4 + 2], colors[v * 4 + 3]);
            mesh.colors = new Il2CppStructArray<Color>(c);
        }

        mesh.subMeshCount = subs.Count;
        for (var i = 0; i < subs.Count; i++) mesh.SetTriangles(new Il2CppStructArray<int>(subs[i]), i, false);
        if (normals == null) mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static float ReadComponent(byte[] b, int off, int format) => format switch
    {
        0 => BitConverter.ToSingle(b, off),
        1 => (float)BitConverter.ToHalf(b, off),
        2 => b[off] / 255f,
        3 => Math.Max((sbyte)b[off] / 127f, -1f),
        4 => BitConverter.ToUInt16(b, off) / 65535f,
        5 => Math.Max(BitConverter.ToInt16(b, off) / 32767f, -1f),
        6 => b[off],
        7 => (sbyte)b[off],
        8 => BitConverter.ToUInt16(b, off),
        9 => BitConverter.ToInt16(b, off),
        10 => BitConverter.ToUInt32(b, off),
        11 => BitConverter.ToInt32(b, off),
        _ => 0f,
    };
}
