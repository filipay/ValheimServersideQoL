namespace ServersideQoL.AutoMapTables.Tests;

[TestClass]
public sealed class UpdateListTests
{
  [TestMethod]
  public void TryAdd_DuplicatePlayerId_DoesNotThrowOrReplaceExistingEntry()
  {
    var updates = new Dictionary<int, string>();

    Assert.IsTrue(UpdateList.TryAdd(updates, 123, "first peer"));
    Assert.IsFalse(UpdateList.TryAdd(updates, 123, "duplicate peer"));

    Assert.AreEqual(1, updates.Count);
    Assert.AreEqual("first peer", updates[123]);
  }

  [TestMethod]
  public void TryAdd_DifferentPlayerIds_AddsBothEntries()
  {
    var updates = new Dictionary<int, string>();

    Assert.IsTrue(UpdateList.TryAdd(updates, 123, "first peer"));
    Assert.IsTrue(UpdateList.TryAdd(updates, 456, "second peer"));

    Assert.AreEqual(2, updates.Count);
  }
}
