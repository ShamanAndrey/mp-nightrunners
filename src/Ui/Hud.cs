using System;
using System.Collections.Generic;
using UnityEngine;

namespace NightRunnersMP.Ui;

/// <summary>Top-left IMGUI overlay: status lines supplied by Core plus a rolling mod log.</summary>
public sealed class Hud
{
    private const int MaxLog = 8;
    private const float Width = 620f, LineH = 20f, Pad = 10f;

    private readonly List<string> _log = new();
    private GUIStyle? _box, _text, _title, _dim;
    private Texture2D? _bg;

    public bool Visible = true;

    public void AddLog(string message)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (_log.Count > MaxLog) _log.RemoveAt(0);
    }

    public void Draw(string title, IReadOnlyList<string> lines)
    {
        if (!Visible) return;
        EnsureStyles();

        var rows = 1 + lines.Count + 1 + _log.Count;
        var height = Pad * 2 + LineH * rows + 8f;
        GUI.Box(new Rect(10f, 10f, Width, height), "", _box);

        var x = 10f + Pad;
        var y = 10f + Pad;
        var w = Width - Pad * 2;

        GUI.Label(new Rect(x, y, w, LineH + 4f), title, _title);
        y += LineH + 8f;

        foreach (var line in lines)
        {
            GUI.Label(new Rect(x, y, w, LineH), line, _text);
            y += LineH;
        }

        GUI.Label(new Rect(x, y, w, LineH), "— log —", _dim);
        y += LineH;
        foreach (var entry in _log)
        {
            GUI.Label(new Rect(x, y, w, LineH), entry, _dim);
            y += LineH;
        }
    }

    /// <summary>Name tag centred on a screen position (Unity screen space, origin bottom-left).</summary>
    public void DrawTag(Vector3 screenPos, string text)
    {
        EnsureStyles();
        const float w = 240f, h = 22f;
        var x = screenPos.x - w / 2f;
        var y = Screen.height - screenPos.y - h;
        GUI.Label(new Rect(x + 1f, y + 1f, w, h), text, _tagShadow);
        GUI.Label(new Rect(x, y, w, h), text, _tag);
    }

    private GUIStyle? _tag, _tagShadow;

    private void EnsureStyles()
    {
        if (_text != null) return;

        _tag = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _tag.normal.textColor = new Color(1f, 0.85f, 0.3f);
        _tagShadow = new GUIStyle(_tag);
        _tagShadow.normal.textColor = new Color(0f, 0f, 0f, 0.9f);

        _bg = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        _bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.78f));
        _bg.Apply();

        _box = new GUIStyle();
        _box.normal.background = _bg;

        _text = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true, wordWrap = false };
        _text.normal.textColor = Color.white;

        _title = new GUIStyle(_text) { fontSize = 16, fontStyle = FontStyle.Bold };
        _title.normal.textColor = new Color(1f, 0.85f, 0.3f);

        _dim = new GUIStyle(_text) { fontSize = 12 };
        _dim.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
    }
}
