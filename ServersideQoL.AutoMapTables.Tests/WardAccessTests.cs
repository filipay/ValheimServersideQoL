namespace ServersideQoL.AutoMapTables.Tests;

[TestClass]
public sealed class WardAccessTests
{
  [TestMethod]
  public void NoWards_DoesNotRestrictAccess()
  {
    Assert.IsFalse(WardAccess.HasActiveWard<TestWard>(null, static ward => ward.Enabled));
    Assert.IsFalse(WardAccess.HasActiveWard<TestWard>([], static ward => ward.Enabled));
  }

  [TestMethod]
  public void InactiveWard_DoesNotRestrictAccess()
  {
    var wards = new[] { new TestWard(false) };

    Assert.IsFalse(WardAccess.HasActiveWard(wards, static ward => ward.Enabled));
  }

  [TestMethod]
  public void MultipleInactiveWards_DoNotRestrictAccess()
  {
    var wards = new[] { new TestWard(false), new TestWard(false) };

    Assert.IsFalse(WardAccess.HasActiveWard(wards, static ward => ward.Enabled));
  }

  [TestMethod]
  public void ActiveWard_RestrictsAccess()
  {
    var wards = new[] { new TestWard(true) };

    Assert.IsTrue(WardAccess.HasActiveWard(wards, static ward => ward.Enabled));
  }

  [TestMethod]
  public void MixedActiveAndInactiveWards_RestrictAccess()
  {
    var wards = new[] { new TestWard(false), new TestWard(true), new TestWard(false) };

    Assert.IsTrue(WardAccess.HasActiveWard(wards, static ward => ward.Enabled));
  }

  readonly record struct TestWard(bool Enabled);

  [TestMethod]
  public void OverlappingWards_AnyEnabledPermittedWardAllowsWrite()
  {
    Assert.IsTrue(WardAccess.CanWrite(new[] { (true, false), (true, true) }, w => w.Item1, w => w.Item2));
    Assert.IsFalse(WardAccess.CanWrite(new[] { (true, false), (false, true) }, w => w.Item1, w => w.Item2));
    Assert.IsTrue(WardAccess.CanWrite(new[] { (false, false) }, w => w.Item1, w => w.Item2));
  }

  [TestMethod]
  public void WardBoundary_IsStrictXZRadius()
  {
    Assert.IsTrue(WardAccess.Covers(9.999f, 10));
    Assert.IsFalse(WardAccess.Covers(10, 10));
    Assert.IsFalse(WardAccess.Covers(10.001f, 10));
  }

  [TestMethod]
  public void WriteAccess_UsesCurrentEnabledAndPermissionValuesNotAnOldOverlapUnion()
  {
    var wards = new[] { (Enabled: true, Permitted: true), (Enabled: true, Permitted: false) };
    Assert.IsTrue(WardAccess.CanWrite(wards, w => w.Enabled, w => w.Permitted));
    wards[0] = (false, true);
    Assert.IsFalse(WardAccess.CanWrite(wards, w => w.Enabled, w => w.Permitted));
    wards[1] = (true, true);
    Assert.IsTrue(WardAccess.CanWrite(wards, w => w.Enabled, w => w.Permitted));
    wards[1] = (false, true);
    Assert.IsTrue(WardAccess.CanWrite(wards, w => w.Enabled, w => w.Permitted));
  }

  [TestMethod]
  public void Review_TableBeforeWardRegistrationUsesAllCurrentCoveringObjectsAndTransitions()
  {
    var world = new List<(bool Registered, bool Enabled, bool Permitted, float Distance)>();
    bool CanWrite() => WardAccess.CanWrite(world, w => w.Enabled, w => w.Permitted, w => WardAccess.Covers(w.Distance, 10));
    Assert.IsTrue(CanWrite());
    world.Add((false, true, false, 1)); // World ZDO exists; ward Process has not run.
    world.Add((true, true, true, 10)); // Permitted outside the strict boundary cannot authorize.
    Assert.IsFalse(CanWrite());
    world[0] = (true, true, false, 1);
    Assert.IsFalse(CanWrite());
    world.Add((false, true, true, 9));
    Assert.IsTrue(CanWrite());
    world[2] = (true, false, true, 9);
    Assert.IsFalse(CanWrite());
    world[0] = (true, false, false, 1);
    Assert.IsTrue(CanWrite());
  }
}
