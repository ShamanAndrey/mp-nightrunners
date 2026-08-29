using System.Collections.Generic;
using UnityEngine;

namespace NightRunnersMP.Ui;

/// <summary>Bottom-left chat: recent lines fade out unless the input line is open.</summary>
public sealed class ChatPanel
{
    private const string InputControl = "nrmp_chat";
    private const int MaxHistory = 60;
    private const int VisibleLines = 8;
    private const float FadeAfter = 15f;
    private const float Width = 560f, LineH = 20f, Pad = 8f;

    private readonly struct Line
    {
        public readonly float Time; public readonly string Text;
        public Line(float time, string text) { Time = time; Text = text; }
    }

    private readonly List<Line> _lines = new();
    private GUIStyle? _text, _shadow, _field, _box;
    private Texture2D? _bg;
    private bool _focusPending;
    private int _openedFrame = -1;

    public bool InputOpen { get; private set; }
    public string Text = "";
    public bool SendRequested;
    public bool CancelRequested;

    public void AddPlayer(string name, string text, bool own = false) =>
        Add($"<color={(own ? "#9fd3ff" : "#ffd24d")}>{name}:</color> {text}");

    public void AddSystem(string text) => Add($"<color=#a0a0a0>* {text}</color>");

    private void Add(string line)
    {
        _lines.Add(new Line(Time.realtimeSinceStartup, line));
        if (_lines.Count > MaxHistory) _lines.RemoveAt(0);
    }

    public void OpenInput()
    {
        Text = "";
        SendRequested = false;
        CancelRequested = false;
        _focusPending = true;
        _openedFrame = Time.frameCount;
        InputOpen = true;
    }

    public void CloseInput() => InputOpen = false;

    public void Draw()
    {
        EnsureStyles();
        var now = Time.realtimeSinceStartup;

        // Which lines to show: everything recent, or the last few while typing.
        var start = Mathf.Max(0, _lines.Count - VisibleLines);
        var visible = new List<string>(VisibleLines);
        for (var i = start; i < _lines.Count; i++)
            if (InputOpen || now - _lines[i].Time < FadeAfter) visible.Add(_lines[i].Text);

        if (visible.Count == 0 && !InputOpen) return;

        var rows = visible.Count + (InputOpen ? 1 : 0);
        var height = rows * LineH + Pad * 2;
        var x = 10f;
        var y = Screen.height - 10f - height;
        if (InputOpen) GUI.Box(new Rect(x, y, Width, height), "", _box);

        var ly = y + Pad;
        foreach (var line in visible)
        {
            GUI.Label(new Rect(x + Pad + 1f, ly + 1f, Width - Pad * 2, LineH), line, _shadow);
            GUI.Label(new Rect(x + Pad, ly, Width - Pad * 2, LineH), line, _text);
            ly += LineH;
        }

        if (!InputOpen) return;

        // Enter / Esc come through the IMGUI event stream while the field has focus.
        var e = Event.current;
        if (e != null && e.type == EventType.KeyDown && Time.frameCount > _openedFrame + 1)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { SendRequested = true; e.Use(); }
            else if (e.keyCode == KeyCode.Escape) { CancelRequested = true; e.Use(); }
        }

        GUI.SetNextControlName(InputControl);
        Text = GUI.TextField(new Rect(x + Pad, ly, Width - Pad * 2, LineH + 2f), Text, 200, _field);
        if (_focusPending || string.IsNullOrEmpty(GUI.GetNameOfFocusedControl()))
        {
            GUI.FocusControl(InputControl);
            _focusPending = false;
        }
    }

    private void EnsureStyles()
    {
        if (_text != null) return;

        _bg = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        _bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.6f));
        _bg.Apply();
        _box = new GUIStyle();
        _box.normal.background = _bg;

        _text = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = true, wordWrap = false };
        _text.normal.textColor = Color.white;
        _shadow = new GUIStyle(_text) { richText = false };
        _shadow.normal.textColor = new Color(0f, 0f, 0f, 0.85f);

        _field = new GUIStyle(GUI.skin.textField) { fontSize = 15 };
    }
}
