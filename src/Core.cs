using System.Collections.Generic;
using Il2Cpp;
using Il2CppPlanetJem.Roads.Traffic;
using MelonLoader;
using NightRunnersMP.Net;
using NightRunnersMP.Sync;
using NightRunnersMP.Ui;
using UnityEngine;

[assembly: MelonInfo(typeof(NightRunnersMP.Core), "Night Runners MP", "0.2.0", "ShamanAndrey", "https://github.com/ShamanAndrey/mp-nightrunners")]
[assembly: MelonGame("PLANET JEM SOFTWARE", "NIGHT-RUNNERS PRIVATE ALPHA")]

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
    private CursorLockMode _prevCursorLock;
    private bool _prevCursorVisible;

    private HostSession? _host;
    private ClientSession? _client;
    private GhostManager _ghosts = null!;

    public override void OnInitializeMelon()
    {
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
        _chatKey = cat.CreateEntry("ChatKey", "Return", description: "Key that opens the chat line while in a session (Unity KeyCode name: Return, T, Y, ...)");
        if (!System.Enum.TryParse(_chatKey.Value, true, out _chatKeyCode)) _chatKeyCode = KeyCode.Return;

        if (_checkUpdates.Value) _updates.Start(Info.Version);

        _ghosts = new GhostManager(Log)
        {
            GhostOffset = _ghostOffset.Value,
            MinInterpDelay = _interpDelayMs.Value / 1000f,
            Collisions = _ghostCollisions.Value,
        };

        Log("Night Runners MP loaded. F5 collisions | F6 traffic | F7 HUD | F9 status | F11 host | F12 connect | F8 disconnect");
        Log($"Config [NightRunnersMP]: name={_playerName.Value} host={_hostPort.Value} connect={_connectAddress.Value}:{_connectPort.Value} ghostOffset={_ghostOffset.Value}");
    }

    private void Log(string message)
    {
        LoggerInstance.Msg(message);
        _hud.AddLog(message);
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        Log($"Scene loaded: {sceneName}");
        if (!sceneName.Contains("Chunk")) _ghosts.OnWorldSceneChanged();
    }

    public override void OnApplicationQuit() => Disconnect();

    public override void OnUpdate()
    {
        if (_connectPanel.Open)
        {
            // Typing mode: the car must not react to the keys, our hotkeys stay quiet,
            // and the game's hidden/locked cursor is forced back so the buttons are usable.
            SuspendCarControl(true);
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
        else if (_chat.InputOpen)
        {
            SuspendCarControl(true);
            if (_chat.SendRequested || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                _chat.SendRequested = false;
                SendChatFromPanel();
            }
            else if (_chat.CancelRequested || Input.GetKeyDown(KeyCode.Escape))
            {
                _chat.CancelRequested = false;
                _chat.CloseInput();
                SuspendCarControl(false);
            }
        }
        else
        {
            if ((_host != null || _client != null) && Input.GetKeyDown(_chatKeyCode)) _chat.OpenInput();
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
            var traffic = TrafficCoordinator.Instance;
            if (traffic != null && traffic.IsTrafficEnabled)
            {
                traffic.SetTrafficEnabled(false, true);
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
        var traffic = TrafficCoordinator.Instance;
        if (traffic == null)
        {
            Log($"[traffic] {(enable ? "on" : "off")} ({reason}); applies once a map is loaded");
            return;
        }
        traffic.SetTrafficEnabled(enable, !enable);
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
        var traffic = TrafficCoordinator.Instance;
        if (traffic == null) return "no map loaded";
        var active = traffic.ActiveVehicles?.TryCast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<TrafficVehicle>>();
        var n = active != null ? active.Count : 0;
        return traffic.IsTrafficEnabled ? $"<color=#88ff88>on</color>, {n} active" : $"<color=#ff9933>off</color>, {n} active";
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
        foreach (var car in _ghosts.Cars)
        {
            if (!car.HasSnapshot) continue;
            var d = Vector3.Distance(myPos, car.LastKnownPos);
            if (nearest < 0f || d < nearest) nearest = d;
        }
        return nearest;
    }

    // Move ghosts after the game's own Update so nothing overrides the pose this frame.
    public override void OnLateUpdate()
    {
        if (_connectPanel.Open) ShowCursor(); // after the game's Update, in case it re-locked the cursor
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
        _hudLines.Add($"Ghosts:   {_ghosts.Count}" + (_ghosts.Count == 0 ? "  (remote players appear here)" : ""));
        var playerPos = rcc != null ? rcc.transform.position : Vector3.zero;
        foreach (var car in _ghosts.Cars)
        {
            var dist = car.HasSnapshot && rcc != null ? Vector3.Distance(playerPos, car.LastKnownPos) : -1f;
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
        _hudLines.Add($"Options:  InterpDelay ≥{_interpDelayMs.Value} ms    LoopbackOffset {_ghostOffset.Value} m");
        _hudLines.Add($"Keys:     F11 host   F12 connect   F8 disconnect   {_chatKey.Value} chat   F5 collisions   F6 traffic   F9 status→log   F7 hide HUD   F4 download page");

        _hud.Draw(HudTitle(), _hudLines);
        _chat.Draw();
        _connectPanel.Draw();
    }

    private void SendChatFromPanel()
    {
        var text = _chat.Text.Trim();
        _chat.CloseInput();
        SuspendCarControl(false);
        if (text.Length == 0) return;
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
        return $"NIGHT RUNNERS MP  v{Info.Version}   {status}   <color=#999999>[F7 hide]</color>";
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
        _chat.AddSystem($"hosting on UDP {_hostPort.Value} — Enter opens chat");
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
        SuspendCarControl(false);
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

    private void SuspendCarControl(bool suspend)
    {
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
            _chat.AddSystem($"connecting to {addr} — Enter opens chat");
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
        Log($"local car: {(rcc != null ? $"{rcc.gameObject.name} model={LocalCar.ModelOf(rcc)} @ {rcc.transform.position}" : "none")}");
        Log($"host: {(_host != null ? _host.Status : "off")}   client: {(_client != null ? _client.Status : "off")}   ghosts: {_ghosts.Count}");
    }
}
