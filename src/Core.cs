using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using NightRunnersMP.MapImport;
using NightRunnersMP.Net;
using NightRunnersMP.Sync;
using NightRunnersMP.Ui;
using UnityEngine;

[assembly: MelonInfo(typeof(NightRunnersMP.Core), "Night Runners MP", "0.4.0", "ShamanAndrey", "https://github.com/ShamanAndrey/mp-nightrunners")]
[assembly: MelonGame("PLANET JEM SOFTWARE", "NIGHT-RUNNERS PRIVATE ALPHA")]
[assembly: MelonGame("PLANET JEM SOFTWARE", "NIGHT-RUNNERS PROLOGUE")]

namespace NightRunnersMP;

public class Core : MelonMod
{
    private MelonPreferences_Entry<string> _playerName = null!;
    private MelonPreferences_Entry<int> _hostPort = null!;
    private MelonPreferences_Entry<string> _connectAddress = null!;
    private MelonPreferences_Entry<int> _connectPort = null!;
    private MelonPreferences_Entry<int> _sendRateHz = null!;
    private MelonPreferences_Entry<float> _ghostOffset = null!;
    private MelonPreferences_Entry<int> _interpDelayMs = null!;
    private MelonPreferences_Entry<bool> _ghostCollisions = null!;
    private MelonPreferences_Entry<bool> _trafficEnabled = null!;
    private bool _loopback;
    private float _nextTrafficCheck;
    private readonly RateGate _uploadGate = new();
    private float _nearestPlayer = -1f;
    // Rules imposed by the host while we are a client; null when we decide for ourselves.
    private bool? _hostTraffic;
    private bool? _hostCollisions;

    private bool EffectiveTraffic => _hostTraffic ?? _trafficEnabled.Value;
    private bool TrafficIsHostControlled => _hostTraffic.HasValue;
    private bool CollisionsAreHostControlled => _hostCollisions.HasValue;

    private readonly Hud _hud = new();
    private readonly List<string> _hudLines = new();
    private readonly UpdateChecker _updates = new();
    private MelonPreferences_Entry<bool> _checkUpdates = null!;
    private MelonPreferences_Entry<string> _hostPassword = null!;
    private MelonPreferences_Entry<string> _connectPassword = null!;
    private readonly ConnectPanel _connectPanel = new();
    private readonly ChatPanel _chat = new();
    private MelonPreferences_Entry<string> _chatKey = null!;
    private KeyCode _chatKeyCode = KeyCode.Return;
    private bool _controlSuspended;
    private MelonPreferences_Entry<bool> _blockUnfocused = null!;
    private MelonPreferences_Entry<bool> _chatFilter = null!;
    private MelonPreferences_Entry<string> _prologueDir = null!;
    private MelonPreferences_Entry<string> _citySpawn = null!;
    private MelonPreferences_Entry<float> _citySpawnYaw = null!;
    private CityMap? _city;

    private readonly TeleportPanel _teleport = new();
    private bool TextBoxOpen => _connectPanel.Open || _chat.InputOpen || _teleport.Open;
    private CursorLockMode _prevCursorLock;
    private bool _prevCursorVisible;

    private HostSession? _host;
    private ClientSession? _client;
    private GhostManager _ghosts = null!;

    public override void OnInitializeMelon()
    {
        Game.Detect();
        var cat = MelonPreferences.CreateCategory("NightRunnersMP");
        _playerName = cat.CreateEntry("PlayerName", "Runner");
        _hostPort = cat.CreateEntry("HostPort", 7777);
        _connectAddress = cat.CreateEntry("ConnectAddress", "127.0.0.1");
        _connectPort = cat.CreateEntry("ConnectPort", 7777);
        _sendRateHz = cat.CreateEntry("SendRateHz", 25, description: "Snapshots per second; rounded to a whole number of physics steps");
        _ghostOffset = cat.CreateEntry("GhostOffset", 0f, description: "Dev only: shift ghosts N metres right (use 4 when testing alone via loopback)");
        _interpDelayMs = cat.CreateEntry("InterpDelayMs", 80, description: "Minimum interpolation delay; the mod raises it automatically when jitter is measured");

        _ghostCollisions = cat.CreateEntry("GhostCollisions", false, description: "true: remote cars are solid (one-way, like walls). false: cars pass through each other");
        _trafficEnabled = cat.CreateEntry("TrafficEnabled", true, description: "false: AI traffic is switched off and cleared whenever a map loads (F6 toggles)");
        _checkUpdates = cat.CreateEntry("CheckForUpdates", true, description: "Ask GitHub once per launch whether a newer release exists (shown in the HUD title)");
        _hostPassword = cat.CreateEntry("HostPassword", "", description: "When hosting with F11, players must enter this password to join (empty = open)");
        _connectPassword = cat.CreateEntry("ConnectPassword", "", description: "Password used by F12 (also editable in the connect panel)");
        _chatKey = cat.CreateEntry("ChatKey", "T", description: "Key that opens the chat line (Unity KeyCode name: T, Y, Return, ...). Enter is avoided by default because the game uses it to interact.");
        if (_chatKey.Value.Equals("Return", System.StringComparison.OrdinalIgnoreCase))
        {
            _chatKey.Value = "T"; // migrate the old default; Enter clashes with the game's interact key
            MelonPreferences.Save();
        }
        if (!System.Enum.TryParse(_chatKey.Value, true, out _chatKeyCode)) _chatKeyCode = KeyCode.T;
        _blockUnfocused = cat.CreateEntry("BlockInputWhenUnfocused", true, description: "Stop the game reacting to your keyboard while its window is not focused (the game normally keeps reading keys in the background)");
        _chatFilter = cat.CreateEntry("ChatFilter", false, description: "Mask profanity in chat and player names you see (f***). Extra words: UserData\\NightRunnersMP-badwords.txt, one per line, trailing * = prefix");
        SetupChatFilter();

        _prologueDir = cat.CreateEntry("PrologueDir", "", description: "Folder of your Steam Night Runners Prologue install (auto-detected when empty); needed to load its city into the alpha with F2");
        _citySpawn = cat.CreateEntry("CitySpawn", "3.7,20.5,25.4", description: "Fallback spawn in Prologue coordinates x,y,z; normally the 'Start' marker found in the city scene is used");
        _citySpawnYaw = cat.CreateEntry("CitySpawnYaw", 0f);
        var cityLightmaps = cat.CreateEntry("CityLightmaps", true, description: "Import the Prologue's baked lightmaps for the city");
        var cityLighting = cat.CreateEntry("CitySceneLighting", true, description: "Use the Prologue's ambient light and fog while in the city");
        var citySkydome = cat.CreateEntry("CitySkydome", true, description: "Show the Prologue's panorama skydome (distant city/horizon) around the player");
        var citySkybox = cat.CreateEntry("CitySkybox", true, description: "Use the Prologue's sky (cubemap) while in the city; its lower half is the ground tone under the expressway");
        if (Game.Variant == GameVariant.Alpha)
        {
            var classData = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserLibsDirectory, "classdata.tpk");
            _city = new CityMap(_prologueDir.Value, classData, Log)
            {
                SpawnYaw = _citySpawnYaw.Value,
                UseLightmaps = cityLightmaps.Value,
                UseSceneLighting = cityLighting.Value,
                UseSkydome = citySkydome.Value,
                UseSkybox = citySkybox.Value,
                BookmarkFile = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "NightRunnersMP-bookmarks.txt"),
            };
            var parts = _citySpawn.Value.Split(',');
            if (parts.Length == 3 && float.TryParse(parts[0], out var sx) && float.TryParse(parts[1], out var sy) && float.TryParse(parts[2], out var sz))
                _city.SpawnPoint = new Vector3(sx, sy, sz);
        }

        if (_checkUpdates.Value) _updates.Start(Info.Version);

        _ghosts = new GhostManager(Log)
        {
            GhostOffset = _ghostOffset.Value,
            MinInterpDelay = _interpDelayMs.Value / 1000f,
            Collisions = _ghostCollisions.Value,
        };

        Log($"Night Runners MP loaded in '{Game.ProductName}' ({Game.DisplayName}). F5 collisions | F6 traffic | F7 HUD | F9 status | F11 host | F12 connect | F8 disconnect");
        if (Game.Variant == GameVariant.Unknown) Log("[warn] unrecognised game build — traffic control disabled, everything else best-effort");
        Log($"Config [NightRunnersMP]: name={_playerName.Value} host={_hostPort.Value} connect={_connectAddress.Value}:{_connectPort.Value} ghostOffset={_ghostOffset.Value}");
    }

    private void Log(string message)
    {
        LoggerInstance.Msg(message);
        _hud.AddLog(message);
    }

    private void SetupChatFilter()
    {
        Wire.Filter.Enabled = _chatFilter.Value;
        var path = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "NightRunnersMP-badwords.txt");
        try
        {
            if (System.IO.File.Exists(path))
            {
                var before = Wire.Filter.WordCount;
                foreach (var line in System.IO.File.ReadAllLines(path)) Wire.Filter.AddWord(line);
                Log($"[filter] loaded {Wire.Filter.WordCount - before} extra word(s) from {path}");
            }
        }
        catch (System.Exception e) { Log($"[filter] could not read {path}: {e.Message}"); }
        if (Wire.Filter.Enabled) Log($"[filter] chat filter on ({Wire.Filter.WordCount} words)");
    }

    /// <summary>Per-player choice: F3 or /filter in chat. The server's own filter is separate and enforced.</summary>
    private void SetChatFilter(bool on)
    {
        _chatFilter.Value = on;
        Wire.Filter.Enabled = on;
        MelonPreferences.Save();
        var msg = on ? "chat filter ON — profanity you see is masked (F3 or /filter off to disable)" : "chat filter off (F3 or /filter on to enable)";
        Log($"[filter] {msg}");
        _chat.AddSystem(msg);
    }

    private void CityKey()
    {
        if (_city == null) { Log("[city] F2 only applies to the alpha — in the Prologue you are already in the city"); return; }
        switch (_city.Current)
        {
            case CityMap.State.Unloaded:
            case CityMap.State.Failed:
                if (LocalCar.Rcc == null) { Log("[city] get into a car in free-roam first, then press F2"); return; }
                Log("[city] loading C1 Tatsumi from your Prologue install — the game will stutter for a while");
                _city.TeleportWhenLoaded = true;
                _city.BeginLoad();
                break;
            case CityMap.State.Loading:
                Log($"[city] {_city.Status}");
                break;
            case CityMap.State.Loaded:
                OpenTeleportPanel();
                break;
        }
    }

    private void OpenTeleportPanel()
    {
        if (_city == null || !_city.IsLoaded) return;
        var entries = new List<TeleportPanel.Entry>
        {
            new() { Label = "Spawn road (near Tatsumi PA)", Action = () => { if (!_city.TeleportPlayer()) Log("[city] no car"); } },
        };
        foreach (var t in _city.Targets)
        {
            var target = t;
            entries.Add(new TeleportPanel.Entry { Label = target.Label, Hint = target.Hint, Action = () => _city.TeleportToTarget(target) });
        }
        foreach (var kv in _city.Bookmarks)
        {
            var name = kv.Key; var pose = kv.Value;
            entries.Add(new TeleportPanel.Entry { Label = $"★ {name}", Hint = "bookmark", Action = () => _city.TeleportToPrologueCoords(pose.prologue, pose.yaw) });
        }
        if (_city.HasReturnPose) entries.Add(new TeleportPanel.Entry { Label = "Back to Mount Haruna", Action = () => _city.TeleportBack() });
        entries.Add(new TeleportPanel.Entry { Label = "Unload city", Hint = "frees memory; /tp back first if you want to keep your spot", Action = () => _city.Unload() });

        _prevCursorLock = Cursor.lockState;
        _prevCursorVisible = Cursor.visible;
        _teleport.Show(entries);
    }

    private void CloseTeleportPanel()
    {
        _teleport.Close();
        Cursor.lockState = _prevCursorLock;
        Cursor.visible = _prevCursorVisible;
    }

    /// <summary>/tp … chat commands; returns a message for the chat box.</summary>
    private string TeleportCommand(string[] parts)
    {
        if (_city == null) return "teleports only work in the alpha";
        if (!_city.IsLoaded) return "load the city first (F2)";
        if (parts.Length == 1 || parts[1] is "list" or "help")
        {
            var names = string.Join(", ", _city.Targets.ConvertAll(t => t.Label));
            var marks = _city.Bookmarks.Count > 0 ? "   bookmarks: " + string.Join(", ", _city.Bookmarks.Keys) : "";
            return $"/tp <area> | next | prev | back | save <name> | <name> | x y z   — areas: {names}{marks}";
        }
        var arg = parts[1].ToLowerInvariant();
        switch (arg)
        {
            case "next": return _city.TeleportToNextRoad(1) ? "next road piece" : "no road surfaces";
            case "prev": return _city.TeleportToNextRoad(-1) ? "previous road piece" : "no road surfaces";
            case "back": case "haruna": return _city.TeleportBack() ? "back to Mount Haruna" : "no saved Haruna position (you haven't jumped yet)";
            case "save":
                if (parts.Length < 3) return "usage: /tp save <name>";
                return _city.SaveBookmark(parts[2]) ? $"bookmark '{parts[2]}' saved" : "cannot save: not in a car";
        }
        if (parts.Length >= 4 && float.TryParse(parts[1], out var x) && float.TryParse(parts[2], out var y) && float.TryParse(parts[3], out var z))
            return _city.TeleportToPrologueCoords(new Vector3(x, y, z), 0f) ? $"teleported to {x},{y},{z}" : "teleport failed";

        var query = string.Join(' ', parts, 1, parts.Length - 1);
        if (_city.Bookmarks.TryGetValue(query, out var bm))
            return _city.TeleportToPrologueCoords(bm.prologue, bm.yaw) ? $"bookmark '{query}'" : "teleport failed";
        var target = _city.FindTarget(query);
        if (target != null) return _city.TeleportToTarget(target) ? $"→ {target.Label}" : "teleport failed";
        return $"unknown place '{query}' — /tp list";
    }

    /// <summary>Local slash commands typed into chat; returns true when handled (nothing is sent).</summary>
    private bool HandleChatCommand(string text)
    {
        if (!text.StartsWith("/")) return false;
        var parts = text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "/filter":
                if (parts.Length > 1 && parts[1] is "on" or "off") SetChatFilter(parts[1] == "on");
                else _chat.AddSystem($"chat filter is {(Wire.Filter.Enabled ? "on" : "off")} — /filter on | /filter off");
                break;
            case "/city" when parts.Length > 1 && parts[1] == "unload":
                if (_city != null) _city.Unload(); else _chat.AddSystem("no city loaded");
                break;
            case "/shot":
            {
                var dir = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "NightRunnersMP-shots");
                System.IO.Directory.CreateDirectory(dir);
                var mode = parts.Length > 1 ? parts[1] : "";
                var file = System.IO.Path.Combine(dir, $"shot-{mode}{System.DateTime.Now:yyyyMMdd-HHmmss}.png");
                if (mode == "top" || mode == "side")
                {
                    var height = parts.Length > 2 && float.TryParse(parts[2], out var hh) ? hh : (mode == "top" ? 150f : 8f);
                    var err = Ui.ShotCamera.Capture(file, mode, height);
                    _chat.AddSystem(err ?? $"screenshot ({mode}) -> {file}");
                }
                else
                {
                    ScreenCapture.CaptureScreenshot(file);
                    _chat.AddSystem($"screenshot -> {file}");
                }
                Log($"[shot] {file}");
                break;
            }
            case "/city" when parts.Length > 2 && parts[1] == "skydome":
                if (_city == null || !_city.IsLoaded) _chat.AddSystem("load the city first (F2)");
                else { _city.SetSkydome(parts[2] == "on"); _chat.AddSystem($"skydome {(parts[2] == "on" ? "on" : "off")}"); }
                break;
            case "/city" when parts.Length > 2 && parts[1] == "lighting":
                if (_city == null || !_city.IsLoaded) _chat.AddSystem("load the city first (F2)");
                else { _city.SetSceneLighting(parts[2] == "on"); _chat.AddSystem($"Prologue ambient/fog {(parts[2] == "on" ? "on" : "off")}"); }
                break;
            case "/tp":
                _chat.AddSystem(TeleportCommand(parts));
                break;
            case "/help":
                _chat.AddSystem("commands: /tp …   /filter on|off   /city unload   /help   — keys: F2 city/teleport menu, F3 filter, F5 collisions, F6 traffic, F7 HUD, F8 disconnect");
                break;
            default:
                _chat.AddSystem($"unknown command {parts[0]} — try /help");
                break;
        }
        return true;
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        Log($"Scene loaded: {sceneName}");
        if (!Game.IsStreamingSubScene(sceneName)) _ghosts.OnWorldSceneChanged();
    }

    public override void OnApplicationQuit()
    {
        Disconnect();
        GameInput.Set(false, Log);
    }

    public override void OnUpdate()
    {
        // Typing or tabbed out: the game must not see the keyboard (Rewired) and the car must not react.
        UpdateInputSuspension();

        if (_connectPanel.Open)
        {
            // Typing mode: our hotkeys stay quiet and the game's hidden/locked cursor is forced back.
            ShowCursor();
            if (_connectPanel.ConnectRequested || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                _connectPanel.ConnectRequested = false;
                ConnectFromPanel();
            }
            else if (_connectPanel.CancelRequested || Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F12))
            {
                _connectPanel.CancelRequested = false;
                CloseConnectPanel();
            }
        }
        else if (_teleport.Open)
        {
            ShowCursor();
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) _teleport.MoveSelection(-1);
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) _teleport.MoveSelection(1);
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) _teleport.ConfirmSelection();
            if (_teleport.Confirmed != null)
            {
                var entry = _teleport.Confirmed;
                _teleport.Confirmed = null;
                CloseTeleportPanel();
                entry.Action();
            }
            else if (_teleport.CancelRequested || Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F2))
            {
                _teleport.CancelRequested = false;
                CloseTeleportPanel();
            }
        }
        else if (_chat.InputOpen)
        {
            if (_chat.SendRequested || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                _chat.SendRequested = false;
                SendChatFromPanel();
            }
            else if (_chat.CancelRequested || Input.GetKeyDown(KeyCode.Escape))
            {
                _chat.CancelRequested = false;
                _chat.CloseInput();
            }
        }
        else
        {
            if (Input.GetKeyDown(_chatKeyCode)) _chat.OpenInput(); // always: /tp and /city work outside sessions too
            if (Input.GetKeyDown(KeyCode.F2)) CityKey();
            if (Input.GetKeyDown(KeyCode.F3)) SetChatFilter(!_chatFilter.Value);
            if (Input.GetKeyDown(KeyCode.F4)) OpenDownloadPage();
            if (Input.GetKeyDown(KeyCode.F5)) ToggleCollisions();
            if (Input.GetKeyDown(KeyCode.F6)) ToggleTraffic();
            if (Input.GetKeyDown(KeyCode.F7)) _hud.Visible = !_hud.Visible;
            if (Input.GetKeyDown(KeyCode.F9)) Probe();
            if (Input.GetKeyDown(KeyCode.F11)) StartHost();
            if (Input.GetKeyDown(KeyCode.F12)) OpenConnectPanel();
            if (Input.GetKeyDown(KeyCode.F8)) Disconnect();
        }

        _host?.Poll();
        _client?.Poll();

        if (_loopback && _client != null && _client.MyId >= 0) _ghosts.LoopbackId = _client.MyId;

        // Traffic "off" must survive map loads: each world scene brings a fresh coordinator that
        // starts enabled. We only ever force it OFF; when the user wants traffic we leave the
        // game's own logic (races, meets) in charge.
        if (!EffectiveTraffic && Time.realtimeSinceStartup >= _nextTrafficCheck)
        {
            _nextTrafficCheck = Time.realtimeSinceStartup + 1f;
            if (TrafficControl.Available && TrafficControl.IsEnabled)
            {
                TrafficControl.Set(false, true);
                Log($"[traffic] switched off ({(TrafficIsHostControlled ? "host rule" : "config")})");
            }
        }
    }

    private void ToggleTraffic()
    {
        if (TrafficIsHostControlled)
        {
            Log("[traffic] host-controlled while connected — only the host can toggle it");
            return;
        }

        var enable = !_trafficEnabled.Value;
        _trafficEnabled.Value = enable;
        MelonPreferences.Save();
        ApplyTraffic(enable, "F6");

        if (_host != null)
        {
            _host.TrafficEnabled = enable;
            _host.BroadcastSettings();
        }
    }

    private void ApplyTraffic(bool enable, string reason)
    {
        _nextTrafficCheck = 0f;
        if (!TrafficControl.Available)
        {
            Log($"[traffic] {(enable ? "on" : "off")} ({reason}); applies once a map is loaded");
            return;
        }
        TrafficControl.Set(enable, !enable);
        Log($"[traffic] {(enable ? "enabled" : "disabled and cleared")} ({reason})");
    }

    private void ToggleCollisions()
    {
        if (CollisionsAreHostControlled)
        {
            Log("[collisions] host-controlled while connected — only the host can toggle them");
            return;
        }

        var on = !_ghostCollisions.Value;
        _ghostCollisions.Value = on;
        MelonPreferences.Save();
        _ghosts.Collisions = on;
        Log($"[collisions] {(on ? "ON — cars are solid" : "off — cars pass through each other")} (F5)");

        if (_host != null)
        {
            _host.CollisionsEnabled = on;
            _host.BroadcastSettings();
        }
    }

    private void OnHostRules(bool traffic, bool collisions)
    {
        if (_hostTraffic != traffic)
        {
            _hostTraffic = traffic;
            ApplyTraffic(traffic, "host rule");
        }
        if (_hostCollisions != collisions)
        {
            _hostCollisions = collisions;
            _ghosts.Collisions = collisions;
            Log($"[collisions] host set collisions {(collisions ? "ON" : "off")}");
        }
    }

    private void ReleaseHostRules()
    {
        if (_hostTraffic.HasValue)
        {
            var imposed = _hostTraffic.Value;
            _hostTraffic = null;
            if (imposed != _trafficEnabled.Value) ApplyTraffic(_trafficEnabled.Value, "restored local setting");
        }
        if (_hostCollisions.HasValue)
        {
            _hostCollisions = null;
            _ghosts.Collisions = _ghostCollisions.Value;
        }
    }

    private static string TrafficStatus()
    {
        if (Game.Variant == GameVariant.Unknown) return "unsupported build";
        if (!TrafficControl.Available) return "no map loaded";
        var n = TrafficControl.ActiveCount;
        return TrafficControl.IsEnabled ? $"<color=#88ff88>on</color>, {n} active" : $"<color=#ff9933>off</color>, {n} active";
    }

    // Sample on the physics clock so snapshots are evenly spaced and pose/timestamp agree.
    // The upload rate follows the distance to the nearest other player (see SendRate tiers).
    public override void OnFixedUpdate()
    {
        _ghosts.FixedUpdate(); // solid ghosts are moved through the physics engine
        if (_host == null && _client == null) return;
        var rcc = LocalCar.Rcc;
        if (rcc == null) return;

        var rb = rcc.rigid;
        var myPos = rb != null ? rb.position : rcc.transform.position;
        _nearestPlayer = NearestPlayerDistance(myPos);
        var dist = _nearestPlayer < 0f ? 0f : _nearestPlayer; // nobody known yet: full rate
        if (!_uploadGate.ShouldSend(Time.realtimeSinceStartup, dist, Mathf.Max(1, _sendRateHz.Value))) return;

        var state = LocalCar.Sample(rcc, Time.fixedTime);
        _host?.SendState(state);
        _client?.SendState(state);
    }

    private float NearestPlayerDistance(Vector3 myPos)
    {
        var nearest = -1f;
        var myWorld = WorldOrigin.ToWorld(myPos);
        foreach (var car in _ghosts.Cars)
        {
            if (!car.HasSnapshot) continue;
            var d = Vector3.Distance(myWorld, car.LastKnownPos);
            if (nearest < 0f || d < nearest) nearest = d;
        }
        return nearest;
    }

    // Move ghosts after the game's own Update so nothing overrides the pose this frame.
    public override void OnLateUpdate()
    {
        _city?.UpdateSkydome();
        if (_connectPanel.Open || _teleport.Open) ShowCursor(); // after the game's Update, in case it re-locked the cursor
        _ghosts.Update();
    }

    private static void ShowCursor()
    {
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
        if (!Cursor.visible) Cursor.visible = true;
    }

    public override void OnGUI()
    {
        DrawNameTags();
        if (!_hud.Visible) return;
        _hudLines.Clear();

        var rcc = LocalCar.Rcc;
        _hudLines.Add(rcc != null
            ? $"Car:      {rcc.gameObject.name}  (model {LocalCar.ModelOf(rcc)})"
            : "Car:      <color=#ff9933>none — drive a car in free-roam first</color>");

        _hudLines.Add(_host != null
            ? $"Host:     <color=#88ff88>{_host.Status}</color>"
            : $"Host:     off  —  F11 to host on UDP {_hostPort.Value}{(_hostPassword.Value.Length > 0 ? " (password set)" : "")}");

        _hudLines.Add(_client != null
            ? $"Client:   <color=#88ff88>{_client.Status}</color>"
            : $"Client:   off  —  F12 to enter a host address (last: {_connectAddress.Value}:{_connectPort.Value})");

        _hudLines.Add($"Traffic:  {TrafficStatus()}   {(TrafficIsHostControlled ? "<color=#999999>(host-controlled)</color>" : $"(F6 toggles; config: {(_trafficEnabled.Value ? "on" : "off")})")}");
        if (_city != null)
            _hudLines.Add($"City:     {(_city.Current == CityMap.State.Loading ? "<color=#ff9933>" : _city.Current == CityMap.State.Loaded ? "<color=#88ff88>" : "")}{_city.Status}{(_city.Current is CityMap.State.Loading or CityMap.State.Loaded ? "</color>" : "")}   {(_city.IsLoaded ? $"(F2: teleport menu, or {_chatKey.Value} then /tp …)" : "(F2: load C1 Tatsumi from your Prologue)")}");
        _hudLines.Add($"Ghosts:   {_ghosts.Count}" + (_ghosts.Count == 0 ? "  (remote players appear here)" : ""));
        var playerPos = rcc != null ? rcc.transform.position : Vector3.zero;
        foreach (var car in _ghosts.Cars)
        {
            var dist = car.HasSnapshot && rcc != null ? Vector3.Distance(WorldOrigin.ToWorld(playerPos), car.LastKnownPos) : -1f;
            var age = car.LastSnapshotAge;
            var state = car.IsSpawned ? "<color=#88ff88>spawned</color>" : "<color=#ff9933>waiting for spawn</color>";
            var packets = float.IsNaN(age) ? "no packets yet" : age > 1f ? $"<color=#ff5555>last packet {age:F1}s ago</color>" : $"packets OK ({age * 1000f:F0} ms)";
            _hudLines.Add($"   #{car.Info.Id} {car.Info.Name}: {state}, {(dist >= 0 ? $"{dist:F0} m away" : "position unknown")}, {packets}");
            if (car.HasSnapshot)
                _hudLines.Add($"      rx {car.ReceiveHz:F0} Hz, delay {car.Delay * 1000f:F0} ms, jitter {car.Jitter * 1000f:F1} ms, mode {car.Mode}");
        }

        var sending = _host != null || _client != null;
        var sendInfo = sending
            ? $"{_uploadGate.HzFor(Mathf.Max(1, _sendRateHz.Value)):F0} Hz (nearest player {(_nearestPlayer < 0f ? "unknown" : $"{_nearestPlayer:F0} m")}; tiers 50/150/400 m)"
            : $"{_sendRateHz.Value} Hz max, distance-scaled";
        _hudLines.Add($"Send:     {sendInfo}");
        var collisionsOn = _hostCollisions ?? _ghostCollisions.Value;
        _hudLines.Add($"Collisions: {(collisionsOn ? "<color=#88ff88>ON — cars are solid</color>" : "off — cars pass through")}   {(CollisionsAreHostControlled ? "<color=#999999>(host-controlled)</color>" : "(F5 toggles)")}");
        var origin = WorldOrigin.Offset;
        _hudLines.Add($"Options:  InterpDelay ≥{_interpDelayMs.Value} ms    LoopbackOffset {_ghostOffset.Value} m    Filter {(Wire.Filter.Enabled ? "on" : "off")}    Input: {(GameInput.Suspended ? "<color=#ff9933>game keys paused</color>" : "live")}{(origin != Vector3.zero ? $"    origin shift ({origin.x:F0}, {origin.y:F0}, {origin.z:F0})" : "")}");
        _hudLines.Add($"Keys:     F11 host   F12 connect   F8 disconnect   {_chatKey.Value} chat   F3 filter   F5 collisions   F6 traffic   F9 status→log   F7 hide HUD   F4 download page");

        _hud.Draw(HudTitle(), _hudLines);
        _chat.Draw();
        _connectPanel.Draw();
        _teleport.Draw();
    }

    private void SendChatFromPanel()
    {
        var text = _chat.Text.Trim();
        _chat.CloseInput();
        if (text.Length == 0) return;
        if (HandleChatCommand(text)) return;
        text = Wire.SanitizeChat(text);
        if (_host == null && _client == null) { _chat.AddSystem("not in a session"); return; }
        _host?.SendChat(text);
        if (!_loopback) _client?.SendChat(text);
        _chat.AddPlayer(Wire.SanitizeName(_playerName.Value), text, own: true);
    }

    private void OnChat(int senderId, string text)
    {
        var name = senderId == Wire.SystemSenderId ? null : _ghosts.NameOf(senderId) ?? $"#{senderId}";
        if (name == null) _chat.AddSystem(text);
        else _chat.AddPlayer(name, text);
    }

    private void WireSessionEvents(NetSession session)
    {
        // Chat/join notices first so PlayerLeft still resolves the name before the ghost is removed.
        session.PlayerJoined += p => _chat.AddSystem($"{p.Name} joined");
        session.PlayerLeft += id => _chat.AddSystem($"{_ghosts.NameOf(id) ?? $"#{id}"} left");
        session.ChatReceived += OnChat;
        session.PlayerJoined += _ghosts.OnPlayerJoined;
        session.PlayerLeft += _ghosts.OnPlayerLeft;
        session.StateReceived += (id, s) => _ghosts.OnState(id, s);
    }

    private string HudTitle()
    {
        var status = _updates.Status switch
        {
            UpdateChecker.State.Checking => "<color=#999999>checking for updates…</color>",
            UpdateChecker.State.UpToDate => "<color=#88ff88>up to date</color>",
            UpdateChecker.State.UpdateAvailable => $"<color=#ff9933>UPDATE v{_updates.Latest} available — F4 opens the download page</color>",
            UpdateChecker.State.Unavailable => "<color=#999999>update check unavailable</color>",
            _ => "",
        };
        return $"NIGHT RUNNERS MP  v{Info.Version}  <color=#999999>({Game.DisplayName})</color>   {status}   <color=#999999>[F7 hide]</color>";
    }

    private void OpenDownloadPage()
    {
        Log($"Opening {UpdateChecker.ReleasesUrl}");
        Application.OpenURL(UpdateChecker.ReleasesUrl);
    }

    private void DrawNameTags()
    {
        if (_ghosts.Count == 0) return;
        var cam = Camera.main;
        if (cam == null) return;
        var me = LocalCar.Rcc;
        var myPos = me != null ? me.transform.position : cam.transform.position;

        foreach (var car in _ghosts.Cars)
        {
            if (!car.IsSpawned || car.IsHidden) continue;
            var world = car.RenderedPos + Vector3.up * 1.9f;
            var sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f) continue; // behind the camera
            var dist = Vector3.Distance(myPos, car.RenderedPos);
            _hud.DrawTag(sp, dist < 1000f ? $"{car.Info.Name}  {dist:F0} m" : $"{car.Info.Name}  {dist / 1000f:F1} km");
        }
    }

    private PlayerInfo Self()
    {
        var rcc = LocalCar.Rcc;
        return new PlayerInfo { Id = -1, Name = Wire.SanitizeName(_playerName.Value), Model = rcc != null ? LocalCar.ModelOf(rcc) : 0 };
    }

    private void StartHost()
    {
        if (_host != null) { Log($"Already {_host.Status}"); return; }
        var host = new HostSession(_hostPort.Value, Self(), Log, _hostPassword.Value)
        {
            TrafficEnabled = _trafficEnabled.Value,
            CollisionsEnabled = _ghostCollisions.Value,
            FullRateHz = Mathf.Max(1, _sendRateHz.Value),
        };
        if (!host.Start()) return;
        WireSessionEvents(host);
        _host = host;
        _chat.AddSystem($"hosting on UDP {_hostPort.Value} — {_chatKey.Value} opens chat");
    }

    private void OpenConnectPanel()
    {
        if (_client != null) { Log($"Already {_client.Status} — F8 to disconnect first"); return; }
        var lastAddr = _connectPort.Value == 7777 ? _connectAddress.Value : $"{_connectAddress.Value}:{_connectPort.Value}";
        _prevCursorLock = Cursor.lockState;
        _prevCursorVisible = Cursor.visible;
        _connectPanel.Show(_playerName.Value, lastAddr, _connectPassword.Value);
    }

    private void CloseConnectPanel()
    {
        _connectPanel.Close();
        Cursor.lockState = _prevCursorLock;
        Cursor.visible = _prevCursorVisible;
    }

    /// <summary>Parse "host" or "host:port", persist name/address, then connect.</summary>
    private void ConnectFromPanel()
    {
        var name = _connectPanel.Name.Trim();
        var text = _connectPanel.Address.Trim();
        if (text.Length == 0) { Log("[client] enter the host's address first"); return; }

        var host = text;
        var port = _connectPort.Value;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text.Substring(colon + 1), out var p) && p is > 0 and < 65536)
        {
            host = text.Substring(0, colon);
            port = p;
        }

        if (name.Length > 0) _playerName.Value = Wire.SanitizeName(name);
        _connectAddress.Value = host;
        _connectPort.Value = port;
        _connectPassword.Value = _connectPanel.Password.Trim();
        MelonPreferences.Save();

        CloseConnectPanel();
        Connect();
    }

    /// <summary>
    /// Two layers: Rewired's keyboard controller (everything the game binds) and RCC's canControl
    /// (the car), both off while a text box is open or, optionally, while the window is unfocused.
    /// </summary>
    private void UpdateInputSuspension()
    {
        var suspend = TextBoxOpen || (_blockUnfocused.Value && !Application.isFocused);
        GameInput.Set(suspend, Log);

        var rcc = LocalCar.Rcc;
        if (suspend)
        {
            if (rcc != null && rcc.canControl) { rcc.canControl = false; _controlSuspended = true; }
        }
        else if (_controlSuspended)
        {
            if (rcc != null) rcc.canControl = true;
            _controlSuspended = false;
        }
    }

    private void Connect()
    {
        if (_client != null) { Log($"Already {_client.Status}"); return; }
        var addr = _connectAddress.Value;
        var loopback = _host != null && (addr == "127.0.0.1" || addr.Equals("localhost", System.StringComparison.OrdinalIgnoreCase));

        var client = new ClientSession(addr, _connectPort.Value, Self(), Log, loopback ? _hostPassword.Value : _connectPassword.Value);
        _loopback = loopback;
        if (loopback)
        {
            // Solo test: this instance is both host and client. The host side already renders
            // the ghost for the loopback client, so the client side only transmits.
            Log("[client] loopback self-test: your own car will appear as a ghost beside you");
        }
        else
        {
            WireSessionEvents(client);
            client.RulesReceived += OnHostRules;
            _chat.AddSystem($"connecting to {addr} — {_chatKey.Value} opens chat");
        }
        client.Start();
        _client = client;
    }

    private void Disconnect()
    {
        if (_host == null && _client == null) return;
        _client?.Stop();
        _host?.Stop();
        _client = null;
        _host = null;
        _loopback = false;
        _ghosts.Clear();
        ReleaseHostRules();
        _chat.CloseInput();
        _chat.AddSystem("disconnected");
        Log("Disconnected; ghosts removed.");
    }

    private void Probe()
    {
        var god = GodConstant.Instance;
        var carParent = god != null ? god.carParent : null;
        var rcc = LocalCar.Rcc;

        Log("--- F9 status ---");
        Log($"carParent: {(carParent != null ? "OK" : "null")}");
        Log($"local car: {(rcc != null ? $"{rcc.gameObject.name} model={LocalCar.ModelOf(rcc)} @ {rcc.transform.position} (origin offset {WorldOrigin.Offset}, world {WorldOrigin.ToWorld(rcc.transform.position)})" : "none")}");
        Log($"host: {(_host != null ? _host.Status : "off")}   client: {(_client != null ? _client.Status : "off")}   ghosts: {_ghosts.Count}");
    }
}
