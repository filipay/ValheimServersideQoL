namespace ServersideQoL.AutoMapTables;

internal sealed class WardIndex<TID> where TID : notnull
{
  internal const int MaxPendingIDs = 4096;
  readonly HashSet<TID> _members = [];
  readonly object _gate = new();
  readonly Queue<TID> _changed = new();
  readonly HashSet<TID> _pending = [];
  bool _ready;
  bool _recording;
  internal int PendingCount { get { lock (_gate) return _pending.Count; } }
  internal bool NeedsSeed { get { lock (_gate) return !_ready; } }

  public void Suspend()
  {
    lock (_gate) SuspendCore();
  }

  void SuspendCore()
  {
    _recording = _ready = false;
    _changed.Clear();
    _pending.Clear();
  }

  public void BeginSeed()
  {
    lock (_gate)
    {
      SuspendCore();
      _members.Clear();
      _recording = true;
    }
  }

  public void Seed(TID id, bool isWard)
  {
    if (isWard && !EqualityComparer<TID>.Default.Equals(id, default!))
      _members.Add(id);
  }

  public void CompleteSeed()
  {
    lock (_gate) _ready = _recording;
  }

  // Lifecycle callbacks may run off-thread: retain IDs only, never pooled objects or Unity state.
  public void Changed(TID oldID, TID newID)
  {
    lock (_gate)
    {
      if (!_recording)
        return;
      QueueID(oldID);
      if (_recording && !EqualityComparer<TID>.Default.Equals(oldID, newID))
        QueueID(newID);
    }
  }

  void QueueID(TID id)
  {
    if (EqualityComparer<TID>.Default.Equals(id, default!) || _pending.Contains(id))
      return;
    if (_pending.Count == MaxPendingIDs)
    {
      // No servicing (for example zero peers): stop recording, and require a complete reseed before access.
      SuspendCore();
      return;
    }
    _pending.Add(id);
    _changed.Enqueue(id);
  }

  public bool Service(Func<TID, bool> isWard)
  {
    while (true)
    {
      TID id;
      lock (_gate)
      {
        if (!_ready) return false;
        if (_changed.Count == 0) return true;
        id = _changed.Dequeue();
        _pending.Remove(id); // A concurrent repeat must be queued for resolution again after this lookup.
      }
      try
      {
        if (isWard(id)) _members.Add(id);
        else _members.Remove(id);
      }
      catch
      {
        Changed(id, id);
        throw;
      }
    }
  }

  public WardAccess.TableAccess? Prepare(Func<TID, bool> isWard, Func<TID, WardAccess.Snapshot?> read)
  {
    if (!Service(isWard))
      return null;
    List<WardAccess.Snapshot> covering = [];
    _members.RemoveWhere(id => {
      if (read(id) is not { } ward)
        return true;
      if (ward.Enabled && WardAccess.Covers(ward.DistanceXZ, ward.Radius))
        covering.Add(ward);
      return false;
    });
    lock (_gate) return _ready ? new(covering.ToArray()) : null;
  }
}
