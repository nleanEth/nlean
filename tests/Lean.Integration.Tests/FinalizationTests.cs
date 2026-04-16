using NUnit.Framework;

namespace Lean.Integration.Tests;

[TestFixture]
[Category("Integration")]
[NonParallelizable]
public class FinalizationTests
{
    // Port ranges: each 4-node test needs base..base+203 (QUIC +0, Metrics +100, API +200).
    // Space base ports 300 apart to avoid overlap between tests.
    private static readonly TimeSpan FinalizationTimeout = ResolveFinalizationTimeout();

    private static TimeSpan ResolveFinalizationTimeout()
    {
        var secondsPerSlot = 2;
        var overrideValue = Environment.GetEnvironmentVariable("NLEAN_INTEG_SECONDS_PER_SLOT");
        if (int.TryParse(overrideValue, out var parsed) && parsed > 0)
        {
            secondsPerSlot = parsed;
        }

        // Baseline is 15 minutes at 2s/slot. Scale linearly for slower slots.
        // leanMultisig 2eb4b9d made aggregated-signature verification ~1.7-2x slower
        // than fd88140, so the prior 10-minute baseline no longer leaves enough
        // headroom on CI (especially for multi-phase tests like CatchupAfterRestart
        // that call WaitForFinalization three times).
        var minutes = Math.Max(15, (int)Math.Ceiling(15.0 * secondsPerSlot / 2.0));
        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>
    /// Pre-warm the per-validator XMSS key cache up to the worst-case count
    /// needed by any test in this fixture (TwoSubnets uses 4 nodes × 2
    /// validators = 8 validators). On a fresh runner the first test to see a
    /// missing key would pay ~15-20 minutes of XMSS generation, leaving only
    /// a few minutes for the actual finalization scenario. Amortizing key gen
    /// here up-front keeps individual test budgets within the 30-minute
    /// FinalizationTimeout wall.
    ///
    /// The fixture constructor only writes files (keys → shared cache at
    /// ~/.cache/nlean-integ-keys, config → its own temp RootDir) and does
    /// not bind sockets or spawn processes, so this is safe even though each
    /// test later builds its own fixture with its own port range.
    /// </summary>
    [OneTimeSetUp]
    public static void PrewarmKeyCache()
    {
        using var warmup = new DevnetFixture(nodeCount: 4, basePort: 29000, validatorsPerNode: 2);
    }

    [Test]
    public async Task FourNode_ReachesFinalization()
    {
        using var cluster = new DevnetCluster(nodeCount: 4, basePort: 19000);
        cluster.StartAll();

        await cluster.WaitForFinalization(
            targetSlot: 20,
            timeout: FinalizationTimeout);

        var checkpoints = new List<(ulong slot, string root)>();
        for (int i = 0; i < 4; i++)
        {
            var cp = await cluster.GetFinalizedCheckpoint(i);
            Assert.That(cp, Is.Not.Null, $"Node {i} finalized checkpoint is null");
            checkpoints.Add(cp!.Value);
        }

        Assert.That(
            checkpoints.Select(c => c.root).Distinct().Count(),
            Is.EqualTo(1),
            "Nodes disagree on finalized root");
    }

    [Test]
    public async Task FourNode_CatchupAfterRestart_Finalizes()
    {
        using var cluster = new DevnetCluster(nodeCount: 4, basePort: 19300);
        cluster.StartAll();

        // Phase 1: 4 nodes finalize to >= 20
        await cluster.WaitForFinalization(
            targetSlot: 20,
            timeout: FinalizationTimeout);

        // Phase 2: stop node 3, 3 nodes continue finalizing to >= 30
        cluster.StopNode(3);

        await cluster.WaitForFinalization(
            targetSlot: 30,
            timeout: FinalizationTimeout,
            nodeIndices: new[] { 0, 1, 2 });

        // Phase 3: restart node 3, all 4 nodes finalize to >= 40
        cluster.RestartNode(3);

        await cluster.WaitForFinalization(
            targetSlot: 40,
            timeout: FinalizationTimeout);
    }

    [Test]
    public async Task FourNode_CheckpointSync_JoinsAndFinalizes()
    {
        using var cluster = new DevnetCluster(nodeCount: 4, basePort: 19600);
        cluster.StartAll();

        await cluster.WaitForFinalization(
            targetSlot: 20,
            timeout: FinalizationTimeout);

        cluster.StopNode(3);
        cluster.CleanNodeData(3);

        var checkpointUrl = $"http://127.0.0.1:{cluster.ApiPort(0)}/lean/v0/states/finalized";
        cluster.RestartNode(3, checkpointSyncUrl: checkpointUrl);

        var node0Cp = await cluster.GetFinalizedCheckpoint(0);
        var targetSlot = (node0Cp?.slot ?? 20) + 20;
        await cluster.WaitForNodeFinalization(3, targetSlot,
            timeout: FinalizationTimeout);
    }

    [Test]
    public async Task TwoNode_TwoValidatorsEach_ReachesFinalization()
    {
        using var cluster = new DevnetCluster(nodeCount: 2, basePort: 19900, validatorsPerNode: 2);
        cluster.StartAll();

        await cluster.WaitForFinalization(
            targetSlot: 20,
            timeout: FinalizationTimeout);

        var checkpoints = new List<(ulong slot, string root)>();
        for (int i = 0; i < 2; i++)
        {
            var cp = await cluster.GetFinalizedCheckpoint(i);
            Assert.That(cp, Is.Not.Null, $"Node {i} finalized checkpoint is null");
            checkpoints.Add(cp!.Value);
        }

        Assert.That(
            checkpoints.Select(c => c.root).Distinct().Count(),
            Is.EqualTo(1),
            "Nodes disagree on finalized root");
    }

    [Test]
    public async Task FourNode_AggregatorRestart_RecoversFinalization()
    {
        using var cluster = new DevnetCluster(nodeCount: 4, basePort: 20500);
        cluster.StartAll();

        await cluster.WaitForFinalization(
            targetSlot: 20,
            timeout: FinalizationTimeout);

        var preFin = await cluster.GetFinalizedCheckpoint(0);
        Assert.That(preFin, Is.Not.Null);

        cluster.StopNode(0);
        await Task.Delay(TimeSpan.FromSeconds(90));

        cluster.RestartNode(0);

        var recoveryTarget = preFin!.Value.slot + 30;
        await cluster.WaitForFinalization(
            targetSlot: recoveryTarget,
            timeout: FinalizationTimeout);

        var postFin = await cluster.GetFinalizedCheckpoint(0);
        Assert.That(postFin, Is.Not.Null);
        Assert.That(postFin!.Value.slot, Is.GreaterThanOrEqualTo(recoveryTarget),
            "Aggregator did not recover finalization after 90s stop");
    }

    [Test]
    public async Task FourNode_TwoSubnets_DedicatedAggregators_ReachesFinalization()
    {
        // 8 validators × 2 subnets with dedicated aggregators. Even with
        // PrewarmKeyCache OneTimeSetUp, this test's per-slot verification
        // cost is roughly 2x a 4-validator test (double the aggregated sigs
        // to verify each slot), so give it double the finalization budget.
        var timeout = TimeSpan.FromMinutes(FinalizationTimeout.TotalMinutes * 2);

        using var cluster = new DevnetCluster(
            nodeCount: 4,
            basePort: 20200,
            validatorsPerNode: 2,
            attestationCommitteeCount: 2,
            nodeIsAggregator: new[] { true, true, false, false },
            nodeAggregateSubnetIds: new[]
            {
                new[] { 0 },
                new[] { 1 },
                Array.Empty<int>(),
                Array.Empty<int>()
            });
        cluster.StartAll();

        await cluster.WaitForFinalization(
            targetSlot: 20,
            timeout: timeout);

        var checkpoints = new List<(ulong slot, string root)>();
        for (int i = 0; i < 4; i++)
        {
            var cp = await cluster.GetFinalizedCheckpoint(i);
            Assert.That(cp, Is.Not.Null, $"Node {i} finalized checkpoint is null");
            checkpoints.Add(cp!.Value);
        }

        Assert.That(
            checkpoints.Select(c => c.root).Distinct().Count(),
            Is.EqualTo(1),
            "Nodes disagree on finalized root");
    }

}
