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

        // Baseline is 10 minutes at 2s/slot. Scale linearly for slower slots.
        var minutes = Math.Max(10, (int)Math.Ceiling(10.0 * secondsPerSlot / 2.0));
        return TimeSpan.FromMinutes(minutes);
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

}
