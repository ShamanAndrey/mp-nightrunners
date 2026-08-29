using System;
using System.Collections.Generic;
using Il2Cpp;
using NightRunnersMP.Net;
using UnityEngine;

namespace NightRunnersMP.Sync;

/// <summary>
/// Owns every remote car. Spawns go through the game's CarParent one at a time because
/// the spawn coroutine reports its result through a single shared field.
/// </summary>
public sealed class GhostManager
{
    private const int PreferredFallbackModel = 33; // car_carOrigin.ModelType.tobimizu_wasp_1996 (alpha)

    /// <summary>The car list differs per build (alpha ~70 models, Prologue ~20): pick something that exists here.</summary>
    private static int FallbackModel => IsKnownModel(PreferredFallbackModel) ? PreferredFallbackModel : 1;
    private const float SpawnTimeout = 30f;

    private readonly Dictionary<int, RemoteCar> _cars = new();
    private readonly Queue<RemoteCar> _spawnQueue = new();
    private readonly Action<string> _log;

    private RemoteCar? _spawning;
    private GameObject? _resultBefore;
    private float _spawnStartedAt;
    private int _loopbackId = -1;

    public float GhostOffset;            // metres to the right, applied only to the loopback ghost
    public float MinInterpDelay = 0.08f; // seconds; floor for each ghost's adaptive delay

    private bool _collisions;
    /// <summary>Whether ghosts are solid; applies to existing cars immediately and to future spawns.</summary>
    public bool Collisions
    {
        get => _collisions;
        set
        {
            _collisions = value;
            foreach (var car in _cars.Values) car.SetCollisions(value);
        }
    }

    public int Count => _cars.Count;
    public IEnumerable<RemoteCar> Cars => _cars.Values;
    public string? NameOf(int id) => _cars.TryGetValue(id, out var car) ? car.Info.Name : null;

    /// <summary>Id of our own loopback client (solo testing); that ghost gets the sideways offset.</summary>
    public int LoopbackId
    {
        get => _loopbackId;
        set
        {
            if (_loopbackId == value) return;
            _loopbackId = value;
            foreach (var kv in _cars) kv.Value.LocalOffset = OffsetFor(kv.Key);
        }
    }

    public GhostManager(Action<string> log) { _log = log; }

    private Vector3 OffsetFor(int id) => id == _loopbackId ? new Vector3(GhostOffset, 0f, 0f) : Vector3.zero;

    /// <summary>Only models the game actually defines are handed to its spawn coroutine.</summary>
    private static bool IsKnownModel(int model)
    {
        if (model <= 0) return true; // 0 = unknown, resolved to the fallback at spawn time
        try { return Enum.IsDefined(typeof(car_carOrigin.ModelType), model); }
        catch { return false; }
    }

    public void OnPlayerJoined(PlayerInfo p)
    {
        if (_cars.ContainsKey(p.Id)) return;
        if (_cars.Count >= Wire.MaxPlayers) { _log($"[ghosts] ignoring #{p.Id}: too many players"); return; }
        if (!IsKnownModel(p.Model))
        {
            _log($"[ghosts] {p.Name} sent unknown car model {p.Model}; using fallback");
            p.Model = 0;
        }
        var car = new RemoteCar(p, _collisions) { LocalOffset = OffsetFor(p.Id), MinDelay = MinInterpDelay };
        _cars[p.Id] = car;
        _spawnQueue.Enqueue(car);
        _log($"[ghosts] {p.Name} (#{p.Id}) joined with model {p.Model}; queued for spawn");
    }

    public void OnPlayerLeft(int id)
    {
        if (!_cars.Remove(id, out var car)) return;
        car.Destroy();
        _log($"[ghosts] #{id} left, car removed");
    }

    public void OnState(int id, in CarState s)
    {
        if (_cars.TryGetValue(id, out var car)) car.Push(s);
    }

    public void Update()
    {
        ProcessSpawnQueue();
        foreach (var car in _cars.Values) car.Update();
    }

    public void FixedUpdate()
    {
        foreach (var car in _cars.Values) car.FixedUpdate();
    }

    /// <summary>The world scene changed: spawned objects are gone, so spawn everyone again.</summary>
    public void OnWorldSceneChanged()
    {
        _spawning = null;
        _spawnQueue.Clear();
        foreach (var car in _cars.Values)
        {
            car.Detach();
            _spawnQueue.Enqueue(car);
        }
    }

    public void Clear()
    {
        foreach (var car in _cars.Values) car.Destroy();
        _cars.Clear();
        _spawnQueue.Clear();
        _spawning = null;
        _loopbackId = -1;
    }

    private void ProcessSpawnQueue()
    {
        var god = GodConstant.Instance;
        var carParent = god != null ? god.carParent : null;
        if (carParent == null) return;

        if (_spawning != null)
        {
            var result = carParent.StockCarSpawn_result;
            if (result != null && result != _resultBefore)
            {
                _spawning.Attach(result);
                _log($"[ghosts] spawned car for {_spawning.Info.Name} (#{_spawning.Info.Id}) -> {result.name}, RCC={(_spawning.IsSpawned ? "yes" : "NO")}, collisions={(_collisions ? "on" : "off")}");
                _spawning = null;
            }
            else if (Time.realtimeSinceStartup - _spawnStartedAt > SpawnTimeout)
            {
                _log($"[ghosts] spawn for #{_spawning.Info.Id} timed out; will retry");
                _spawnQueue.Enqueue(_spawning);
                _spawning = null;
            }
            return;
        }

        if (_spawnQueue.Count == 0) return;
        var player = LocalCar.Rcc;
        if (player == null) return; // not in the world yet

        var next = _spawnQueue.Dequeue();
        if (!_cars.ContainsKey(next.Info.Id)) return; // left while queued

        var pt = player.transform;
        var spawnPoint = new GameObject("NRMP_SpawnPoint").transform;
        if (next.HasSnapshot)
        {
            spawnPoint.position = next.LastKnownPos;
            spawnPoint.rotation = next.LastKnownRot;
        }
        else
        {
            spawnPoint.position = pt.position + pt.right * 4f + Vector3.up * 0.5f;
            spawnPoint.rotation = pt.rotation;
        }

        var model = next.Info.Model > 0 ? next.Info.Model : FallbackModel;
        _spawning = next;
        _resultBefore = carParent.StockCarSpawn_result;
        _spawnStartedAt = Time.realtimeSinceStartup;
        _log($"[ghosts] spawning model {model} for {next.Info.Name} (#{next.Info.Id}) at {spawnPoint.position}");

        carParent.StartCoroutine(carParent.StockCarSpawn(
            (car_carOrigin.ModelType)model,
            CarParent.CarSetupType.full_Combine,
            spawnPoint,
            isStillCar: true,
            engineRunning: false,
            isAutoGear: true));
    }
}
