namespace ServersideQoL.AutoMapTables.Tests;

[TestClass]
public sealed class ShipLabelTests
{
  [TestMethod]
  public void KnownBuilder_AppendsNameToOrdinaryShipLabel()
  {
    var state = new ExplorationState(4, 1);
    state.RememberName(11, "Builder");
    Assert.AreEqual("$ship_karve (Builder)", ShipLabel.Format("$ship_karve", state.GetName(11)));
  }

  [TestMethod]
  public void OfflineBuilder_UsesLastKnownNameAfterRestart()
  {
    var state = new ExplorationState(4, 1);
    state.RememberName(11, "Offline builder");
    state = ExplorationState.Deserialize(state.Serialize(), 4, 1);
    Assert.AreEqual("$ship_karve (Offline builder)", ShipLabel.Format("$ship_karve", state.GetName(11)));
  }

  [TestMethod]
  public void UnknownBuilder_KeepsOrdinaryLabelUntilNameIsKnown()
  {
    var state = new ExplorationState(4, 1);
    Assert.AreEqual("$ship_karve", ShipLabel.Format("$ship_karve", state.GetName(11)));
    Assert.AreEqual("$ship_karve", ShipLabel.Format("$ship_karve", " "));
    state.RememberName(11, "Now known");
    Assert.AreEqual("$ship_karve (Now known)", ShipLabel.Format("$ship_karve", state.GetName(11)));
  }

  [TestMethod]
  public void RetainedShip_UsesOfflineNameAndRenamesAtNextEligibleReconciliationWithoutChangingOwner()
  {
    var state = new ExplorationState(4, 1);
    const long creator = 11;
    const long encodedOwner = 111;
    var ship = new SharedMapData.Discovery("ship:1", new(encodedOwner, "$ship_karve", 1, 2, 3, 2, false, "plugin"), SharedMapData.DiscoveryKind.Ship);
    byte[]? saved = null;
    byte[] table = SharedMapData.Encode(new byte[2], 4, []);
    state.Reconcile(table, true, [new(creator, [ship], Owner: encodedOwner)], IsGenerated, b => saved = b, b => table = b);
    state.RememberName(creator, "Offline builder");
    state.Flush(b => saved = b);
    state = ExplorationState.Deserialize(saved!, 4, 1);
    var cursor = new SharedMapData.UpdateCursor();
    SharedMapData.Pin? Project(SharedMapData.Discovery discovery) => discovery.Pin with
    {
      Name = ShipLabel.Format(discovery.Pin.Name, state.GetName(creator))
    };
    Assert.IsTrue(state.Reconcile(table, true, [], IsGenerated, _ => Assert.Fail("Already persisted"), b => table = b, Project, cursor));
    Assert.AreEqual(ship.Pin with { Name = "$ship_karve (Offline builder)" }, SharedMapData.Decode(table, 4).Pins.Single());
    state.RememberName(creator, "Renamed");
    var oldTable = table;
    Assert.IsFalse(state.Reconcile(table, false, [], IsGenerated, _ => Assert.Fail(), _ => Assert.Fail(), Project, cursor));
    CollectionAssert.AreEqual(oldTable, table);
    Assert.IsTrue(state.Reconcile(table, true, [], IsGenerated, b => saved = b, b => table = b, Project, cursor));
    Assert.AreEqual(ship.Pin with { Name = "$ship_karve (Renamed)" }, SharedMapData.Decode(table, 4).Pins.Single());
    state = ExplorationState.Deserialize(saved!, 4, 1);
    byte[] otherTable = SharedMapData.Encode(new byte[2], 4, []);
    state.Reconcile(otherTable, true, [], IsGenerated, _ => Assert.Fail(), b => otherTable = b, Project);
    Assert.AreEqual(ship.Pin with { Name = "$ship_karve (Renamed)" }, SharedMapData.Decode(otherTable, 4).Pins.Single());

    static bool IsGenerated(long owner) => owner == encodedOwner;
  }

  [TestMethod]
  public void ProductionWiring_ShipCreatorNotSailorOrNetworkOwnerSuppliesEncodedIdentityAndNameLookup()
  {
    // Linked-source tests cannot instantiate Unity; also guard the actual production ID wiring.
    var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Production", "AutoMapTablesProcessor.cs"));
    StringAssert.Contains(source, "GetOrAddPlayerState(zdo.Vars.GetCreator()).Ships.Add(zdo)");
    var shipBranch = Section("else if (prefabInfo.Ship is not null)", "else if (prefabInfo is { MineRock5:");
    StringAssert.Contains(shipBranch, "var playerID = zdo.Vars.GetCreator();");
    StringAssert.Contains(shipBranch, "var state = GetOrAddPlayerState(playerID);");
    StringAssert.Contains(shipBranch, "state.Ships.Add(zdo)");
    Assert.IsFalse(shipBranch.Contains("GetOwner", StringComparison.Ordinal));
    var collect = Section("SharedMapData.Contribution CollectDiscoveries", "void OnMapTableDestroyed");
    StringAssert.Contains(collect, "new SharedMapData.Pin(playerState.PlayerID.AsModPlayerID().Value,");
    StringAssert.Contains(collect, "foreach (var zdo in playerState.Ships)");
    var emit = Section("SharedMapData.Pin? EmitDiscovery", "SharedMapData.Contribution CollectDiscoveries");
    StringAssert.Contains(emit, "new PlayerID(pin.Owner).IsModPlayerID(out PlayerID builderID)");
    StringAssert.Contains(emit, "ShipLabel.Format(pin.Name, _exploration?.GetName(builderID.Value))");
    Assert.IsFalse(emit.Contains("GetOwner", StringComparison.Ordinal));

    string Section(string start, string end)
    {
      var first = source.IndexOf(start, StringComparison.Ordinal);
      Assert.IsGreaterThanOrEqualTo(0, first);
      var last = source.IndexOf(end, first, StringComparison.Ordinal);
      Assert.IsGreaterThan(first, last);
      return source[first..last];
    }
  }
}
