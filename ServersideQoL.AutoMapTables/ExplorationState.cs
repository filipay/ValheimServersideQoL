using System.IO;

namespace ServersideQoL.AutoMapTables;

internal sealed class ExplorationState
{
  const int FormatVersion = 3;
  readonly byte[] _published;
  readonly Dictionary<long, byte[]> _pending = [];
  readonly Dictionary<long, string> _names = [];
  readonly Dictionary<long, double> _nextSamples = [];
  readonly Dictionary<long, SharedMapData.Discovery[]> _generated = [];
  readonly HashSet<long> _authoritativeOwners = [];
  bool _dirty;

  public int TextureSize { get; }
  public float PixelSize { get; }
  public long Revision { get; private set; }
  internal bool HasGenerated => _generated.Count > 0;
  public ReadOnlySpan<byte> Published => _published;
  public ReadOnlySpan<byte> GetPending(long characterID) => _pending.TryGetValue(characterID, out var mask) ? mask : ReadOnlySpan<byte>.Empty;
  public string? GetName(long characterID) => _names.TryGetValue(characterID, out var name) ? name : null;

  public ExplorationState(int textureSize, float pixelSize)
  {
    if (textureSize <= 0 || (long)textureSize * textureSize > int.MaxValue - 7)
      throw new ArgumentOutOfRangeException(nameof(textureSize));
    if (!float.IsFinite(pixelSize) || pixelSize <= 0)
      throw new ArgumentOutOfRangeException(nameof(pixelSize));
    TextureSize = textureSize;
    PixelSize = pixelSize;
    _published = new byte[(textureSize * textureSize + 7) / 8];
  }

  public bool RememberName(long characterID, string? name)
  {
    if (characterID == 0 || string.IsNullOrWhiteSpace(name) || GetName(characterID) == name)
      return false;
    _names[characterID] = name!;
    _dirty = true;
    return true;
  }

  internal float GetSampleDelay(long characterID, double now)
    => _nextSamples.TryGetValue(characterID, out var next) ? (float)Math.Max(0, next - now) : 0;

  internal void SampleCohort(IEnumerable<(long ID, string? Name, float X, float Z, bool InInterior)> observations,
    double now, float interval, float radius, Action<byte[]> persist)
  {
    try
    {
      foreach (var observation in observations)
      {
        if (observation.ID == 0)
          continue;
        _nextSamples.TryGetValue(observation.ID, out var next);
        if (ScheduleSample(now, interval, ref next, out _))
        {
          _nextSamples[observation.ID] = next;
          RememberName(observation.ID, observation.Name);
          Sample(observation.ID, observation.X, observation.Z, radius, observation.InInterior);
        }
      }
    }
    finally
    {
      // Flush completed observations even if a later character cannot be acquired.
      Flush(persist);
    }
  }

  internal static bool ScheduleSample(double now, float interval, ref double next, out float delay)
  {
    var due = now >= next;
    if (due)
      next = now + interval;
    // Early repeats or revision-triggered calls must not postpone the existing deadline.
    delay = (float)(next - now);
    return due;
  }

  public bool Sample(long characterID, float x, float z, float radius, bool inInterior)
  {
    if (characterID == 0 || inInterior || !float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(radius) || radius < 0)
      return false;

    var pixelX = x / PixelSize + TextureSize / 2;
    var pixelY = z / PixelSize + TextureSize / 2;
    var pixelRadius = Math.Ceiling(radius / PixelSize);
    if (!float.IsFinite(pixelX) || !float.IsFinite(pixelY) || pixelRadius > int.MaxValue ||
        Math.Abs((double)pixelX) > int.MaxValue - 64001 || Math.Abs((double)pixelY) > int.MaxValue - 64001)
      return false;

    // Match Valheim 1.0.20 Utils.RoundToInt, including its float precision.
    var px = (int)(pixelX + 64000.5f) - 64000;
    var py = (int)(pixelY + 64000.5f) - 64000;
    var minX = (int)Math.Max(0, px - pixelRadius);
    var maxX = (int)Math.Min(TextureSize - 1, px + pixelRadius);
    var minY = (int)Math.Max(0, py - pixelRadius);
    var maxY = (int)Math.Min(TextureSize - 1, py + pixelRadius);
    _pending.TryGetValue(characterID, out var mask);
    var changed = false;
    for (int iy = minY; iy <= maxY; iy++)
    {
      for (int ix = minX; ix <= maxX; ix++)
      {
        var dx = (double)ix - px;
        var dy = (double)iy - py;
        if (dx * dx + dy * dy > pixelRadius * pixelRadius)
          continue;
        var index = iy * TextureSize + ix;
        var bit = (byte)(1 << (index % 8));
        if ((_published[index / 8] & bit) != 0 || (mask is not null && (mask[index / 8] & bit) != 0))
          continue;
        if (mask is null)
          _pending.Add(characterID, mask = new byte[_published.Length]);
        mask[index / 8] |= bit;
        _dirty = true;
        changed = true;
      }
    }
    return changed;
  }

  public bool Publish(long characterID, bool eligible)
  {
    if (!eligible || !_pending.TryGetValue(characterID, out var mask))
      return false;
    var changed = false;
    for (int i = 0; i < mask.Length; i++)
    {
      var union = (byte)(_published[i] | mask[i]);
      changed |= union != _published[i];
      _published[i] = union;
    }
    _pending.Remove(characterID);
    _dirty = true;
    if (changed)
      Revision++;
    return changed;
  }

  internal bool Reconcile(byte[]? payload, bool eligibleNearby, IEnumerable<SharedMapData.Contribution> contributors,
    Func<long, bool> generatedOwner, Action<byte[]> persist, Action<byte[]> writeTable,
    Func<SharedMapData.Discovery, SharedMapData.Pin?>? project = null, SharedMapData.UpdateCursor? cursor = null, uint dataRevision = 0)
  {
    if (!eligibleNearby)
      return false;
    project ??= static discovery => discovery.Pin;
    var updates = contributors.ToArray();
    // Incomplete collectors retry upstream; they cannot require publication or acknowledge freshness.
    if (cursor is not null && cursor.DataRevision == dataRevision && cursor.SharedRevision == Revision &&
        updates.All(update => !update.Complete || (GetPending(update.CharacterID).IsEmpty &&
          _authoritativeOwners.Contains(update.Owner) &&
          Project(update.Discoveries).SequenceEqual(Project(_generated.TryGetValue(update.CharacterID, out var old) ? old : [])))) &&
        cursor.Emitted.SequenceEqual(EmittedPins(project)))
      return false;
    // The entire client payload is validated before touching pending or published state.
    var table = SharedMapData.Decode(payload, TextureSize);
    if (updates.Any(x => x.CharacterID == 0) || updates.Select(x => x.CharacterID).Distinct().Count() != updates.Length)
      throw new InvalidDataException("Invalid publication contributors");
    var previousPublished = (byte[])_published.Clone();
    var previousPending = new Dictionary<long, byte[]>(_pending);
    var previousGenerated = new Dictionary<long, SharedMapData.Discovery[]>(_generated);
    var previousOwners = _authoritativeOwners.ToArray();
    var previousRevision = Revision;
    var previousDirty = _dirty;
    var committed = false;
    try
    {
      var seeded = false;
      for (int i = 0; i < _published.Length; i++)
      {
        var union = (byte)(_published[i] | table.Explored[i]);
        seeded |= union != _published[i];
        _published[i] = union;
      }
      if (seeded)
      {
        Revision++;
        _dirty = true;
      }
      foreach (var update in updates)
      {
        if (!update.Complete)
          continue;
        if (!generatedOwner(update.Owner))
          throw new InvalidDataException("Invalid generated contributor owner");
        Publish(update.CharacterID, true);
        if (_authoritativeOwners.Add(update.Owner))
        {
          Revision++;
          _dirty = true;
        }
        if (!_generated.TryGetValue(update.CharacterID, out var old) || !old.SequenceEqual(update.Discoveries))
        {
          if (update.Discoveries.Length == 0)
          {
            if (!_generated.Remove(update.CharacterID))
              continue;
          }
          else
          {
            if (update.Discoveries.Any(x => string.IsNullOrEmpty(x.Source) ||
                System.Text.Encoding.UTF8.GetByteCount(x.Source) > 128 || x.Pin.Owner != update.Owner ||
                x.Kind < SharedMapData.DiscoveryKind.Unknown || x.Kind > SharedMapData.DiscoveryKind.Dungeon))
              throw new InvalidDataException("Invalid generated discovery");
            _generated[update.CharacterID] = update.Discoveries.ToArray();
          }
          Revision++;
          _dirty = true;
        }
      }
      // Legacy generated pins stay local until their owner supplies a complete authoritative snapshot.
      var pins = table.Pins.Where(pin => !generatedOwner(pin.Owner) || !_authoritativeOwners.Contains(pin.Owner))
        .Concat(EmittedPins(project)).ToArray();
      var changed = !table.Explored.AsSpan().SequenceEqual(_published) || !table.Pins.SequenceEqual(pins);
      var replacement = changed ? SharedMapData.Encode(_published, TextureSize, pins) : null;
      // Persist even an all-overlap pending removal; Publish's return value only describes new bits.
      Flush(persist);
      committed = true;
      if (replacement is not null)
        writeTable(replacement);
      if (cursor is not null)
      {
        cursor.DataRevision = dataRevision;
        cursor.SharedRevision = Revision;
        cursor.Emitted = EmittedPins(project);
      }
      return changed;
    }
    catch
    {
      if (!committed)
      {
        previousPublished.CopyTo(_published, 0);
        _pending.Clear();
        foreach (var entry in previousPending)
          _pending.Add(entry.Key, entry.Value);
        _generated.Clear();
        foreach (var entry in previousGenerated)
          _generated.Add(entry.Key, entry.Value);
        _authoritativeOwners.Clear();
        _authoritativeOwners.UnionWith(previousOwners);
        Revision = previousRevision;
        _dirty = previousDirty;
      }
      throw;
    }

    IEnumerable<SharedMapData.Discovery> Project(IEnumerable<SharedMapData.Discovery> discoveries)
      => discoveries.Select(discovery => (Discovery: discovery, Pin: project(discovery)))
        .Where(x => x.Pin.HasValue).Select(x => x.Discovery with { Pin = x.Pin!.Value });
  }

  SharedMapData.Pin[] EmittedPins(Func<SharedMapData.Discovery, SharedMapData.Pin?> project)
    => _generated.Values.SelectMany(x => x).Select(project).Where(x => x.HasValue).Select(x => x!.Value).ToArray();

  internal void ResolveGeneratedMetadata(Func<SharedMapData.Discovery, SharedMapData.Discovery> resolve)
  {
    foreach (var id in _generated.Keys.ToArray())
    {
      var old = _generated[id];
      var resolved = old.Select(x => x.Kind == SharedMapData.DiscoveryKind.Unknown ? resolve(x) : x).ToArray();
      if (old.SequenceEqual(resolved))
        continue;
      _generated[id] = resolved;
      _dirty = true;
      Revision++;
    }
  }

  internal bool RemoveGenerated(string source) => PruneGenerated(candidate => candidate != source);

  internal bool PruneGenerated(Func<string, bool> exists)
  {
    var changed = false;
    foreach (var id in _generated.Keys.ToArray())
    {
      var old = _generated[id];
      var retained = old.Where(x => exists(x.Source)).ToArray();
      if (old.Length == retained.Length)
        continue;
      if (retained.Length == 0)
        _generated.Remove(id);
      else
        _generated[id] = retained;
      changed = true;
    }
    if (changed)
    {
      Revision++;
      _dirty = true;
    }
    return changed;
  }

  internal void Flush(Action<byte[]> persist)
  {
    if (!_dirty)
      return;
    persist(Serialize());
    _dirty = false;
  }

  public byte[] Serialize()
  {
    if (Revision < 0)
      throw new InvalidDataException("Invalid exploration revision");
    var retainedCount = 0;
    foreach (var (id, discoveries) in _generated)
    {
      // Apply the reader's aggregate limit to retained data, including categories hidden at emission.
      if (id == 0 || discoveries.Length == 0 || discoveries.Length > SharedMapData.MaxPins - retainedCount)
        throw new InvalidDataException("Invalid generated discovery count");
      retainedCount += discoveries.Length;
    }
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(FormatVersion);
    writer.Write(TextureSize);
    writer.Write(PixelSize);
    writer.Write(Revision);
    writer.Write(_published);
    writer.Write(_pending.Count);
    foreach (var (id, mask) in _pending)
    {
      writer.Write(id);
      writer.Write(mask);
    }
    writer.Write(_names.Count);
    foreach (var (id, name) in _names)
    {
      writer.Write(id);
      writer.Write(name);
    }
    writer.Write(_generated.Count);
    foreach (var (id, discoveries) in _generated)
    {
      writer.Write(id);
      writer.Write(discoveries.Length);
      foreach (var discovery in discoveries)
      {
        if (string.IsNullOrEmpty(discovery.Source) || System.Text.Encoding.UTF8.GetByteCount(discovery.Source) > 128 ||
            discovery.Kind < SharedMapData.DiscoveryKind.Unknown || discovery.Kind > SharedMapData.DiscoveryKind.Dungeon)
          throw new InvalidDataException("Invalid generated discovery metadata");
        writer.Write(discovery.Source);
        SharedMapData.WritePin(writer, discovery.Pin);
        writer.Write((int)discovery.Kind);
        writer.Write(discovery.PrefabHash);
      }
    }
    writer.Write(_authoritativeOwners.Count);
    foreach (var owner in _authoritativeOwners)
    {
      if (owner == 0)
        throw new InvalidDataException("Invalid authoritative owner");
      writer.Write(owner);
    }
    return stream.ToArray();
  }

  public static ExplorationState Deserialize(byte[] data, int textureSize, float pixelSize)
  {
    try
    {
      using var stream = new MemoryStream(data, writable: false);
      using var reader = new BinaryReader(stream);
      var version = reader.ReadInt32();
      if ((version < 1 || version > FormatVersion) || reader.ReadInt32() != textureSize || reader.ReadSingle() != pixelSize)
        throw new InvalidDataException("Unsupported exploration state version or map settings");
      var state = new ExplorationState(textureSize, pixelSize);
      state.Revision = reader.ReadInt64();
      if (state.Revision < 0)
        throw new InvalidDataException("Invalid exploration revision");
      ReadMask().CopyTo(state._published, 0);
      var count = reader.ReadInt32();
      if (count < 0 || count > (stream.Length - stream.Position) / (sizeof(long) + state._published.Length))
        throw new InvalidDataException("Invalid pending character count");
      for (int i = 0; i < count; i++)
      {
        var id = reader.ReadInt64();
        if (id == 0 || !state._pending.TryAdd(id, ReadMask()))
          throw new InvalidDataException("Invalid pending character ID");
      }
      count = reader.ReadInt32();
      if (count < 0 || count > (stream.Length - stream.Position) / (sizeof(long) + 1))
        throw new InvalidDataException("Invalid character name count");
      for (int i = 0; i < count; i++)
      {
        var id = reader.ReadInt64();
        var name = reader.ReadString();
        if (id == 0 || string.IsNullOrWhiteSpace(name) || !state._names.TryAdd(id, name))
          throw new InvalidDataException("Invalid character name");
      }
      if (version >= 2)
      {
        count = reader.ReadInt32();
        if (count < 0 || count > SharedMapData.MaxPins || count > (stream.Length - stream.Position) / 12)
          throw new InvalidDataException("Invalid discovery contributor count");
        var total = 0;
        for (int i = 0; i < count; i++)
        {
          var id = reader.ReadInt64();
          var pinCount = reader.ReadInt32();
          total += pinCount;
          if (id == 0 || pinCount <= 0 || pinCount > SharedMapData.MaxPins || total > SharedMapData.MaxPins ||
              pinCount > (stream.Length - stream.Position) / 28)
            throw new InvalidDataException("Invalid generated discovery count");
          var discoveries = new SharedMapData.Discovery[pinCount];
          for (int p = 0; p < pinCount; p++)
          {
            var source = SharedMapData.ReadString(reader, 128);
            if (source.Length == 0)
              throw new InvalidDataException("Invalid discovery source");
            var pin = SharedMapData.ReadPin(reader);
            var kind = version >= 3 ? (SharedMapData.DiscoveryKind)reader.ReadInt32() : SharedMapData.DiscoveryKind.Unknown;
            var prefab = version >= 3 ? reader.ReadInt32() : 0;
            if (kind < SharedMapData.DiscoveryKind.Unknown || kind > SharedMapData.DiscoveryKind.Dungeon)
              throw new InvalidDataException("Invalid discovery category");
            discoveries[p] = new(source, pin, kind, prefab);
            if (version == 2)
              state._authoritativeOwners.Add(pin.Owner);
          }
          if (!state._generated.TryAdd(id, discoveries))
            throw new InvalidDataException("Duplicate discovery contributor");
        }
      }
      if (version >= 3)
      {
        count = reader.ReadInt32();
        if (count < 0 || count > (stream.Length - stream.Position) / sizeof(long))
          throw new InvalidDataException("Invalid authoritative owner count");
        for (int i = 0; i < count; i++)
        {
          var owner = reader.ReadInt64();
          if (owner == 0 || !state._authoritativeOwners.Add(owner))
            throw new InvalidDataException("Invalid authoritative owner");
        }
      }
      if (stream.Position != stream.Length)
        throw new InvalidDataException("Trailing exploration state data");
      return state;

      byte[] ReadMask()
      {
        if (stream.Length - stream.Position < state._published.Length)
          throw new InvalidDataException("Truncated exploration mask");
        return reader.ReadBytes(state._published.Length);
      }
    }
    catch (Exception ex) when (ex is EndOfStreamException or FormatException or ArgumentException)
    {
      throw new InvalidDataException("Invalid exploration state", ex);
    }
  }
}
