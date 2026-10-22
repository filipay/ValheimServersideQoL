using System.IO;
using System.IO.Compression;
using System.Text;
using System.Diagnostics;

namespace ServersideQoL.AutoMapTables.Tests;

[TestClass]
public sealed class SharedMapDataTests
{
  static readonly SharedMapData.Pin Manual = new(42, "local", 1, 2, 3, 0, true, "Steam_42");
  static SharedMapData.Discovery Ship(float x) => new("ship:1", new(101, "$ship", x, 0, 3, 2, false, "plugin"), SharedMapData.DiscoveryKind.Ship);
  static SharedMapData.Contribution Contribution(long id, params SharedMapData.Discovery[] pins) => new(id, pins, Owner: 100 + id);
  static bool Generated(long owner) => owner >= 100;
  static byte[] Table(params SharedMapData.Pin[] pins) => SharedMapData.Encode(new byte[2], 4, pins);

  [TestMethod]
  public void UpstreamIntegration_ShipBobbingAndSubpixelDriftDoNotRepublishButPixelMovementDoes()
  {
    var state = new ExplorationState(4, 1);
    var ship = Ship(1);
    byte[] table = Table();
    state.Reconcile(table, true, [Contribution(1, ship)], Generated, _ => { }, b => table = b);
    var previous = ship.Pin;
    foreach (var current in new[] { ship.Pin with { Y = 4 }, ship.Pin with { X = 1.5f, Y = 9 }, ship.Pin with { X = 1.99f, Y = -2 } })
    {
      var stable = SharedMapData.StabilizeShipPosition(current, previous, 1);
      Assert.AreEqual(ship.Pin, stable);
      Assert.IsFalse(state.Reconcile(table, true, [Contribution(1, ship with { Pin = stable })], Generated,
        _ => Assert.Fail("Bobbing must not persist a changed snapshot"), _ => Assert.Fail("Bobbing must not rewrite tables")));
      previous = stable;
    }
    var moved = ship.Pin with { X = 2, Y = 7 };
    Assert.AreEqual(moved, SharedMapData.StabilizeShipPosition(moved, previous, 1));
    Assert.IsTrue(state.Reconcile(table, true, [Contribution(1, ship with { Pin = moved })], Generated, _ => { }, b => table = b));
    Assert.AreEqual(moved, SharedMapData.Decode(table, 4).Pins.Single());
    var diagonal = moved with { X = 5, Z = 7 };
    Assert.AreEqual(diagonal, SharedMapData.StabilizeShipPosition(diagonal, moved, 5));
    var renamed = moved with { Name = "new label", Y = 20 };
    Assert.AreEqual(moved with { Name = "new label" }, SharedMapData.StabilizeShipPosition(renamed, moved, 1));
  }

  [TestMethod]
  public void UpstreamIntegration_ProductionKeepsLowestPingDuplicatePeerAndStableCollectedShipPosition()
  {
    var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Production", "AutoMapTablesProcessor.cs"));
    StringAssert.Contains(source, "_updateList.TryAdd(playerID, (peer, playerState))");
    StringAssert.Contains(source, "peer.ZNetPeer.m_rpc.GetTimeSinceLastPing() < _updateList[playerID].Peer.ZNetPeer.m_rpc.GetTimeSinceLastPing()");
    StringAssert.Contains(source, "pin = SharedMapData.StabilizeShipPosition(pin, previous, Minimap.instance.m_pixelSize)");
    StringAssert.Contains(source, "_shipPositions[zdo] = pin;");
    StringAssert.Contains(source, "_shipPositions.Clear();");
    StringAssert.Contains(source, "_shipPositions.Remove(zdo);");
  }

  [TestMethod]
  public void Codec_V3RoundTripMatchesVanillaBinaryFields()
  {
    var mask = new byte[] { 0x81, 0x40 };
    var encoded = SharedMapData.Encode(mask, 4, [Manual]);
    using var zipped = new GZipStream(new MemoryStream(encoded), CompressionMode.Decompress);
    using var reader = new BinaryReader(zipped);
    Assert.AreEqual(3, reader.ReadInt32());
    Assert.AreEqual(16, reader.ReadInt32());
    for (int i = 0; i < 16; i++)
      Assert.AreEqual((mask[i / 8] & (1 << (i % 8))) != 0, reader.ReadBoolean());
    Assert.AreEqual(1, reader.ReadInt32());
    Assert.AreEqual(Manual.Owner, reader.ReadInt64());
    Assert.AreEqual(Manual.Name, reader.ReadString());
    Assert.AreEqual(Manual.X, reader.ReadSingle());
    Assert.AreEqual(Manual.Y, reader.ReadSingle());
    Assert.AreEqual(Manual.Z, reader.ReadSingle());
    Assert.AreEqual(Manual.Type, reader.ReadInt32());
    Assert.AreEqual(Manual.Checked, reader.ReadBoolean());
    Assert.AreEqual(Manual.Author, reader.ReadString());
    Assert.AreEqual(-1, zipped.ReadByte());
    var decoded = SharedMapData.Decode(encoded, 4);
    CollectionAssert.AreEqual(mask, decoded.Explored);
    CollectionAssert.AreEqual(new[] { Manual }, decoded.Pins);
  }

  [TestMethod]
  public void Reconcile_OnlySameEligibleCharacterPublishesAndPersistsBeforeTableWrite()
  {
    var state = new ExplorationState(4, 1);
    state.Sample(1, -1, -1, 0, false);
    state.Sample(2, 1, 1, 0, false);
    var calls = new List<string>();
    byte[]? saved = null;
    byte[]? table = null;
    Assert.IsFalse(state.Reconcile(Table(), false, [Contribution(1, Ship(1))], Generated,
      _ => Assert.Fail("Distant/denied visitor cannot persist"), _ => Assert.Fail("Cannot push distant table")));
    Assert.IsTrue(state.Reconcile(Table(), true, [Contribution(1, Ship(1))], Generated,
      bytes => { saved = bytes; calls.Add("persist"); }, bytes => { table = bytes; calls.Add("table"); }));
    CollectionAssert.AreEqual(new[] { "persist", "table" }, calls);
    var restored = ExplorationState.Deserialize(saved!, 4, 1);
    Assert.IsTrue(restored.GetPending(1).IsEmpty);
    Assert.IsFalse(restored.GetPending(2).IsEmpty);
    Assert.AreEqual(1, SharedMapData.Decode(table!, 4).Pins.Length);
  }

  [TestMethod]
  public void Reconcile_RefreshesOtherTableWithoutContributionAndRepairsStaleClientBlob()
  {
    var state = new ExplorationState(4, 1);
    state.Sample(1, -1, -1, 0, false);
    byte[]? saved = null;
    byte[]? first = null;
    state.Reconcile(Table(), true, [Contribution(1, Ship(1))], Generated, b => saved = b, b => first = b);
    state = ExplorationState.Deserialize(saved!, 4, 1);
    var stale = Table(Manual);
    byte[]? repaired = null;
    Assert.IsTrue(state.Reconcile(stale, true, [], Generated, _ => Assert.Fail("Already durable"), b => repaired = b));
    var decoded = SharedMapData.Decode(repaired!, 4);
    Assert.IsTrue(decoded.Pins.Contains(Manual));
    Assert.IsTrue(decoded.Pins.Contains(Ship(1).Pin));
    CollectionAssert.AreEqual(SharedMapData.Decode(first!, 4).Explored, decoded.Explored);
    Assert.IsFalse(state.Reconcile(repaired, true, [], Generated, _ => Assert.Fail("Idempotent"), _ => Assert.Fail("Idempotent")));
    Assert.IsFalse(state.Reconcile(stale, false, [], Generated, _ => Assert.Fail(), _ => Assert.Fail()));
    Assert.IsTrue(state.Reconcile(stale, true, [], Generated, _ => Assert.Fail(), _ => { }));
  }

  [TestMethod]
  public void Generated_ReplacementMovesShipAndDestroyedRemovalSurvivesRestartWithoutManualResurrection()
  {
    var state = new ExplorationState(4, 1);
    byte[] table = Table(Manual);
    byte[]? saved = null;
    state.Reconcile(table, true, [Contribution(1, Ship(1))], Generated, b => saved = b, b => table = b);
    state.Reconcile(table, true, [Contribution(1, Ship(9))], Generated, b => saved = b, b => table = b);
    var pins = SharedMapData.Decode(table, 4).Pins;
    CollectionAssert.AreEqual(new[] { Manual, Ship(9).Pin }, pins);
    Assert.IsTrue(state.RemoveGenerated("ship:1"));
    state.Flush(b => saved = b);
    state = ExplorationState.Deserialize(saved!, 4, 1);
    state.Reconcile(table, true, [], Generated, _ => Assert.Fail(), b => table = b);
    CollectionAssert.AreEqual(new[] { Manual }, SharedMapData.Decode(table, 4).Pins);
    // A later vanilla removal of a manual pin is table-local, never resurrected globally.
    Assert.IsFalse(state.Reconcile(Table(), true, [], Generated, _ => Assert.Fail(), _ => Assert.Fail()));
  }

  [TestMethod]
  public void Publication_AllOverlapPendingRemovalIsPersistedEvenWhenPublishReturnsFalse()
  {
    var state = new ExplorationState(4, 1);
    state.Sample(1, 0, 0, 0, false);
    state.Sample(2, 0, 0, 0, false);
    byte[] table = Table();
    state.Reconcile(table, true, [Contribution(1)], Generated, _ => { }, b => table = b);
    byte[]? saved = null;
    state.Reconcile(table, true, [Contribution(2)], Generated, b => saved = b, _ => Assert.Fail("No table change"));
    Assert.IsNotNull(saved);
    Assert.IsTrue(ExplorationState.Deserialize(saved, 4, 1).GetPending(2).IsEmpty);
  }

  [TestMethod]
  public void PersistenceFailure_RollsBackPendingAndGeneratedButTableFailureKeepsDurablePublication()
  {
    var state = new ExplorationState(4, 1);
    state.Sample(1, 0, 0, 0, false);
    var before = state.Serialize();
    Assert.ThrowsExactly<IOException>(() => state.Reconcile(Table(), true, [Contribution(1, Ship(1))], Generated,
      _ => throw new IOException("storage"), _ => Assert.Fail("Must not write")));
    CollectionAssert.AreEqual(before, state.Serialize());
    byte[]? saved = null;
    Assert.ThrowsExactly<IOException>(() => state.Reconcile(Table(), true, [Contribution(1, Ship(1))], Generated,
      b => saved = b, _ => throw new IOException("table")));
    Assert.IsTrue(state.GetPending(1).IsEmpty);
    var restored = ExplorationState.Deserialize(saved!, 4, 1);
    Assert.IsTrue(restored.Reconcile(Table(), true, [], Generated, _ => Assert.Fail(), _ => { }));
  }

  [TestMethod]
  public void Codec_RejectsUntrustedVersionGeometryCountsStringsPositionsAndExpansionBeforeMutation()
  {
    var invalid = new List<byte[]> {
      Raw(w => { w.Write(99); }),
      Raw(w => { w.Write(3); w.Write(int.MaxValue); }),
      Raw(w => Header(w, -1)),
      Raw(w => Header(w, int.MaxValue)),
      Raw(w => { Header(w, 1); w.Write(42L); w.Write(new string('a', 4097)); }),
      Raw(w => WritePin(w, float.NaN, 0, 0)),
      Raw(w => WritePin(w, 0, 99, 0)),
      Raw(w => WritePin(w, 0, 0, 2)),
      Raw(w => { Header(w, 0); w.Write(1); }),
      Raw(w => { Header(w, 0); w.Write(new byte[SharedMapData.MaxPinBytes + 1]); }),
      new byte[] { 1, 2, 3 }
    };
    foreach (var payload in invalid)
    {
      var state = new ExplorationState(4, 1);
      state.Sample(1, 0, 0, 0, false);
      var before = state.Serialize();
      Assert.ThrowsExactly<InvalidDataException>(() => state.Reconcile(payload, true, [Contribution(1)], Generated,
        _ => Assert.Fail("Invalid input must not persist"), _ => Assert.Fail("Invalid input must not overwrite")));
      CollectionAssert.AreEqual(before, state.Serialize());
    }
  }

  [TestMethod]
  public void TableExploration_SeedsPublishedUnionButNeverGlobalizesLocalManualPins()
  {
    var state = new ExplorationState(4, 1);
    var original = SharedMapData.Encode([1, 0], 4, [Manual]);
    byte[]? saved = null;
    Assert.IsFalse(state.Reconcile(original, true, [], Generated, b => saved = b, _ => Assert.Fail()));
    var restored = ExplorationState.Deserialize(saved!, 4, 1);
    byte[]? other = null;
    restored.Reconcile(Table(), true, [], Generated, _ => Assert.Fail(), b => other = b);
    CollectionAssert.AreEqual(new byte[] { 1, 0 }, SharedMapData.Decode(other!, 4).Explored);
    Assert.AreEqual(0, SharedMapData.Decode(other!, 4).Pins.Length);
  }

  [TestMethod]
  public void Publication_MultipleContributorsUseOneSnapshotAndBadGeneratedFieldsRollbackAll()
  {
    var state = new ExplorationState(4, 1);
    state.Sample(1, -1, -1, 0, false);
    state.Sample(2, 1, 1, 0, false);
    var before = state.Serialize();
    var invalid = new SharedMapData.Discovery("bad", Ship(1).Pin with { Z = float.PositiveInfinity });
    Assert.ThrowsExactly<InvalidDataException>(() => state.Reconcile(Table(), true,
      [Contribution(1, Ship(1)), Contribution(2, invalid)], Generated, _ => Assert.Fail(), _ => Assert.Fail()));
    CollectionAssert.AreEqual(before, state.Serialize());
    var flushes = 0;
    state.Reconcile(Table(), true, [Contribution(1, Ship(1)), Contribution(2)], Generated,
      bytes => {
        flushes++;
        var restored = ExplorationState.Deserialize(bytes, 4, 1);
        Assert.IsTrue(restored.GetPending(1).IsEmpty);
        Assert.IsTrue(restored.GetPending(2).IsEmpty);
      }, _ => { });
    Assert.AreEqual(1, flushes);
    state.SampleCohort([], 1, 2, 0, _ => Assert.Fail("Publication already acknowledged this snapshot"));
  }

  [TestMethod]
  public void Persistence_FormatOneMigratesWithoutPublishingAndPrunesDestroyedOfflineSources()
  {
    using var stream = new MemoryStream();
    using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
    {
      writer.Write(1); writer.Write(4); writer.Write(1f); writer.Write(0L);
      writer.Write(new byte[2]);
      writer.Write(1); writer.Write(1L); writer.Write(new byte[] { 1, 0 });
      writer.Write(1); writer.Write(1L); writer.Write("Builder");
    }
    var state = ExplorationState.Deserialize(stream.ToArray(), 4, 1);
    Assert.AreEqual("Builder", state.GetName(1));
    CollectionAssert.AreEqual(new byte[] { 1, 0 }, state.GetPending(1).ToArray());
    CollectionAssert.AreEqual(new byte[2], state.Published.ToArray());
    byte[]? saved = null;
    state.Reconcile(Table(), true, [Contribution(1, Ship(1))], Generated, b => saved = b, _ => { });
    state = ExplorationState.Deserialize(saved!, 4, 1);
    Assert.IsTrue(state.PruneGenerated(_ => false));
    state.Flush(b => saved = b);
    state = ExplorationState.Deserialize(saved!, 4, 1);
    Assert.IsFalse(state.HasGenerated);
    Assert.IsFalse(state.PruneGenerated(_ => false));
  }

  [TestMethod]
  public void Codec_RejectsTruncationInvalidUtf8AndOversizedAuthor()
  {
    var bad = new[] {
      Raw(w => { Header(w, 1); w.Write(42L); w.Write((byte)2); w.Write(new byte[] { 0xc3, 0x28 }); w.Write(new byte[32]); }),
      Raw(w => { Header(w, 1); w.Write(42L); w.Write(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x7f }); w.Write(new byte[32]); }),
      Raw(w => { Header(w, 1); w.Write(42L); w.Write("name"); w.Write(new byte[17]); w.Write(new string('a', 1025)); }),
      Raw(w => { Header(w, 1); w.Write(42L); w.Write("name"); }),
      Raw(w => { w.Write(3); w.Write(16); w.Write((byte)2); w.Write(new byte[15]); w.Write(0); })
    };
    foreach (var bytes in bad)
      Assert.ThrowsExactly<InvalidDataException>(() => SharedMapData.Decode(bytes, 4));
    Assert.ThrowsExactly<InvalidDataException>(() => SharedMapData.Decode(null, int.MaxValue));
  }

  [TestMethod]
  public void Review_LegacyPinsStayLocalUntilCompleteAuthoritativeOwnerSnapshotThenNeverResurrect()
  {
    var state = new ExplorationState(4, 1);
    var legacy = Table(Manual, Ship(1).Pin);
    Assert.IsFalse(state.Reconcile(legacy, true, [Contribution(1) with { Complete = false }], Generated,
      _ => Assert.Fail(), _ => Assert.Fail("Incomplete legacy owner cannot authorize deletion")));
    Assert.IsFalse(state.Reconcile(legacy, true, [Contribution(2)], Generated, _ => { }, _ => Assert.Fail("Absent owner's legacy remains local")));
    Assert.IsFalse(state.Reconcile(Table(), true, [], Generated, _ => Assert.Fail(), _ => Assert.Fail("Legacy must not globalize")));
    byte[]? table = null;
    state.Reconcile(legacy, true, [Contribution(1, Ship(9))], Generated, _ => { }, b => table = b);
    CollectionAssert.AreEqual(new[] { Manual, Ship(9).Pin }, SharedMapData.Decode(table!, 4).Pins);
    state.RemoveGenerated("ship:1");
    byte[]? saved = null;
    state.Flush(b => saved = b);
    state = ExplorationState.Deserialize(saved!, 4, 1);
    state.Reconcile(legacy, true, [], Generated, _ => Assert.Fail(), b => table = b);
    CollectionAssert.AreEqual(new[] { Manual }, SharedMapData.Decode(table!, 4).Pins);
    var emptyOwner = new ExplorationState(4, 1);
    emptyOwner.Reconcile(legacy, true, [Contribution(1)], Generated, _ => { }, b => table = b);
    CollectionAssert.AreEqual(new[] { Manual }, SharedMapData.Decode(table!, 4).Pins);
  }

  [TestMethod]
  public void Review_IncompleteDungeonSnapshotKeepsPendingAndPublishedPinsUntilStationaryRetryCompletes()
  {
    var state = new ExplorationState(4, 1);
    var dungeon = Ship(1) with { Source = "dungeon:1", Kind = SharedMapData.DiscoveryKind.Dungeon };
    var cursor = new SharedMapData.UpdateCursor();
    byte[] table = Table();
    state.Reconcile(table, true, [Contribution(1, dungeon)], Generated, _ => { }, b => table = b, cursor: cursor);
    state.Sample(1, 0, 0, 0, false);
    state.Flush(_ => { });
    var before = state.Serialize();
    var unresolved = Contribution(1) with { Complete = false };
    state.Reconcile(table, true, [unresolved], Generated, _ => Assert.Fail("Cannot consume unresolved owner's pending"),
      _ => Assert.Fail("Cannot delete retained dungeon"), cursor: cursor);
    CollectionAssert.AreEqual(before, state.Serialize());
    Assert.IsFalse(state.Reconcile([1, 2, 3], true, [unresolved], Generated, _ => Assert.Fail(), _ => Assert.Fail(), cursor: cursor));
    var ready = dungeon with { Pin = dungeon.Pin with { Name = "ready" } };
    state.Reconcile(table, true, [Contribution(1, ready)], Generated, _ => { }, b => table = b, cursor: cursor);
    Assert.IsTrue(state.GetPending(1).IsEmpty);
    Assert.AreEqual("ready", SharedMapData.Decode(table, 4).Pins.Single().Name);
  }

  [TestMethod]
  public void Review_OfflineRetainedDiscoveriesHonorCurrentTargetsAndTypesWithoutDestroyingOriginals()
  {
    var state = new ExplorationState(4, 1);
    var ore = Ship(1) with { Source = "ore:1", Kind = SharedMapData.DiscoveryKind.Ore, PrefabHash = 123 };
    byte[]? saved = null;
    state.Reconcile(Table(), true, [Contribution(1, ore)], Generated, b => saved = b, _ => { });
    state = ExplorationState.Deserialize(saved!, 4, 1);
    Assert.IsFalse(state.Reconcile(Table(), true, [], Generated, _ => Assert.Fail(), _ => Assert.Fail("DiscoveringPlayer only"),
      project: d => d.Kind == SharedMapData.DiscoveryKind.Ore ? null : d.Pin));
    byte[]? table = null;
    state.Reconcile(Table(), true, [], Generated, _ => Assert.Fail(), b => table = b,
      project: d => d.PrefabHash == 123 ? d.Pin with { Type = 6 } : d.Pin);
    Assert.AreEqual(6, SharedMapData.Decode(table!, 4).Pins.Single().Type);
    state.Reconcile(Table(), true, [], Generated, _ => Assert.Fail(), b => table = b);
    Assert.AreEqual(ore.Pin, SharedMapData.Decode(table!, 4).Pins.Single());
  }

  [TestMethod]
  public void Review_IdleAndDisabledSnapshotsSkipDecodeButMovementTagsConfigAndRevisionStillRefresh()
  {
    var state = new ExplorationState(4, 1);
    var cursor = new SharedMapData.UpdateCursor();
    var current = Contribution(1, Ship(1));
    byte[] table = Table();
    state.Reconcile(table, true, [current], Generated, _ => { }, b => table = b, cursor: cursor, dataRevision: 1);
    // The server revision token is trusted; unchanged validated payloads need no second decode.
    var malformed = new byte[] { 1, 2, 3 };
    Assert.IsFalse(state.Reconcile(malformed, true, [current], Generated, _ => Assert.Fail(), _ => Assert.Fail(), cursor: cursor, dataRevision: 1));
    current = Contribution(1, Ship(7) with { Pin = Ship(7).Pin with { Name = "renamed tag" } });
    state.Reconcile(table, true, [current], Generated, _ => { }, b => table = b, cursor: cursor, dataRevision: 1);
    Assert.AreEqual("renamed tag", SharedMapData.Decode(table, 4).Pins.Single().Name);
    state.Reconcile(table, true, [current], Generated, _ => Assert.Fail(), b => table = b, project: _ => null, cursor: cursor, dataRevision: 1);
    Assert.AreEqual(0, SharedMapData.Decode(table, 4).Pins.Length);
    current = current with { Discoveries = [Ship(20)] };
    Assert.IsFalse(state.Reconcile(malformed, true, [current], Generated, _ => Assert.Fail(), _ => Assert.Fail(), project: _ => null, cursor: cursor, dataRevision: 1));
    Assert.ThrowsExactly<InvalidDataException>(() => state.Reconcile(malformed, true, [current], Generated,
      _ => Assert.Fail(), _ => Assert.Fail(), project: _ => null, cursor: cursor, dataRevision: 2));
    state.Sample(1, 0, 0, 0, false);
    Assert.ThrowsExactly<InvalidDataException>(() => state.Reconcile(malformed, true, [current], Generated,
      _ => Assert.Fail(), _ => Assert.Fail(), project: _ => null, cursor: cursor, dataRevision: 1));
    Assert.IsFalse(state.GetPending(1).IsEmpty);
    state.Publish(1, true);
    Assert.ThrowsExactly<InvalidDataException>(() => state.Reconcile(malformed, true, [current], Generated,
      _ => Assert.Fail(), _ => Assert.Fail(), project: _ => null, cursor: cursor, dataRevision: 1));
  }

  [TestMethod]
  public void Review_FormatTwoMetadataMigrationKeepsOriginalPinAndAppliesCurrentOfflinePolicy()
  {
    using var stream = new MemoryStream();
    using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
    {
      writer.Write(2); writer.Write(4); writer.Write(1f); writer.Write(1L); writer.Write(new byte[2]);
      writer.Write(0); writer.Write(0); // pending/names
      writer.Write(1); writer.Write(1L); writer.Write(1); writer.Write("ore:1");
      SharedMapData.WritePin(writer, Ship(1).Pin);
    }
    var state = ExplorationState.Deserialize(stream.ToArray(), 4, 1);
    state.ResolveGeneratedMetadata(d => d with { Kind = SharedMapData.DiscoveryKind.Ore, PrefabHash = 123 });
    byte[]? saved = null;
    state.Flush(b => saved = b);
    state = ExplorationState.Deserialize(saved!, 4, 1);
    var stale = Table(Manual, Ship(1).Pin);
    byte[]? filtered = null;
    state.Reconcile(stale, true, [], Generated, _ => Assert.Fail(), b => filtered = b,
      project: d => d.Kind == SharedMapData.DiscoveryKind.Ore && d.PrefabHash == 123 ? null : d.Pin);
    CollectionAssert.AreEqual(new[] { Manual }, SharedMapData.Decode(filtered!, 4).Pins);
    state.Reconcile(Table(), true, [], Generated, _ => Assert.Fail(), b => filtered = b);
    CollectionAssert.AreEqual(new[] { Ship(1).Pin }, SharedMapData.Decode(filtered!, 4).Pins);
  }

  [TestMethod]
  public void Round2_FilteredRetainedOverLimitRollsBackAndKeepsPendingAcrossRestart()
  {
    var state = new ExplorationState(4, 1);
    state.Sample(1, -1, -1, 0, false);
    state.Sample(2, 1, 1, 0, false);
    SharedMapData.Discovery[] Ores(long owner, int count) => Enumerable.Range(0, count)
      .Select(i => new SharedMapData.Discovery($"ore:{owner}:{i}", Ship(1).Pin with { Owner = owner }, SharedMapData.DiscoveryKind.Ore)).ToArray();
    byte[]? saved = null;
    byte[] table = Table();
    state.Reconcile(table, true, [Contribution(1, Ores(101, 20000))], Generated,
      b => saved = b, b => table = b, project: _ => null);
    Assert.AreEqual(0, SharedMapData.Decode(table, 4).Pins.Length);
    var before = state.Serialize();
    Assert.ThrowsExactly<InvalidDataException>(() => state.Reconcile(table, true,
      [Contribution(2, Ores(102, 20000))], Generated, _ => Assert.Fail("Cannot persist an unreadable retained union"),
      _ => Assert.Fail(), project: _ => null));
    CollectionAssert.AreEqual(before, state.Serialize());
    var restarted = ExplorationState.Deserialize(saved!, 4, 1);
    CollectionAssert.AreEqual(before, restarted.Serialize());
    Assert.IsFalse(restarted.GetPending(2).IsEmpty);
    // Exactly the aggregate cap is accepted, regardless of emission filtering.
    state.Reconcile(table, true, [Contribution(2, Ores(102, SharedMapData.MaxPins - 20000))], Generated,
      b => saved = b, b => table = b, project: _ => null);
    Assert.AreEqual(0, SharedMapData.Decode(table, 4).Pins.Length);
    CollectionAssert.AreEqual(saved!, ExplorationState.Deserialize(saved!, 4, 1).Serialize());
  }

  [TestMethod]
  public void Round2_SerializeRejectsOtherValuesItsReaderCannotRestore()
  {
    foreach (var invalid in new[] {
      Ship(1) with { Source = new string('a', 129), Kind = SharedMapData.DiscoveryKind.Unknown },
      Ship(1) with { Source = "", Kind = SharedMapData.DiscoveryKind.Unknown },
      Ship(1) with { Kind = (SharedMapData.DiscoveryKind)99 }
    })
    {
      var state = new ExplorationState(4, 1);
      state.Reconcile(Table(), true, [Contribution(1, Ship(1) with { Kind = SharedMapData.DiscoveryKind.Unknown })],
        Generated, _ => { }, _ => { });
      state.ResolveGeneratedMetadata(_ => invalid);
      Assert.ThrowsExactly<InvalidDataException>(() => state.Serialize());
    }
    var revisionState = new ExplorationState(4, 1);
    var bytes = revisionState.Serialize();
    BitConverter.GetBytes(long.MaxValue).CopyTo(bytes, 12);
    revisionState = ExplorationState.Deserialize(bytes, 4, 1);
    revisionState.Sample(1, 0, 0, 0, false);
    revisionState.Publish(1, true);
    Assert.ThrowsExactly<InvalidDataException>(() => revisionState.Serialize());
  }

  [TestMethod]
  public void Round2_SuccessfulSnapshotsRoundTripForEveryMutationAndCategory()
  {
    var state = new ExplorationState(4, 1);
    void RoundTrip()
    {
      var bytes = state.Serialize();
      CollectionAssert.AreEqual(bytes, ExplorationState.Deserialize(bytes, 4, 1).Serialize());
    }
    RoundTrip();
    state.RememberName(1, "Builder");
    state.Sample(1, 0, 0, 0, false);
    RoundTrip();
    foreach (var kind in Enum.GetValues<SharedMapData.DiscoveryKind>())
    {
      state.Reconcile(Table(), true, [Contribution(1, Ship(1) with { Kind = kind, PrefabHash = 123 })], Generated,
        bytes => CollectionAssert.AreEqual(bytes, ExplorationState.Deserialize(bytes, 4, 1).Serialize()), _ => { }, project: _ => null);
      RoundTrip();
    }
    state.RemoveGenerated("ship:1");
    RoundTrip();
    state.Reconcile(Table(), true, [Contribution(2)], Generated, _ => { }, _ => { }, project: _ => null);
    RoundTrip();
  }

  [TestMethod]
  public void Review_ProductionSizeEncodingIsBufferedAndRoundTrips()
  {
    var mask = new byte[2048 * 2048 / 8];
    new Random(42).NextBytes(mask);
    byte[] Legacy()
    {
      using var stream = new MemoryStream();
      using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
      using (var writer = new BinaryWriter(gzip))
      {
        writer.Write(3); writer.Write(2048 * 2048);
        for (int i = 0; i < 2048 * 2048; i++)
          writer.Write((mask[i / 8] & (1 << (i % 8))) != 0);
        writer.Write(0);
      }
      return stream.ToArray();
    }
    SharedMapData.Encode(mask, 2048, []);
    Legacy();
    var timer = Stopwatch.StartNew();
    var encoded = SharedMapData.Encode(mask, 2048, []);
    var buffered = timer.Elapsed.TotalMilliseconds;
    timer.Restart();
    Legacy();
    var legacy = timer.Elapsed.TotalMilliseconds;
    Console.WriteLine($"2048² actual Encode: {buffered:F2} ms; unbuffered baseline: {legacy:F2} ms");
    Assert.IsLessThan(legacy * 0.75, buffered, "Avoid millions of direct gzip writes");
    CollectionAssert.AreEqual(mask, SharedMapData.Decode(encoded, 2048).Explored);
  }

  [TestMethod]
  public void Codec_RejectsMissingGzipFooterOrCorruptChecksum()
  {
    var valid = Table(Manual);
    Assert.ThrowsExactly<InvalidDataException>(() => SharedMapData.Decode(valid[..^8], 4));
    var badChecksum = valid.ToArray();
    badChecksum[^8] ^= 1;
    Assert.ThrowsExactly<InvalidDataException>(() => SharedMapData.Decode(badChecksum, 4));
    var partialFooter = valid[..^8].Concat(valid[^4..]).ToArray();
    Assert.ThrowsExactly<InvalidDataException>(() => SharedMapData.Decode(partialFooter, 4));
  }

  static byte[] Raw(Action<BinaryWriter> write)
  {
    using var result = new MemoryStream();
    using (var gzip = new GZipStream(result, CompressionLevel.Fastest, leaveOpen: true))
    using (var writer = new BinaryWriter(gzip, Encoding.UTF8, leaveOpen: true))
      write(writer);
    return result.ToArray();
  }

  static void Header(BinaryWriter writer, int pins)
  {
    writer.Write(3);
    writer.Write(16);
    writer.Write(new byte[16]);
    writer.Write(pins);
  }

  static void WritePin(BinaryWriter writer, float x, int type, byte check)
  {
    Header(writer, 1);
    writer.Write(42L);
    writer.Write("pin");
    writer.Write(x); writer.Write(0f); writer.Write(0f);
    writer.Write(type); writer.Write(check); writer.Write("author");
  }
}
