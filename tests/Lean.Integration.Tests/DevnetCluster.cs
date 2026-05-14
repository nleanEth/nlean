using System.Text.Json;

namespace Lean.Integration.Tests;

public sealed class DevnetCluster : IDisposable
{
    private readonly DevnetFixture _fixture;
    private readonly NodeProcess?[] _nodes;
    private readonly HttpClient _http;

    public int NodeCount => _fixture.NodeCount;
    public int ApiPort(int nodeIndex) => _fixture.ApiPorts[nodeIndex];

    public DevnetCluster(
        int nodeCount = 4,
        int basePort = 19100,
        int validatorsPerNode = 1,
        int attestationCommitteeCount = 1,
        bool[]? nodeIsAggregator = null,
        int[][]? nodeAggregateSubnetIds = null)
    {
        _fixture = new DevnetFixture(
            nodeCount,
            basePort,
            validatorsPerNode,
            attestationCommitteeCount,
            nodeIsAggregator,
            nodeAggregateSubnetIds);
        _nodes = new NodeProcess?[nodeCount];
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public void StartAll()
    {
        for (int i = 0; i < NodeCount; i++)
        {
            StartNode(i);
        }
    }

    public void StartNode(int index, string? checkpointSyncUrl = null)
    {
        _nodes[index]?.Dispose();
        _nodes[index] = _fixture.CreateNodeProcess(index, checkpointSyncUrl);
        _nodes[index]!.Start();
    }

    public void StopNode(int index)
    {
        _nodes[index]?.Kill();
    }

    public void CleanNodeData(int index)
    {
        _nodes[index]?.CleanConsensusData();
    }

    public void RestartNode(int index, string? checkpointSyncUrl = null)
    {
        StopNode(index);
        _nodes[index]?.Dispose();
        _nodes[index] = _fixture.CreateNodeProcess(index, checkpointSyncUrl);
        _nodes[index]!.Start();
    }

    public async Task<(ulong slot, string root)?> GetFinalizedCheckpoint(int nodeIndex)
    {
        try
        {
            var url = $"http://127.0.0.1:{_fixture.ApiPorts[nodeIndex]}/lean/v0/checkpoints/finalized";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var slot = doc.RootElement.GetProperty("slot").GetUInt64();
            var root = doc.RootElement.GetProperty("root").GetString() ?? "";
            return (slot, root);
        }
        catch
        {
            return null;
        }
    }

    public async Task WaitForFinalization(ulong targetSlot, TimeSpan timeout, int[]? nodeIndices = null)
    {
        var indices = nodeIndices ?? Enumerable.Range(0, NodeCount).ToArray();
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            // Fail-fast: any expected-running node that exited unexpectedly aborts the wait.
            var deadIndex = indices.FirstOrDefault(i => _nodes[i] is { IsRunning: false }, -1);
            if (deadIndex >= 0)
            {
                var deadDiag = await BuildDiagnosticsAsync(indices);
                throw new InvalidOperationException(
                    $"Node {deadIndex} exited unexpectedly while waiting for finalization >= {targetSlot}.\n{deadDiag}");
            }

            var allReached = true;
            foreach (var i in indices)
            {
                var cp = await GetFinalizedCheckpoint(i);
                if (cp is null || cp.Value.slot < targetSlot)
                {
                    allReached = false;
                    break;
                }
            }

            if (allReached) return;
            await Task.Delay(2000);
        }

        var diag = await BuildDiagnosticsAsync(indices);
        throw new TimeoutException(
            $"Timed out waiting for finalization >= {targetSlot} after {timeout.TotalSeconds}s.\n{diag}");
    }

    public async Task WaitForNodeFinalization(int nodeIndex, ulong targetSlot, TimeSpan timeout)
    {
        await WaitForFinalization(targetSlot, timeout, new[] { nodeIndex });
    }

    private async Task<string> BuildDiagnosticsAsync(int[] indices)
    {
        var lines = new List<string>();
        foreach (var i in indices)
        {
            var node = _nodes[i];
            string status;
            if (node is null) status = "null";
            else if (node.IsRunning) status = "running";
            else status = $"exited code={node.ExitCode?.ToString() ?? "?"} at={node.ExitTime?.ToString("HH:mm:ss.fff") ?? "?"}";
            var checkpoint = await GetFinalizedCheckpoint(i);
            var checkpointText = checkpoint is null
                ? $"api={ApiPort(i)} finalized=<unavailable>"
                : $"api={ApiPort(i)} finalized_slot={checkpoint.Value.slot} finalized_root={checkpoint.Value.root}";
            var stdout = node?.GetStdout() ?? "";
            var stderr = node?.GetStderr() ?? "";
            // Strip `[createdump]` noise (50+ lines per SIGSEGV) before slicing
            // so we keep nlean's own tail. The dump path itself stays useful so
            // surface that one line separately.
            var stdoutLines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var nleanLines = stdoutLines.Where(l => !l.StartsWith("[createdump]"));
            var lastStdout = string.Join('\n', nleanLines.TakeLast(50));
            var dumpLine = stdoutLines.FirstOrDefault(l =>
                l.StartsWith("[createdump]") && l.Contains("Writing full dump to file"));
            var lastStderr = string.Join('\n',
                stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(20));
            var logRef = (node?.StdoutLogPath, node?.StderrLogPath) switch
            {
                (null, null) => "",
                ({ } so, { } se) => $"\nFull logs: stdout={so} stderr={se}",
                ({ } so, null) => $"\nFull stdout log: {so}",
                (null, { } se) => $"\nFull stderr log: {se}",
            };
            var dumpRef = dumpLine is null ? "" : $"\n{dumpLine}";
            lines.Add($"--- Node {i} ({status}) ---\n{checkpointText}{logRef}{dumpRef}\nSTDOUT (last 50, [createdump] filtered):\n{lastStdout}\nSTDERR (last 20):\n{lastStderr}");
        }
        return string.Join('\n', lines);
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node?.Dispose();
        }

        // NodeProcess.Dispose waits for child exit, but macOS doesn't release
        // TCP TIME_WAIT sockets, RocksDB lock files, or mDNS registrations
        // synchronously when the owning process dies. NUnit fires the next
        // test immediately after Dispose returns; without this cooldown the
        // next cluster's nodes occasionally fight the previous cluster's
        // stale OS state and stall on libp2p handshake (observed locally
        // as AggregatorRestart + TwoNode timing out in the full suite but
        // passing in isolation).
        Thread.Sleep(2000);

        _http.Dispose();
        _fixture.Dispose();
    }
}
