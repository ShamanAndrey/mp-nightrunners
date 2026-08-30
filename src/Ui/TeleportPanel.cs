using System;
using System.Collections.Generic;
using UnityEngine;

namespace NightRunnersMP.Ui;

/// <summary>Centre-screen list of teleport destinations: arrows/W-S + Enter, or click. Esc closes.</summary>
public sealed class TeleportPanel
{
    public sealed class Entry
    {
        public string Label = "";
        public string Hint = "";
        public Action Action = () => { };
    }

    private const float Width = 520f, RowH = 24f, Pad = 12f;
    private const int MaxVisible = 16;

    private readonly List<Entry> _entries = new();
    private GUIStyle? _box, _title, _row, _rowSel, _hint;
    private Texture2D? _bg, _sel;
    private int _selected;
    private int _scroll;
    private int _openedFrame = -1;

    public bool Open { get; private set; }
    public Entry? Confirmed;   // consumed by Core
    public bool CancelRequested;

    public void Show(IEnumerable<Entry> entries)
    {
        _entries.Clear();
        _entries.AddRange(entries);
        _selected = 0; _scroll = 0;
        Confirmed = null; CancelRequested = false;
        _openedFrame = Time.frameCount;
        Open = true;
    }

    public void Close() => Open = false;

    public void MoveSelection(int delta)
    {
        if (_entries.Count == 0) return;
        _selected = (_selected + delta + _entries.Count) % _entries.Count;
        if (_selected < _scroll) _scroll = _selected;
        if (_selected >= _scroll + MaxVisible) _scroll = _selected - MaxVisible + 1;
    }

    public void ConfirmSelection()
    {
        if (_entries.Count > 0) Confirmed = _entries[_selected];
    }

    public void Draw()
    {
        if (!Open) return;
        EnsureStyles();

        var e = Event.current;
        if (e != null && e.type == EventType.KeyDown && Time.frameCount > _openedFrame + 1)
        {
            switch (e.keyCode)
            {
                case KeyCode.UpArrow: case KeyCode.W: MoveSelection(-1); e.Use(); break;
                case KeyCode.DownArrow: case KeyCode.S: MoveSelection(1); e.Use(); break;
                case KeyCode.Return: case KeyCode.KeypadEnter: ConfirmSelection(); e.Use(); break;
                case KeyCode.Escape: CancelRequested = true; e.Use(); break;
            }
        }

        var visible = Math.Min(MaxVisible, _entries.Count);
        var h = Pad * 2 + 30f + visible * RowH + 22f;
        var x = (Screen.width - Width) / 2f;
        var y = (Screen.height - h) / 2f;
        GUI.Box(new Rect(x, y, Width, h), "", _box);
        GUI.Label(new Rect(x + Pad, y + Pad, Width - Pad * 2, 24f), "TELEPORT", _title);

        var ry = y + Pad + 30f;
        for (var i = _scroll; i < _scroll + visible && i < _entries.Count; i++)
        {
            var entry = _entries[i];
            var rect = new Rect(x + Pad, ry, Width - Pad * 2, RowH);
            var isSel = i == _selected;
            if (GUI.Button(rect, "", GUIStyle.none)) { _selected = i; Confirmed = entry; }
            GUI.Label(rect, (isSel ? "▶ " : "   ") + entry.Label + (entry.Hint.Length > 0 ? $"   <color=#999999>{entry.Hint}</color>" : ""), isSel ? _rowSel : _row);
            ry += RowH;
        }
        var more = _entries.Count > visible ? $"   ({_scroll + 1}–{Math.Min(_scroll + visible, _entries.Count)} of {_entries.Count})" : "";
        GUI.Label(new Rect(x + Pad, ry + 2f, Width - Pad * 2, 20f), $"<color=#a0a0a0>↑↓ / W S select   Enter go   Esc close   — or click{more}</color>", _hint);
    }

    private void EnsureStyles()
    {
        if (_row != null) return;

        _bg = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        _bg.SetPixel(0, 0, new Color(0.05f, 0.05f, 0.08f, 0.95f)); _bg.Apply();
        _sel = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        _sel.SetPixel(0, 0, new Color(1f, 0.85f, 0.3f, 0.18f)); _sel.Apply();

        _box = new GUIStyle(); _box.normal.background = _bg;
        _title = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold };
        _title.normal.textColor = new Color(1f, 0.85f, 0.3f);
        _row = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = true, alignment = TextAnchor.MiddleLeft };
        _row.normal.textColor = Color.white;
        _rowSel = new GUIStyle(_row); _rowSel.normal.background = _sel; _rowSel.normal.textColor = new Color(1f, 0.92f, 0.6f);
        _hint = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true };
    }
}
