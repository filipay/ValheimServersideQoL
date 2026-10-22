namespace ServersideQoL.AutoMapTables.Tests;

[TestClass]
public sealed class WardIndexTests
{
  static WardAccess.Snapshot Ward(float distance = 1, float radius = 10, bool enabled = true, long creator = 1, params long[] permitted)
    => new(enabled, distance, radius, creator, permitted);

  [TestMethod]
  public void Membership_NotReadyFailsClosedAndReinitializationDropsOldWorld()
  {
    var index = new WardIndex<int>();
    Assert.IsNull(index.Prepare(_ => throw new Exception(), _ => throw new Exception()));
    index.BeginSeed();
    index.Seed(1, true);
    Assert.IsNull(index.Prepare(_ => true, _ => Ward()));
    index.CompleteSeed();
    Assert.IsFalse(index.Prepare(_ => true, _ => Ward())!.CanWrite(2));
    index.Changed(1, 2);
    index.BeginSeed();
    index.Seed(3, true);
    index.CompleteSeed();
    var reads = new List<int>();
    Assert.IsTrue(index.Prepare(_ => throw new Exception("Old queued world IDs must be discarded"), id => {
      reads.Add(id); return Ward(creator: 2);
    })!.CanWrite(2));
    CollectionAssert.AreEqual(new[] { 3 }, reads);
  }

  [TestMethod]
  public void Membership_NewUnprocessedWardAndRecreationDuringDrainAreSeenBeforeTable()
  {
    var index = new WardIndex<int>();
    var world = new Dictionary<int, WardAccess.Snapshot>();
    index.BeginSeed(); index.CompleteSeed();
    world[1] = Ward();
    index.Changed(0, 1); // SetPrefab/Deserialize postfix; no ward Process required.
    Assert.IsFalse(index.Prepare(world.ContainsKey, id => world.GetValueOrDefault(id))!.CanWrite(2));
    world.Remove(1); world[2] = Ward(distance: 100, creator: 2);
    index.Changed(1, 2); // Same pooled object; membership uses IDs, never its reference.
    var created = false;
    var access = index.Prepare(id => {
      if (!created) { created = true; world[3] = Ward(); index.Changed(0, 3); }
      return world.ContainsKey(id);
    }, id => world.TryGetValue(id, out var ward) ? ward : null)!;
    Assert.IsFalse(access.CanWrite(2)); // Newly queued covering ward must be drained in this same call.
    world.Remove(2); index.Changed(2, 0);
    Assert.IsFalse(index.Prepare(world.ContainsKey, id => world.TryGetValue(id, out var ward) ? ward : null)!.CanWrite(2));
  }

  [TestMethod]
  public void Membership_SameIDPrefabChangeAndFailedResolutionRetryUseCurrentObject()
  {
    var index = new WardIndex<int>();
    index.BeginSeed(); index.Seed(1, true); index.CompleteSeed();
    var isWard = false;
    index.Changed(1, 1);
    Assert.ThrowsExactly<InvalidOperationException>(() => index.Prepare(_ => throw new InvalidOperationException(), _ => Ward()));
    Assert.IsTrue(index.Prepare(_ => isWard, _ => throw new Exception("Current nonward must be removed"))!.CanWrite(2));
    isWard = true;
    index.Changed(1, 1);
    Assert.IsFalse(index.Prepare(_ => isWard, _ => Ward())!.CanWrite(2));
  }

  [TestMethod]
  public void Membership_DestroyResetAndPoolReuseNeverRetainOldWardIdentity()
  {
    var index = new WardIndex<int>();
    var world = new Dictionary<int, WardAccess.Snapshot> { [1] = Ward() };
    index.BeginSeed(); index.Seed(1, true); index.CompleteSeed();
    var oldID = 1; // Prefix capture before Reset mutates the pooled instance's UID.
    world.Remove(oldID);
    index.Changed(oldID, 0);
    Assert.IsTrue(index.Prepare(world.ContainsKey, id => world.TryGetValue(id, out var ward) ? ward : null)!.CanWrite(2));
    // Same reference reused as a nonward, then later as a differently identified ward.
    index.Changed(0, 2);
    Assert.IsTrue(index.Prepare(world.ContainsKey, _ => throw new Exception())!.CanWrite(2));
    world[3] = Ward(); index.Changed(2, 3);
    Assert.IsFalse(index.Prepare(world.ContainsKey, id => world[id])!.CanWrite(2));
  }

  [TestMethod]
  public void Membership_LiveOversizedRadiusEnableAndPermissionChangesNeedNoReindex()
  {
    var index = new WardIndex<int>();
    var value = Ward(distance: 500, radius: 10, enabled: false);
    index.BeginSeed(); index.Seed(1, true); index.CompleteSeed();
    WardAccess.TableAccess Prepare() => index.Prepare(_ => throw new Exception("No lifecycle change to resolve"), _ => value)!;
    Assert.IsTrue(Prepare().CanWrite(2));
    value = value with { Enabled = true, Radius = 501 };
    Assert.IsFalse(Prepare().CanWrite(2));
    value = value with { Radius = 500 };
    Assert.IsTrue(Prepare().CanWrite(2)); // Strict XZ boundary.
    value = value with { Radius = 501, Permitted = [2] };
    Assert.IsTrue(Prepare().CanWrite(2));
    value = value with { Permitted = [] };
    Assert.IsFalse(Prepare().CanWrite(2));
  }

  [TestMethod]
  public void Membership_ConcurrentNotificationsOnlyResolveOnPreparingThread()
  {
    var index = new WardIndex<int>();
    index.BeginSeed(); index.CompleteSeed();
    Parallel.For(1, 101, id => index.Changed(0, id));
    var thread = Environment.CurrentManagedThreadId;
    var reads = 0;
    var access = index.Prepare(id => {
      Assert.AreEqual(thread, Environment.CurrentManagedThreadId); return true;
    }, _ => { reads++; return Ward(); });
    Assert.IsNotNull(access);
    Assert.AreEqual(100, reads);
    Assert.IsFalse(access.CanWrite(2));
  }

  [TestMethod]
  public void Membership_World820978SeededOnceThen67LiveReadsTotalForTenPeers()
  {
    var index = new WardIndex<int>();
    var seedReads = 0;
    index.BeginSeed();
    for (int id = 1; id <= 820978; id++) { seedReads++; index.Seed(id, id <= 67); }
    index.CompleteSeed();
    var wardReads = 0;
    var resolutions = 0;
    var access = index.Prepare(_ => { resolutions++; return false; }, _ => { wardReads++; return Ward(); })!;
    for (int peer = 2; peer <= 11; peer++) Assert.IsFalse(access.CanWrite(peer));
    Assert.AreEqual(820978, seedReads);
    Assert.AreEqual(67, wardReads);
    Assert.AreEqual(0, resolutions);
    index.Prepare(_ => { resolutions++; return false; }, _ => { wardReads++; return Ward(); });
    Assert.AreEqual(134, wardReads);
    Assert.AreEqual(0, resolutions);
    Console.WriteLine($"Synthetic world IDs={seedReads}; ward members=67; live reads for two table passes={wardReads}; queued-ID resolutions={resolutions}; peers on first pass=10");
  }

  [TestMethod]
  public void Round3_RepeatedAndConcurrentNotificationsCoalesceWithoutAnyTableVisit()
  {
    var index = new WardIndex<int>();
    index.BeginSeed(); index.CompleteSeed();
    for (int n = 0; n < 100000; n++) index.Changed(1, 1);
    Assert.AreEqual(1, index.PendingCount);
    Parallel.For(1, 17, id => { for (int n = 0; n < 1000; n++) index.Changed(id, id); });
    Assert.AreEqual(16, index.PendingCount);
    var resolutions = 0;
    Assert.IsTrue(index.Service(_ => { resolutions++; return false; }));
    Assert.AreEqual(16, resolutions);
    Assert.AreEqual(0, index.PendingCount);
  }

  [TestMethod]
  public void Round3_NoTableServiceMaintainsCurrentMembershipBeforeAuthorizationIsNeeded()
  {
    var index = new WardIndex<int>();
    var world = new Dictionary<int, WardAccess.Snapshot> { [1] = Ward() };
    index.BeginSeed(); index.CompleteSeed();
    index.Changed(0, 1);
    Assert.IsTrue(index.Service(world.ContainsKey));
    Assert.AreEqual(0, index.PendingCount);
    world.Remove(1); world[2] = Ward(creator: 2);
    index.Changed(1, 2);
    Assert.IsTrue(index.Service(world.ContainsKey));
    Assert.AreEqual(0, index.PendingCount);
    Assert.IsTrue(index.Prepare(_ => throw new Exception("No table-driven service should be necessary"), id => world[id])!.CanWrite(2));
  }

  [TestMethod]
  public void Round3_DisabledRecordingIgnoresChangesAndRequiresCompleteCurrentWorldReseed()
  {
    var index = new WardIndex<int>();
    index.BeginSeed(); index.Seed(1, true); index.CompleteSeed();
    index.Changed(1, 2);
    index.Suspend();
    for (int id = 1; id <= 100000; id++) index.Changed(id, id + 1);
    Assert.AreEqual(0, index.PendingCount);
    Assert.IsTrue(index.NeedsSeed);
    Assert.IsFalse(index.Service(_ => throw new Exception()));
    Assert.IsNull(index.Prepare(_ => throw new Exception(), _ => throw new Exception()));
    index.BeginSeed();
    index.Seed(3, true); // Changed wards while disabled are recovered from current world, not old notifications.
    Assert.IsNull(index.Prepare(_ => throw new Exception(), _ => Ward()));
    index.CompleteSeed();
    Assert.IsFalse(index.Prepare(_ => throw new Exception(), id => {
      Assert.AreEqual(3, id); return Ward(distance: 500, radius: 501);
    })!.CanWrite(2));
  }

  [TestMethod]
  public void Round3_AbsentProcessingUniqueIDLimitInvalidatesInsteadOfAuthorizingStaleMembership()
  {
    var index = new WardIndex<int>();
    index.BeginSeed(); index.CompleteSeed();
    for (int id = 1; id <= WardIndex<int>.MaxPendingIDs + 100; id++) index.Changed(0, id);
    Assert.IsTrue(index.NeedsSeed);
    Assert.AreEqual(0, index.PendingCount);
    for (int id = 1; id < 10000; id++) index.Changed(0, id);
    Assert.AreEqual(0, index.PendingCount);
    Assert.IsNull(index.Prepare(_ => true, _ => Ward()));
    index.CompleteSeed(); // Cannot accidentally acknowledge a suspended or interrupted seed.
    Assert.IsTrue(index.NeedsSeed);
    index.BeginSeed(); index.Seed(99999, true); index.CompleteSeed();
    Assert.IsFalse(index.Prepare(_ => throw new Exception(), _ => Ward())!.CanWrite(2));
  }

  [TestMethod]
  public void Round3_NotificationsDuringResolutionAreCoalescedButLatestStateIsResolvedAgainSamePass()
  {
    var index = new WardIndex<int>();
    index.BeginSeed(); index.CompleteSeed();
    index.Changed(0, 1);
    var isWard = true;
    var calls = 0;
    Assert.IsTrue(index.Service(_ => {
      var current = isWard;
      if (++calls == 1)
      {
        var notifications = Task.Run(() => {
          isWard = false;
          for (int n = 0; n < 1000; n++) index.Changed(1, 1);
        });
        Assert.IsTrue(notifications.Wait(TimeSpan.FromSeconds(2)), "Notifications cannot wait for the live membership lookup");
      }
      return current;
    }));
    Assert.AreEqual(2, calls);
    Assert.AreEqual(0, index.PendingCount);
    Assert.IsTrue(index.Prepare(_ => throw new Exception(), _ => throw new Exception("No stale ward may remain"))!.CanWrite(2));
  }

  [TestMethod]
  public void Round3_SuspensionDuringSeedOrSnapshotCannotProduceReadyAccess()
  {
    var index = new WardIndex<int>();
    index.BeginSeed(); index.Seed(1, true); index.Suspend(); index.CompleteSeed();
    Assert.IsTrue(index.NeedsSeed);
    Assert.IsNull(index.Prepare(_ => true, _ => Ward()));
    index.BeginSeed(); index.Seed(1, true); index.CompleteSeed();
    Assert.IsNull(index.Prepare(_ => true, _ => { index.Suspend(); return Ward(); }));
  }

  [TestMethod]
  public void Radius_FreshMarkersAndFloatKeyMatchGameLoadFieldsWithoutCachedHasFields()
  {
    var bools = new Dictionary<string, bool>();
    var floats = new Dictionary<string, float> { ["PrivateArea.m_radius"] = 600 };
    float Read() => WardAccess.ReadRadius("HasFields", "PrivateArea", 10,
      key => bools.GetValueOrDefault(key), (key, fallback) => floats.GetValueOrDefault(key, fallback));
    Assert.AreEqual(10, Read());
    bools["HasFields"] = true;
    Assert.AreEqual(10, Read());
    bools["HasFieldsPrivateArea"] = true;
    Assert.AreEqual(600, Read());
    floats["PrivateArea.m_radius"] = 900;
    Assert.AreEqual(900, Read());
    floats.Clear(); Assert.AreEqual(10, Read());
    floats["PrivateArea.m_radius"] = 600;
    bools["HasFields"] = false; Assert.AreEqual(10, Read());
  }
}
