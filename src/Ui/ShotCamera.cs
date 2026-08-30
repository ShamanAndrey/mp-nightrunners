using System;
using UnityEngine;
using NightRunnersMP.Sync;

namespace NightRunnersMP.Ui;

/// <summary>Renders a one-off view from a temporary camera around the player's car (bird's-eye or side) to a PNG.</summary>
public static class ShotCamera
{
    public static string? Capture(string file, string mode, float height)
    {
        var rcc = LocalCar.Rcc;
        var main = Camera.main;
        if (rcc == null) return "no player car";
        if (main == null) return "no main camera";
        GameObject? go = null; RenderTexture? rt = null; Texture2D? tex = null;
        try
        {
            var car = rcc.transform.position;
            var fwd = rcc.transform.forward; fwd.y = 0f; if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward; fwd.Normalize();
            var right = Vector3.Cross(Vector3.up, fwd);
            go = new GameObject("NRMP_ShotCam");
            var cam = go.AddComponent<Camera>();
            cam.CopyFrom(main);
            cam.enabled = false;
            cam.farClipPlane = Mathf.Max(main.farClipPlane, 6000f);
            if (mode == "top")
            {
                cam.transform.position = car + Vector3.up * height - fwd * (height * 0.35f);
                cam.transform.LookAt(car + fwd * (height * 0.3f), Vector3.up);
                cam.fieldOfView = 75f;
            }
            else
            {
                cam.transform.position = car + right * 6f + Vector3.up * height - fwd * 4f;
                cam.transform.LookAt(car - right * 30f + fwd * 20f + Vector3.up * 1f, Vector3.up);
                cam.fieldOfView = 90f;
            }
            const int w = 1920, h = 1080;
            rt = new RenderTexture(w, h, 24);
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;
            var png = ImageConversion.EncodeToPNG(tex);
            System.IO.File.WriteAllBytes(file, png);
            return null;
        }
        catch (Exception e) { return $"shot failed: {e.GetType().Name} {e.Message}"; }
        finally
        {
            if (go != null) UnityEngine.Object.Destroy(go);
            if (rt != null) UnityEngine.Object.Destroy(rt);
            if (tex != null) UnityEngine.Object.Destroy(tex);
        }
    }
}
