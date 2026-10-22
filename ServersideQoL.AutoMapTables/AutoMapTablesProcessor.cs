using BepInEx.Configuration;
using HarmonyLib;
using ServersideQoL.Processors;
using ServersideQoL.Utilities;
using System.Reflection;
using UnityEngine;

namespace ServersideQoL.AutoMapTables;

[Processor(Id)]
[DependsOn<PlayerRegistryProcessor>]
public sealed class AutoMapTablesProcessor : Processor<AutoMapTablesProcessor.PrefabInfo>
{
  public const string Id = "05450dd6-13bd-42cc-9bd3-b1eed5e501af";

  public sealed record PrefabInfo(MapTable? MapTable, PrivateArea? PrivateArea, TeleportWorld? TeleportWorld, Ship? Ship, MineRock5? MineRock5, LocationProxy? LocationProxy, Player? Player) : ProcessorPrefabInfo
  {
    public Config.OreDepositConfig? MineRockPinConfig => field ??= MineRock5 is not null && Config.Instance.AutoUpdateOreDeposits.TryGetValue(PrefabInfo.PrefabHash, out var value) ? value : null;
    public Piece? Piece => field ??= PrefabInfo.GetComponent<Piece>();
    public override bool IsValid => this switch
    {
      { PrivateArea: not null } or { TeleportWorld: not null } or { Ship: not null } => Piece is not null && PrefabInfo.HasComponent<PieceTable>(),
      { MineRock5: not null } => MineRockPinConfig is not null,
      _ => true
    };
  }

  readonly Dictionary<ServersideQoLZDO, MapTableState> _mapTables = [];
  readonly Dictionary<PlayerID, PlayerState> _playerStates = [];
  readonly Dictionary<ServersideQoLZDO, SharedMapData.Pin> _shipPositions = [];
  readonly Dictionary<PlayerID, (Peer Peer, PlayerState PlayerState)> _updateList = [];
  float _mapTableRangeSqr;
  float _oreDepositRangeSqr;
  float _dungeonRangeSqr;
  ExplorationState? _exploration;
  bool _explorationLoadFailed;
  bool _explorationWarningLogged;
  bool _explorationReady;
  bool _wardPatchesInstalled;
  bool _wardWarningLogged;
  ZDOMan? _wardZDOMan;
  static readonly WardIndex<ZDOID> __wardIndex = new();

  static readonly ServerVar<HashSet<PlayerID>> __playerIDsVar = AutoMapTablesPlugin.RegisterServerVar<HashSet<PlayerID>>("PlayerIDs");
  static readonly ServerVar<byte[]> __explorationVar = AutoMapTablesPlugin.RegisterServerVar<byte[]>("ExplorationState");

  protected override void Initialize()
  {
    Config.Instance.ConfigChanged -= OnWardConfigChanged;
    Config.Instance.ConfigChanged += OnWardConfigChanged;
    __wardIndex.BeginSeed();
    if (!_wardPatchesInstalled)
    {
      AutoMapTablesPlugin.HarmonyInstance.PatchAll(typeof(WardLifecyclePatches));
      _wardPatchesInstalled = true;
    }
    if (_wardZDOMan is not null)
      _wardZDOMan.m_onZDODestroyed -= QueueWardDestroyed;
    _wardZDOMan = ZDOMan.instance;
    _wardZDOMan.m_onZDODestroyed += QueueWardDestroyed;
    _wardWarningLogged = false;
    _mapTableRangeSqr = Config.Instance.MapTableRange.Value * Config.Instance.MapTableRange.Value;
    _oreDepositRangeSqr = Config.Instance.OreDepositsDiscoverRange.Value * Config.Instance.OreDepositsDiscoverRange.Value;
    _dungeonRangeSqr = Config.Instance.DungeonsDiscoverRange.Value * Config.Instance.DungeonsDiscoverRange.Value;
    _exploration = null;
    _explorationLoadFailed = false;
    _explorationWarningLogged = false;
    _explorationReady = false;

    _mapTables.Clear();
    _playerStates.Clear();
    _shipPositions.Clear();

    foreach (var zdo in ZDOMan.instance.m_objectsByID.Values.Select(static x => x.ServersideQoLZDO))
    {
      __wardIndex.Seed(zdo.ZDO.m_uid, GetPrefabInfo(zdo).GetComponent<PrivateArea>() is not null);
      switch (GetProcessorPrefabInfo(zdo))
      {
        case { MapTable: not null }:
          _mapTables.Add(zdo, new(zdo));
          zdo.Destroyed += OnMapTableDestroyed;
          break;

        case { TeleportWorld: not null }:
          GetOrAddPlayerState(zdo.Vars.GetCreator()).Portals.Add(zdo);
          zdo.Destroyed += OnPortalDestroyed;
          break;

        case { Ship: not null }:
          GetOrAddPlayerState(zdo.Vars.GetCreator()).Ships.Add(zdo);
          zdo.Destroyed += OnShipDestroyed;
          break;

        case { MineRockPinConfig: not null }:
          if (!Character.InInterior(zdo.ZDO.GetPosition()))
          {
            zdo.Destroyed += OnOreDepositDestroyed;
            if (__playerIDsVar.Get(zdo) is { } playerIDs)
            {
              foreach (var id in playerIDs)
                GetOrAddPlayerState(id).OreVeins.Add(zdo);
            }
          }
          break;

        case { LocationProxy: not null }:
          {
            zdo.Destroyed += OnDungeonDestroyed;
            if (__playerIDsVar.Get(zdo) is { } playerIDs)
            {
              foreach (var id in playerIDs)
                GetOrAddPlayerState(id).Dungeons.Add(zdo, null);

              if (Config.Instance.DungeonsPinType.Value is not Minimap.PinType.None)
              {
                var hash = zdo.Vars.GetLocation();
                if (hash is not 0 && ZoneSystem.instance.m_locationsByHash.TryGetValue(hash, out var location) &&
                    location.m_prefab is { IsValid: true, IsLoaded: false, IsLoading: false })
                {
                  location.m_prefab.LoadAsync();
                }
              }
            }
          }
          break;
      }
    }

    _playerStates.Remove(default);
    __wardIndex.CompleteSeed();
    if (!Config.Instance.Enabled.Value)
      __wardIndex.Suspend();
  }

  protected override ProcessResult Process(ServersideQoLZDO zdo, IReadOnlyList<Peer> peers, PrefabInfo prefabInfo)
  {
    if (prefabInfo.Player is not null)
    {
      foreach (var peer in peers.Enumerate())
      {
        if (peer.ZNetPeer.m_characterID == zdo.ZDO.m_uid)
          return ScheduleReprocessing(_explorationLoadFailed ? 1 :
            _exploration?.GetSampleDelay(zdo.Vars.GetPlayerID().Value, Time.realtimeSinceStartupAsDouble) ?? 0);
      }
      return default;
    }

    ProcessResult result;

    if (prefabInfo.MapTable is not null)
    {
      if (!_mapTables.TryGetValue(zdo, out var state))
      {
        _mapTables.Add(zdo, state = new(zdo));
        zdo.Destroyed += OnMapTableDestroyed;
      }

      if (!TryServiceWardIndex())
        return ScheduleReprocessing(1);
      WardAccess.TableAccess? access;
      try
      {
        access = __wardIndex.Prepare(IsCurrentWard, id => ReadCurrentWard(id, zdo.ZDO.GetPosition()));
        if (access is null)
          throw new InvalidOperationException("Ward membership initialization is incomplete");
        _wardWarningLogged = false;
      }
      catch (Exception ex)
      {
        if (!_wardWarningLogged)
          Logger.LogWarning($"Map table publication suspended until ward authorization can be prepared: {ex.Message}");
        _wardWarningLogged = true;
        return ScheduleReprocessing(1);
      }
      _updateList.Clear();
      foreach (var peer in peers.Enumerate())
      {
        if (ZDOMan.instance.GetZDO(peer.ZNetPeer.m_characterID) is not { } character ||
            GetProcessorPrefabInfo(character.ServersideQoLZDO)?.Player is null)
          continue;
        var playerID = character.ServersideQoLZDO.Vars.GetPlayerID();
        var distance = Utils.DistanceSqr(zdo.ZDO.GetPosition(), character.GetPosition());
        if (!float.IsFinite(distance) || distance > _mapTableRangeSqr)
          continue;
        if (playerID.Value == 0 || !access.CanWrite(playerID.Value))
          continue;
        var playerState = GetOrAddPlayerState(playerID);
        if (!_updateList.TryAdd(playerID, (peer, playerState)))
        {
          if (peer.ZNetPeer.m_rpc.GetTimeSinceLastPing() < _updateList[playerID].Peer.ZNetPeer.m_rpc.GetTimeSinceLastPing())
            _updateList[playerID] = (peer, playerState);
        }
      }

      if (_updateList.Count > 0)
      {
        try
        {
          UpdateMapTable(state, _updateList);
        }
        finally
        {
          _updateList.Clear();
        }
      }

      result = ScheduleReprocessing(1);
    }
    else if (prefabInfo.PrivateArea is not null)
    {
      result = default;
    }
    else if (prefabInfo.TeleportWorld is not null)
    {
      var playerID = zdo.Vars.GetCreator();
      if (playerID.Value is 0)
        return ProcessResult.UnregisterProcessor;
      if (peers.Any(x => x.PlayerState?.PlayerID == playerID))
      {
        var state = GetOrAddPlayerState(playerID);
        if (state.Portals.Add(zdo))
        {
          zdo.Destroyed += OnPortalDestroyed;
          state.UpToDateMapTables.Clear();
        }
      }
      result = default;
    }
    else if (prefabInfo.Ship is not null)
    {
      var playerID = zdo.Vars.GetCreator();
      if (playerID.Value is 0)
        return ProcessResult.UnregisterProcessor;
      if (peers.Any(x => x.PlayerState?.PlayerID == playerID))
      {
        var state = GetOrAddPlayerState(playerID);
        if (state.Ships.Add(zdo))
        {
          zdo.Destroyed += OnShipDestroyed;
          state.UpToDateMapTables.Clear();
        }
      }
      result = default;
    }
    else if (prefabInfo is { MineRock5: not null, MineRockPinConfig: not null })
    {
      if (Character.InInterior(zdo.ZDO.GetPosition()))
        result = ProcessResult.UnregisterProcessor;
      else
      {
        HashSet<PlayerID>? ids = null;
        foreach (var peer in peers.Enumerate())
        {
          if (peer.PlayerState?.PlayerID is not { } playerID)
            continue;
          if (Utils.DistanceSqr(zdo.ZDO.GetPosition(), peer.RefPos) > _oreDepositRangeSqr)
            continue;
          var playerState = GetOrAddPlayerState(playerID);
          if (!playerState.OreVeins.Add(zdo))
            continue;
          zdo.Destroyed -= OnOreDepositDestroyed;
          zdo.Destroyed += OnOreDepositDestroyed;
          ids ??= __playerIDsVar.Get(zdo) ?? [];
          ids.Add(playerID);

          if (prefabInfo.MineRockPinConfig.PinType.Value is Minimap.PinType.None)
            continue;

          ShowMessage([peer], zdo, Config.Instance.Localization.Value.Discovered(prefabInfo.MineRock5.m_name), Config.Instance.DiscoveredMessageType.Value);
          if ((Config.Instance.OreDepositsPinTarget.Value & Config.PinTargets.MapTable) is not 0)
            playerState.UpToDateMapTables.Clear();
          if ((Config.Instance.OreDepositsPinTarget.Value & Config.PinTargets.DiscoveringPlayer) is not 0)
            RPC.DiscoverLocationResponse(peer.ZNetPeer.m_uid, prefabInfo.MineRockPinConfig.Label.Value, prefabInfo.MineRockPinConfig.PinType.Value, zdo.ZDO.GetPosition(), showMap: false);
        }
        if (ids is not null)
          __playerIDsVar.Set(zdo, ids);
        result = default;
      }
    }
    else if (prefabInfo.LocationProxy is not null)
    {
      zdo.Destroyed -= OnDungeonDestroyed;
      zdo.Destroyed += OnDungeonDestroyed;
      var hash = zdo.Vars.GetLocation();
      if (hash is 0)
        return default;

      using var loc = ZoneSystem.instance.GetAndLoadLocationByHash(hash);
      if (!loc.IsValid)
        return ProcessResult.UnregisterProcessor;

      if (loc.Prefab is not { } prefab)
        return ScheduleReprocessing();

      HashSet<PlayerID>? ids = null;
      foreach (var component in prefab.GetComponentsInChildren<Teleport>())
      {
        var pos = zdo.ZDO.GetPosition() + zdo.ZDO.GetRotation() * component.gameObject.transform.position;
        if (Character.InInterior(pos))
          continue;

        foreach (var peer in peers.Enumerate())
        {
          if (peer.PlayerState?.PlayerID is not { } playerID)
            continue;
          if (Utils.DistanceSqr(zdo.ZDO.GetPosition(), peer.RefPos) > _dungeonRangeSqr)
            continue;
          var playerState = GetOrAddPlayerState(playerID);
          if (!playerState.Dungeons.TryAdd(zdo, (pos, component.m_enterText)))
            continue;
          ids ??= __playerIDsVar.Get(zdo) ?? [];
          ids.Add(playerID);

          if (Config.Instance.DungeonsPinType.Value is Minimap.PinType.None)
            continue;

          ShowMessage([peer], zdo, Config.Instance.Localization.Value.Discovered(component.m_enterText), Config.Instance.DiscoveredMessageType.Value);
          if ((Config.Instance.DungeonsPinTarget.Value & Config.PinTargets.MapTable) is not 0)
            playerState.UpToDateMapTables.Clear();
          if ((Config.Instance.DungeonsPinTarget.Value & Config.PinTargets.DiscoveringPlayer) is not 0)
            RPC.DiscoverLocationResponse(peer.ZNetPeer.m_uid, Config.Instance.DungeonsLabel.Value is Config.DefaultOreDepositName ? component.m_enterText : Config.Instance.DungeonsLabel.Value, Config.Instance.DungeonsPinType.Value, zdo.ZDO.GetPosition(), showMap: false);
        }

        break;
      }

      if (ids is not null)
        __playerIDsVar.Set(zdo, ids);

      result = ScheduleReprocessing(0.5f);
    }
    else
    {
      result = ProcessResult.UnregisterProcessor;
      Logger.DevLog($"Unexpected prefab: {prefabInfo.PrefabInfo.PrefabName}");
    }
    return result;
  }

  protected override void PreProcess(PeersEnumerable peers)
  {
    TryServiceWardIndex();
    _explorationReady = false;
    var minimap = Minimap.instance;
    if (minimap is null || minimap.m_textureSize <= 0 || (long)minimap.m_textureSize * minimap.m_textureSize > int.MaxValue - 7 ||
        !float.IsFinite(minimap.m_pixelSize) || minimap.m_pixelSize <= 0 ||
        !float.IsFinite(minimap.m_exploreInterval) || minimap.m_exploreInterval <= 0 ||
        !float.IsFinite(minimap.m_exploreRadius) || minimap.m_exploreRadius < 0 ||
        Math.Ceiling(minimap.m_exploreRadius / minimap.m_pixelSize) > int.MaxValue)
    {
      if (!_explorationWarningLogged)
      {
        Logger.LogWarning("Travel sampling suspended: runtime Minimap settings are unavailable or invalid");
        _explorationWarningLogged = true;
      }
      return;
    }

    if (_explorationLoadFailed)
      return;
    if (_exploration is null)
    {
      try
      {
        _exploration = __explorationVar.Get(DataZDO) is { } data
          ? ExplorationState.Deserialize(Utils.Decompress(data), minimap.m_textureSize, minimap.m_pixelSize)
          : new(minimap.m_textureSize, minimap.m_pixelSize);
        if (_exploration.HasGenerated)
        {
          var liveSources = ZDOMan.instance.m_objectsByID.Values.ToDictionary(static value => value.m_uid.ToString());
          _exploration.PruneGenerated(liveSources.ContainsKey);
          _exploration.ResolveGeneratedMetadata(discovery => liveSources.TryGetValue(discovery.Source, out var source)
            ? discovery with { Kind = DiscoveryKind(source.ServersideQoLZDO), PrefabHash = source.GetPrefab() } : discovery);
        }
      }
      catch (Exception ex)
      {
        _explorationLoadFailed = true;
        Logger.LogWarning($"Travel sampling disabled; retaining unreadable exploration state: {ex.Message}");
        return;
      }
    }
    if (_exploration.TextureSize != minimap.m_textureSize || _exploration.PixelSize != minimap.m_pixelSize)
    {
      if (!_explorationWarningLogged)
      {
        Logger.LogWarning("Travel sampling suspended: runtime Minimap geometry changed; retaining exploration state");
        _explorationWarningLogged = true;
      }
      return;
    }
    try
    {
      _exploration.SampleCohort(ExplorationObservations(peers), Time.realtimeSinceStartupAsDouble,
        minimap.m_exploreInterval, minimap.m_exploreRadius, PersistExploration);
      _explorationWarningLogged = false;
      _explorationReady = true;
    }
    catch (Exception ex)
    {
      if (!_explorationWarningLogged)
        Logger.LogWarning($"Unable to finish or persist exploration cohort; will retry: {ex.Message}");
      _explorationWarningLogged = true;
    }
  }

  static IEnumerable<(long ID, string? Name, float X, float Z, bool InInterior)> ExplorationObservations(PeersEnumerable peers)
  {
    foreach (var peer in peers)
    {
      if (ZDOMan.instance.GetZDO(peer.ZNetPeer.m_characterID) is not { } zdo ||
          GetProcessorPrefabInfo(zdo.ServersideQoLZDO)?.Player is null)
        continue;
      var character = zdo.ServersideQoLZDO;
      var pos = zdo.GetPosition();
      yield return (character.Vars.GetPlayerID().Value, character.Vars.GetPlayerName(), pos.x, pos.z, Character.InInterior(pos));
    }
  }

  static void PersistExploration(byte[] data) => __explorationVar.Set(DataZDO, Utils.Compress(data));

  PlayerState GetOrAddPlayerState(PlayerID playerID)
  {
    if (!_playerStates.TryGetValue(playerID, out var state))
      _playerStates.Add(playerID, state = new(playerID));
    return state;
  }

  static void OnWardConfigChanged(object sender, SettingChangedEventArgs args)
  {
    if (!((Config)sender).Enabled.Value)
      __wardIndex.Suspend();
  }

  bool TryServiceWardIndex()
  {
    try
    {
      if (!Config.Instance.Enabled.Value)
      {
        __wardIndex.Suspend();
        return false;
      }
      if (__wardIndex.NeedsSeed)
      {
        __wardIndex.BeginSeed();
        foreach (var zdo in ZDOMan.instance.m_objectsByID.Values)
          __wardIndex.Seed(zdo.m_uid, GetPrefabInfo(zdo.GetPrefab()).GetComponent<PrivateArea>() is not null);
        __wardIndex.CompleteSeed();
      }
      if (!Config.Instance.Enabled.Value)
      {
        __wardIndex.Suspend();
        return false;
      }
      if (!__wardIndex.Service(IsCurrentWard))
        throw new InvalidOperationException("Ward membership needs a complete reseed");
      return true;
    }
    catch (Exception ex)
    {
      if (!_wardWarningLogged)
        Logger.LogWarning($"Unable to service ward membership; table publication remains suspended: {ex.Message}");
      _wardWarningLogged = true;
      return false;
    }
  }

  static void QueueWardDestroyed(ZDO zdo) => __wardIndex.Changed(zdo.m_uid, default);

  static bool IsCurrentWard(ZDOID id) => ZDOMan.instance.GetZDO(id) is { } zdo &&
    GetPrefabInfo(zdo.GetPrefab()).GetComponent<PrivateArea>() is not null;

  static WardAccess.Snapshot? ReadCurrentWard(ZDOID id, Vector3 tablePosition)
  {
    if (ZDOMan.instance.GetZDO(id) is not { } zdo || GetPrefabInfo(zdo.GetPrefab()).GetComponent<PrivateArea>() is not { } area)
      return null;
    var ward = zdo.ServersideQoLZDO;
    var enabled = ward.Vars.GetEnabled();
    // Read persisted markers directly: generic component accessors memoize HasFields across receives.
    var radius = WardAccess.ReadRadius(ZNetView.CustomFieldsStr, area.GetType().Name, area.m_radius,
      key => zdo.GetBool(key), (key, fallback) => zdo.GetFloat(key, fallback));
    var distance = Utils.DistanceXZ(zdo.GetPosition(), tablePosition);
    if (!enabled || !WardAccess.Covers(distance, radius))
      return new(enabled, distance, radius, 0, []);
    List<long> permitted = [];
    var count = ward.Vars.GetPermitted();
    for (int i = 0; i < count; i++)
    {
      var playerID = zdo.GetLong(Invariant($"pu_id{i}"));
      if (playerID != 0)
        permitted.Add(playerID);
    }
    return new(enabled, distance, radius, ward.Vars.GetCreator().Value, permitted.ToArray());
  }

  [HarmonyPatch]
  static class WardLifecyclePatches
  {
    [HarmonyTargetMethods]
    public static IEnumerable<MethodInfo> TargetMethods()
    {
      var zdo = new ZDO();
      yield return ((Delegate)zdo.SetPrefab).Method;
      yield return ((Delegate)zdo.Deserialize).Method;
      yield return ((Delegate)zdo.Load).Method;
      yield return ((Delegate)zdo.LoadOldFormat).Method;
      yield return ((Delegate)zdo.Reset).Method;
    }

    [HarmonyPrefix]
    public static void Prefix(ZDO __instance, out ZDOID __state) => __state = __instance.m_uid;

    [HarmonyPostfix]
    public static void Postfix(ZDO __instance, ZDOID __state) => __wardIndex.Changed(__state, __instance.m_uid);
  }

  void UpdateMapTable(MapTableState state, Dictionary<PlayerID, (Peer Peer, PlayerState PlayerState)> peers)
  {
    if (!_explorationReady || _exploration is null)
      return;
    try
    {
      var contributions = peers.Values.Select(x => CollectDiscoveries(x.Item2)).ToArray();
      var changed = _exploration.Reconcile(state.ZDO.Vars.GetData(), peers.Count > 0,
        contributions, static owner => new PlayerID(owner).IsModPlayerID(), PersistExploration, data => state.ZDO.Vars.SetData(data),
        EmitDiscovery, state.Cursor, state.ZDO.ZDO.DataRevision);
      foreach (var contribution in contributions)
      {
        if (contribution.Complete)
          peers[new(contribution.CharacterID)].Item2.UpToDateMapTables.Add(state.ZDO);
      }
      state.Cursor.DataRevision = state.ZDO.ZDO.DataRevision;
      state.FailedDataRevision = null;
      if (changed)
        ShowMessage(peers.Values.Select(static x => x.Item1), state.ZDO, Config.Instance.Localization.Value.Updated, Config.Instance.UpdatedMessageType.Value);
    }
    catch (Exception ex)
    {
      if (state.FailedDataRevision != state.ZDO.ZDO.DataRevision)
      {
        Logger.LogWarning($"Unable to finish map table reconciliation; retaining recoverable state for retry: {ex.Message}");
        state.FailedDataRevision = state.ZDO.ZDO.DataRevision;
      }
    }
  }

  static SharedMapData.DiscoveryKind DiscoveryKind(ServersideQoLZDO zdo)
  {
    var prefab = GetPrefabInfo(zdo);
    if (prefab.HasComponent<TeleportWorld>()) return SharedMapData.DiscoveryKind.Portal;
    if (prefab.HasComponent<Ship>()) return SharedMapData.DiscoveryKind.Ship;
    if (prefab.HasComponent<MineRock5>()) return SharedMapData.DiscoveryKind.Ore;
    if (prefab.HasComponent<LocationProxy>()) return SharedMapData.DiscoveryKind.Dungeon;
    return SharedMapData.DiscoveryKind.Unknown;
  }

  SharedMapData.Pin? EmitDiscovery(SharedMapData.Discovery discovery)
  {
    var type = discovery.Kind switch
    {
      SharedMapData.DiscoveryKind.Portal => Config.Instance.PortalsPinType.Value,
      SharedMapData.DiscoveryKind.Ship => Config.Instance.ShipsPinType.Value,
      SharedMapData.DiscoveryKind.Ore when (Config.Instance.OreDepositsPinTarget.Value & Config.PinTargets.MapTable) != 0 &&
        Config.Instance.AutoUpdateOreDeposits.TryGetValue(discovery.PrefabHash, out var ore) => ore.PinType.Value,
      SharedMapData.DiscoveryKind.Dungeon when (Config.Instance.DungeonsPinTarget.Value & Config.PinTargets.MapTable) != 0 => Config.Instance.DungeonsPinType.Value,
      _ => Minimap.PinType.None
    };
    if (type == Minimap.PinType.None)
      return null;
    var pin = discovery.Pin with { Type = (int)type };
    if (discovery.Kind == SharedMapData.DiscoveryKind.Ship && new PlayerID(pin.Owner).IsModPlayerID(out PlayerID builderID))
      pin = pin with { Name = ShipLabel.Format(pin.Name, _exploration?.GetName(builderID.Value)) };
    return pin;
  }

  SharedMapData.Contribution CollectDiscoveries(PlayerState playerState)
  {
    List<SharedMapData.Discovery> pins = [];
    var complete = true;
    void Add(ServersideQoLZDO zdo, string name, Minimap.PinType type)
    {
      var pos = zdo.ZDO.GetPosition();
      var kind = DiscoveryKind(zdo);
      var pin = new SharedMapData.Pin(playerState.PlayerID.AsModPlayerID().Value,
        name, pos.x, pos.y, pos.z, (int)type, false, AutoMapTablesPlugin.PluginGuid);
      if (kind == SharedMapData.DiscoveryKind.Ship)
      {
        if (_shipPositions.TryGetValue(zdo, out var previous))
          pin = SharedMapData.StabilizeShipPosition(pin, previous, Minimap.instance.m_pixelSize);
        _shipPositions[zdo] = pin;
      }
      pins.Add(new(zdo.ZDO.m_uid.ToString(), pin, kind, zdo.ZDO.GetPrefab()));
    }

    foreach (var zdo in playerState.Portals)
      Add(zdo, zdo.Vars.GetTag(), Config.Instance.PortalsPinType.Value);

    foreach (var zdo in playerState.Ships)
      Add(zdo, GetProcessorPrefabInfo(zdo)!.Piece!.m_name, Config.Instance.ShipsPinType.Value);

    foreach (var zdo in playerState.OreVeins)
    {
      if (GetProcessorPrefabInfo(zdo) is { MineRockPinConfig: { } cfg, MineRock5: { } mineRock })
        Add(zdo, cfg.Label.Value is Config.DefaultOreDepositName ? mineRock.m_name : cfg.Label.Value, cfg.PinType.Value);
    }

    List<(ServersideQoLZDO, Vector3, string)>? updatedPos = null;
    foreach (var (zdo, nullableValue) in playerState.Dungeons)
    {
      if (nullableValue is not { } value)
      {
        var hash = zdo.Vars.GetLocation();
        if (hash is 0)
        {
          complete = false;
          continue;
        }

        using var loc = ZoneSystem.instance.GetAndLoadLocationByHash(hash);
        if (loc.Prefab is not { } prefab)
        {
          complete = false;
          continue;
        }

        var found = false;
        value = default;
        foreach (var component in prefab.GetComponentsInChildren<Teleport>())
        {
          var pos = zdo.ZDO.GetPosition() + zdo.ZDO.GetRotation() * component.gameObject.transform.position;
          if (Character.InInterior(pos))
            continue;
          found = true;
          (updatedPos ??= []).Add((zdo, pos, component.m_enterText));
          value = (pos, component.m_enterText);
          break;
        }

        if (!found)
          continue;
      }

      Add(zdo, Config.Instance.DungeonsLabel.Value is Config.DefaultOreDepositName ? value.Item2! : Config.Instance.DungeonsLabel.Value, Config.Instance.DungeonsPinType.Value);
    }

    if (updatedPos is not null)
    {
      foreach (var (zdo, pos, text) in updatedPos)
        playerState.Dungeons[zdo] = (pos, text);
    }
    return new(playerState.PlayerID.Value, pins.OrderBy(static pin => pin.Source, StringComparer.Ordinal).ToArray(),
      complete, playerState.PlayerID.AsModPlayerID().Value);
  }

  void OnMapTableDestroyed(ServersideQoLZDO zdo)
  {
    if (!_mapTables.Remove(zdo))
      return;

    foreach (var state in _playerStates.Values)
      state.UpToDateMapTables.Remove(zdo);
  }

  void OnPortalDestroyed(ServersideQoLZDO zdo)
  {
    RemoveGeneratedDiscovery(zdo);
    if (!_playerStates.TryGetValue(zdo.Vars.GetCreator(), out var state))
      return;
    if (state.Portals.Remove(zdo))
      state.UpToDateMapTables.Clear();
  }

  void OnShipDestroyed(ServersideQoLZDO zdo)
  {
    _shipPositions.Remove(zdo);
    RemoveGeneratedDiscovery(zdo);
    if (!_playerStates.TryGetValue(zdo.Vars.GetCreator(), out var state))
      return;
    if (state.Ships.Remove(zdo))
      state.UpToDateMapTables.Clear();
  }

  void OnOreDepositDestroyed(ServersideQoLZDO zdo)
  {
    RemoveGeneratedDiscovery(zdo);
    if (__playerIDsVar.Get(zdo) is not { } ids)
      return;

    foreach (var id in ids)
    {
      if (!_playerStates.TryGetValue(id, out var state))
        continue;
      if (state.OreVeins.Remove(zdo) && (Config.Instance.OreDepositsPinTarget.Value & Config.PinTargets.MapTable) is not 0)
        state.UpToDateMapTables.Clear();
    }
  }

  void OnDungeonDestroyed(ServersideQoLZDO zdo)
  {
    RemoveGeneratedDiscovery(zdo);
    if (__playerIDsVar.Get(zdo) is not { } ids)
      return;
    foreach (var id in ids)
    {
      if (_playerStates.TryGetValue(id, out var state) && state.Dungeons.Remove(zdo))
        state.UpToDateMapTables.Clear();
    }
  }

  void RemoveGeneratedDiscovery(ServersideQoLZDO zdo)
  {
    if (_exploration?.RemoveGenerated(zdo.ZDO.m_uid.ToString()) is not true)
      return;
    try
    {
      _exploration.Flush(PersistExploration);
    }
    catch (Exception ex)
    {
      Logger.LogWarning($"Unable to persist removed discovery; dirty state retained for retry: {ex.Message}");
    }
  }

  sealed class PlayerState(PlayerID playerID)
  {
    public PlayerID PlayerID { get; } = playerID;
    public HashSet<ServersideQoLZDO> UpToDateMapTables => field ??= [];
    public HashSet<ServersideQoLZDO> Portals => field ??= [];
    public HashSet<ServersideQoLZDO> Ships => field ??= [];
    public HashSet<ServersideQoLZDO> OreVeins => field ??= [];
    public Dictionary<ServersideQoLZDO, (Vector3, string)?> Dungeons => field ??= [];
  }

  sealed class MapTableState(ServersideQoLZDO zdo)
  {
    public ServersideQoLZDO ZDO { get; } = zdo;
    public SharedMapData.UpdateCursor Cursor { get; } = new();
    public uint? FailedDataRevision { get; set; }
  }
}
