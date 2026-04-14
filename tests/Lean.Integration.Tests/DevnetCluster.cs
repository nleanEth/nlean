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
            var status = node is null ? "null" : node.IsRunning ? "running" : "exited";
            var checkpoint = await GetFinalizedCheckpoint(i);
            var checkpointText = checkpoint is null
                ? $"api={ApiPort(i)} finalized=<unavailable>"
                : $"api={ApiPort(i)} finalized_slot={checkpoint.Value.slot} finalized_root={checkpoint.Value.root}";
            var stdout = node?.GetStdout() ?? "";
            var stderr = node?.GetStderr() ?? "";
            var lastStdout = string.Join('\n',
                stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(50));
            var lastStderr = string.Join('\n',
                stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(20));
            lines.Add($"--- Node {i} ({status}) ---\n{checkpointText}\nSTDOUT (last 50):\n{lastStdout}\nSTDERR (last 20):\n{lastStderr}");
        }
        return string.Join('\n', lines);
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node?.Dispose();
        }
        _http.Dispose();
        _fixture.Dispose();
    }
}
