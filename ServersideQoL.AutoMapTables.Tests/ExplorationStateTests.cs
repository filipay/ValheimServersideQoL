namespace ServersideQoL.AutoMapTables.Tests;

[TestClass]
public sealed class ExplorationStateTests
{
  [TestMethod]
  public void Cohort_DirtyCharactersFlushOnceAndCleanCohortDoesNotFlush()
  {
    var state = new ExplorationState(8, 1);
    var snapshots = new List<byte[]>();
    (long, string?, float, float, bool)[] observations = [(1, "First", -2, 0, false), (1, "Duplicate", 0, 0, false), (2, "Second", 2, 0, false)];
    state.SampleCohort(observations, 0, 2, 0, snapshots.Add);
    Assert.AreEqual(1, snapshots.Count);
    var restored = ExplorationState.Deserialize(snapshots[0], 8, 1);
    CollectionAssert.AreEqual(new[] { 34 }, Pixels(restored.GetPending(1)));
    CollectionAssert.AreEqual(new[] { 38 }, Pixels(restored.GetPending(2)));
    Assert.AreEqual("First", restored.GetName(1));
    Assert.AreEqual("Second", restored.GetName(2));
    Assert.AreEqual(0L, restored.Revision);
    Assert.AreEqual(0, Pixels(restored.Published).Length);
    state.SampleCohort(observations, 0, 2, 0, snapshots.Add);
    state.SampleCohort(observations, 2, 2, 0, snapshots.Add);
    Assert.AreEqual(1, snapshots.Count);
  }

  [TestMethod]
  public void Cohort_IndividualDeadlinesSurviveEarlyRepeatAndRevisionPasses()
  {
    var state = new ExplorationState(8, 1);
    var snapshots = new List<byte[]>();
    state.SampleCohort([(1, "First", -2, 0, false)], 0, 2, 0, snapshots.Add);
    Assert.AreEqual(0, state.GetSampleDelay(2, 0));
    state.SampleCohort([(1, "First", 0, 0, false), (2, "Second", 2, 0, false)], 1, 2, 0, snapshots.Add);
    Assert.AreEqual(1, state.GetSampleDelay(1, 1));
    Assert.AreEqual(2, state.GetSampleDelay(2, 1));
    state.SampleCohort([(1, "First", 0, 0, false), (2, "Second", 0, 0, false)], 1.98, 2, 0, snapshots.Add);
    Assert.AreEqual(2, snapshots.Count);
    Assert.AreEqual(0.02f, state.GetSampleDelay(1, 1.98), 0.000001f);
    Assert.AreEqual(1.02f, state.GetSampleDelay(2, 1.98), 0.000001f);
    state.SampleCohort([(1, "First", 0, 0, false), (2, "Second", 0, 0, false)], 2, 2, 0, snapshots.Add);
    Assert.AreEqual(3, snapshots.Count);
    CollectionAssert.AreEqual(new[] { 34, 36 }, Pixels(state.GetPending(1)));
    CollectionAssert.AreEqual(new[] { 38 }, Pixels(state.GetPending(2)));
    Assert.AreEqual(2, state.GetSampleDelay(1, 2));
    Assert.AreEqual(1, state.GetSampleDelay(2, 2));
    state.SampleCohort([(2, "Second", 0, 0, false)], 3, 2, 0, snapshots.Add);
    Assert.AreEqual(4, snapshots.Count);
    CollectionAssert.AreEqual(new[] { 36, 38 }, Pixels(state.GetPending(2)));
  }

  [TestMethod]
  public void Cohort_FinalCharacterLogoutNeedsNoLaterPassToPersist()
  {
    var state = new ExplorationState(8, 1);
    var connected = new List<(long, string?, float, float, bool)> { (7, "Last", 0, 0, false) };
    byte[]? saved = null;
    state.SampleCohort(connected, 0, 2, 0, data => saved = data);
    connected.Clear();
    Assert.IsNotNull(saved);
    // No second cohort call: a normal save can already retain the final character's data.
    var restarted = ExplorationState.Deserialize(saved, 8, 1);
    CollectionAssert.AreEqual(new[] { 36 }, Pixels(restarted.GetPending(7)));
    Assert.AreEqual("Last", restarted.GetName(7));
    Assert.AreEqual(0, Pixels(restarted.Published).Length);
    Assert.AreEqual(0L, restarted.Revision);
  }

  [TestMethod]
  public void Cohort_PartialAcquisitionFailureFlushesCompletedCharactersOnceBeforeUnwinding()
  {
    var state = new ExplorationState(8, 1);
    var snapshots = new List<byte[]>();
    var error = Assert.ThrowsExactly<InvalidOperationException>(() => state.SampleCohort(Observations(), 0, 2, 0, snapshots.Add));
    Assert.AreEqual("third character unavailable", error.Message);
    Assert.AreEqual(1, snapshots.Count);
    var restored = ExplorationState.Deserialize(snapshots[0], 8, 1);
    CollectionAssert.AreEqual(new[] { 34 }, Pixels(restored.GetPending(1)));
    CollectionAssert.AreEqual(new[] { 38 }, Pixels(restored.GetPending(2)));
    Assert.AreEqual(2, state.GetSampleDelay(1, 0));
    Assert.AreEqual(0, state.GetSampleDelay(3, 0));
    Assert.AreEqual(0, Pixels(restored.Published).Length);
    Assert.AreEqual(0L, restored.Revision);

    static IEnumerable<(long, string?, float, float, bool)> Observations()
    {
      yield return (1, "First", -2, 0, false);
      yield return (2, "Second", 2, 0, false);
      throw new InvalidOperationException("third character unavailable");
    }
  }

  [TestMethod]
  public void Cohort_FailedPersistenceRetainsDirtyStateAndCanRetryWithoutAnyCharacters()
  {
    var state = new ExplorationState(8, 1);
    Assert.ThrowsExactly<IOException>(() => state.SampleCohort([(1, "First", 0, 0, false)], 0, 2, 0,
      _ => throw new IOException("write failed")));
    var snapshots = new List<byte[]>();
    state.SampleCohort([], 0, 2, 0, snapshots.Add);
    Assert.AreEqual(1, snapshots.Count);
    var restored = ExplorationState.Deserialize(snapshots[0], 8, 1);
    CollectionAssert.AreEqual(new[] { 36 }, Pixels(restored.GetPending(1)));
    state.SampleCohort([], 0, 2, 0, snapshots.Add);
    Assert.AreEqual(1, snapshots.Count);
  }

  [TestMethod]
  public void Sampling_EarlyRepeatKeepsDeadlineAndSchedulesOnlyRemainingDelay()
  {
    double next = 0;
    Assert.IsTrue(ExplorationState.ScheduleSample(0, 2, ref next, out var delay));
    Assert.AreEqual(2, delay);
    Assert.IsFalse(ExplorationState.ScheduleSample(1.98, 2, ref next, out delay));
    Assert.AreEqual(2, next);
    Assert.AreEqual(0.02f, delay, 0.000001f);
    Assert.IsTrue(ExplorationState.ScheduleSample(2, 2, ref next, out delay));
    Assert.AreEqual(4, next);
    Assert.AreEqual(2, delay);
  }

  [TestMethod]
  public void Sampling_EarlyRevisionCallsDoNotPostponeOrDuplicateSamples()
  {
    double next = 2;
    foreach (var now in new[] { 0.5, 1.0, 1.99 })
    {
      Assert.IsFalse(ExplorationState.ScheduleSample(now, 2, ref next, out var delay));
      Assert.AreEqual(2, next);
      Assert.AreEqual((float)(2 - now), delay, 0.000001f);
    }
    Assert.IsTrue(ExplorationState.ScheduleSample(2, 2, ref next, out var nextDelay));
    Assert.AreEqual(4, next);
    Assert.IsFalse(ExplorationState.ScheduleSample(2, 2, ref next, out nextDelay));
    Assert.AreEqual(4, next);
    Assert.AreEqual(2, nextDelay);
    Assert.IsTrue(ExplorationState.ScheduleSample(5, 2, ref next, out nextDelay));
    Assert.AreEqual(7, next);
    Assert.AreEqual(2, nextDelay);
  }

  [TestMethod]
  public void Sample_UsesGameRoundingCeilingRadiusAndBoundedCircle()
  {
    var state = new ExplorationState(8, 2);
    Assert.IsTrue(state.Sample(1, -7, -7, 0.1f, false));
    // Game rounds half pixels up: (-7 / 2 + 4) becomes pixel 1, not 0.
    CollectionAssert.AreEquivalent(new[] { 1, 8, 9, 10, 17 }, Pixels(state.GetPending(1)));
    Assert.IsFalse(state.Sample(1, -7, -7, 0.1f, false));
    Assert.IsTrue(state.Sample(2, -8, -8, 2, false));
    CollectionAssert.AreEquivalent(new[] { 0, 1, 8 }, Pixels(state.GetPending(2)));
    Assert.IsFalse(state.Sample(3, 1000, -1000, 2, false));
  }

  [TestMethod]
  public void Sample_MatchesFloatPrecisionOfGameRoundToInt()
  {
    var state = new ExplorationState(8, 1);
    state.Sample(1, -1.501f, 0, 0, false);
    // Utils.RoundToInt adds 64000.5f before truncating; this is not Math.Round.
    CollectionAssert.AreEqual(new[] { 35 }, Pixels(state.GetPending(1)));
  }

  [TestMethod]
  public void Sample_ClipsEveryEdgeAndKeepsCornersOutsideTheCircle()
  {
    var state = new ExplorationState(9, 2);
    state.Sample(1, 0, 0, 3, false);
    CollectionAssert.AreEquivalent(new[] { 22, 30, 31, 32, 38, 39, 40, 41, 42, 48, 49, 50, 58 }, Pixels(state.GetPending(1)));
    Assert.AreEqual(11, state.GetPending(1).Length);
    state.Sample(2, 8, 8, 2, false);
    CollectionAssert.AreEquivalent(new[] { 71, 79, 80 }, Pixels(state.GetPending(2)));
    state.Sample(3, -10, -8, 2, false);
    CollectionAssert.AreEquivalent(new[] { 0 }, Pixels(state.GetPending(3)));
    Assert.IsFalse(state.Sample(4, -12, -12, 2, false));
  }

  [TestMethod]
  public void InteriorsAndTeleports_DoNotRevealCorridorsOrPublish()
  {
    var state = new ExplorationState(32, 1);
    Assert.IsFalse(state.Sample(1, 0, 0, 1, true));
    Assert.AreEqual(0, state.GetPending(1).Length);
    state.Sample(1, -10, 0, 1, false);
    state.Sample(1, 10, 0, 1, false);
    Assert.AreEqual(10, Pixels(state.GetPending(1)).Length);
    Assert.IsFalse(Pixels(state.GetPending(1)).Contains(16 * 32 + 16));
    Assert.AreEqual(0, Pixels(state.Published).Length);
    Assert.AreEqual(0L, state.Revision);
  }

  [TestMethod]
  public void Persistence_KeepsOfflineCharactersUnpublishedUntilExplicitEligiblePublication()
  {
    var state = new ExplorationState(8, 1);
    state.Sample(11, -2, 0, 0, false);
    state.Sample(22, 2, 0, 0, false);
    state.RememberName(11, "Builder");
    // Disconnect needs no mutation: serialize all characters, including absent peers.
    var restored = ExplorationState.Deserialize(state.Serialize(), 8, 1);
    CollectionAssert.AreEqual(new[] { 34 }, Pixels(restored.GetPending(11)));
    CollectionAssert.AreEqual(new[] { 38 }, Pixels(restored.GetPending(22)));
    Assert.AreEqual(0, Pixels(restored.Published).Length);
    Assert.AreEqual("Builder", restored.GetName(11));
    Assert.IsNull(restored.GetName(22));
    Assert.IsFalse(restored.Publish(11, false));
    Assert.AreEqual(1, Pixels(restored.GetPending(11)).Length);
    Assert.IsTrue(restored.Publish(11, true));
    Assert.AreEqual(0, restored.GetPending(11).Length);
    Assert.AreEqual(1, Pixels(restored.GetPending(22)).Length);
    CollectionAssert.AreEqual(new[] { 34 }, Pixels(restored.Published));
    Assert.AreEqual(1L, restored.Revision);
    var restarted = ExplorationState.Deserialize(restored.Serialize(), 8, 1);
    Assert.AreEqual(1L, restarted.Revision);
    Assert.AreEqual(1, Pixels(restarted.GetPending(22)).Length);
    Assert.IsFalse(restarted.Publish(11, true));
  }

  [TestMethod]
  public void Publication_IsAnIdempotentUnion()
  {
    var state = new ExplorationState(8, 1);
    state.Sample(1, 0, 0, 0, false);
    state.Sample(2, 0, 0, 0, false);
    Assert.IsTrue(state.Publish(1, true));
    Assert.IsFalse(state.Publish(2, true));
    Assert.AreEqual(0, state.GetPending(2).Length);
    Assert.AreEqual(1L, state.Revision);
    Assert.AreEqual(1, Pixels(state.Published).Length);
    Assert.IsFalse(state.Sample(3, 0, 0, 0, false));
    Assert.AreEqual(0, state.GetPending(3).Length);
  }

  [TestMethod]
  public void Names_AreUpdatedByCharacterAndUnknownValuesDoNotEraseThem()
  {
    var state = new ExplorationState(8, 1);
    Assert.IsFalse(state.RememberName(0, "Invalid"));
    Assert.IsTrue(state.RememberName(1, "First"));
    Assert.IsFalse(state.RememberName(1, "First"));
    Assert.IsFalse(state.RememberName(1, " "));
    Assert.IsTrue(state.RememberName(1, "Renamed"));
    var restored = ExplorationState.Deserialize(state.Serialize(), 8, 1);
    Assert.AreEqual("Renamed", restored.GetName(1));
    Assert.IsNull(restored.GetName(2));
  }

  [TestMethod]
  public void InvalidSettingsAndSamples_AreRejected()
  {
    Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ExplorationState(0, 1));
    Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ExplorationState(8, float.NaN));
    Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ExplorationState(8, 0));
    var state = new ExplorationState(8, 1);
    Assert.IsFalse(state.Sample(0, 0, 0, 1, false));
    Assert.IsFalse(state.Sample(1, float.NaN, 0, 1, false));
    Assert.IsFalse(state.Sample(1, 0, 0, float.PositiveInfinity, false));
    Assert.IsFalse(state.Sample(1, 0, 0, -1, false));
    Assert.IsFalse(state.Sample(1, float.MaxValue, 0, 1, false));
  }

  [TestMethod]
  public void Persistence_RejectsUnsupportedTruncatedMismatchedAndTrailingData()
  {
    var state = new ExplorationState(8, 1);
    state.Sample(1, 0, 0, 1, false);
    var data = state.Serialize();
    Assert.ThrowsExactly<InvalidDataException>(() => ExplorationState.Deserialize(data, 16, 1));
    Assert.ThrowsExactly<InvalidDataException>(() => ExplorationState.Deserialize(data, 8, 2));
    Assert.ThrowsExactly<InvalidDataException>(() => ExplorationState.Deserialize(data[..^1], 8, 1));
    Assert.ThrowsExactly<InvalidDataException>(() => ExplorationState.Deserialize([.. data, 0], 8, 1));
    data[0] = 99;
    Assert.ThrowsExactly<InvalidDataException>(() => ExplorationState.Deserialize(data, 8, 1));
  }

  static int[] Pixels(ReadOnlySpan<byte> packed)
  {
    var pixels = new List<int>();
    for (int i = 0; i < packed.Length * 8; i++)
      if ((packed[i / 8] & (1 << (i % 8))) != 0)
        pixels.Add(i);
    return [.. pixels];
  }
}
