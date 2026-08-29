using UnityEngine;

namespace NightRunnersMP.Ui;

/// <summary>Centre-screen IMGUI dialog for entering a player name and host address.</summary>
public sealed class ConnectPanel
{
    private const string AddressControl = "nrmp_addr";

    private GUIStyle? _box, _title, _label, _field, _button, _hint;
    private Texture2D? _bg;
    private bool _focusPending;

    public bool Open { get; private set; }
    public string Name = "";
    public string Address = "";
    /// <summary>Set by the Connect button or Enter; Core consumes it.</summary>
    public bool ConnectRequested;
    /// <summary>Set by the Cancel button or Esc; Core consumes it.</summary>
    public bool CancelRequested;

    public void Show(string name, string address)
    {
        Name = name;
        Address = address;
        ConnectRequested = false;
        CancelRequested = false;
        _focusPending = true;
        Open = true;
    }

    public void Close() => Open = false;

    public void Draw()
    {
        if (!Open) return;
        EnsureStyles();

        // Enter / Esc must be taken from the IMGUI event stream: while a text field has keyboard
        // focus, these keys are delivered here and not reliably through Input.GetKeyDown.
        var e = Event.current;
        if (e != null && e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { ConnectRequested = true; e.Use(); }
            else if (e.keyCode == KeyCode.Escape) { CancelRequested = true; e.Use(); }
        }

        const float w = 500f, h = 200f;
        var x = (Screen.width - w) / 2f;
        var y = (Screen.height - h) / 2f;

        GUI.Box(new Rect(x, y, w, h), "", _box);
        GUI.Label(new Rect(x + 16f, y + 12f, w - 32f, 24f), "CONNECT TO A HOST", _title);

        GUI.Label(new Rect(x + 16f, y + 50f, 120f, 26f), "Your name", _label);
        Name = GUI.TextField(new Rect(x + 140f, y + 50f, w - 156f, 26f), Name, 24, _field);

        GUI.Label(new Rect(x + 16f, y + 88f, 120f, 26f), "Host address", _label);
        GUI.SetNextControlName(AddressControl);
        Address = GUI.TextField(new Rect(x + 140f, y + 88f, w - 156f, 26f), Address, 96, _field);
        GUI.Label(new Rect(x + 140f, y + 116f, w - 156f, 20f), "IP or hostname, optional :port   e.g. 100.99.206.114  or  abc.playit.gg:12345", _hint);

        if (GUI.Button(new Rect(x + 140f, y + 148f, 150f, 32f), "Connect   [Enter]", _button)) ConnectRequested = true;
        if (GUI.Button(new Rect(x + 300f, y + 148f, 150f, 32f), "Cancel   [Esc]", _button)) CancelRequested = true;

        // Keep keyboard focus on the address field so typing always lands somewhere useful.
        if (_focusPending || string.IsNullOrEmpty(GUI.GetNameOfFocusedControl()))
        {
            GUI.FocusControl(AddressControl);
            _focusPending = false;
        }
    }

    private void EnsureStyles()
    {
        if (_field != null) return;

        _bg = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        _bg.SetPixel(0, 0, new Color(0.05f, 0.05f, 0.08f, 0.95f));
        _bg.Apply();

        _box = new GUIStyle();
        _box.normal.background = _bg;

        _title = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold };
        _title.normal.textColor = new Color(1f, 0.85f, 0.3f);

        _label = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleLeft };
        _label.normal.textColor = Color.white;

        _field = new GUIStyle(GUI.skin.textField) { fontSize = 15, alignment = TextAnchor.MiddleLeft };
        _button = new GUIStyle(GUI.skin.button) { fontSize = 14 };

        _hint = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        _hint.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
    }
}
