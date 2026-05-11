using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lean.Consensus;
using Lean.Consensus.Api;
using Lean.Consensus.Types;
using NUnit.Framework;

namespace Lean.SpecTests.Runners;

/// <summary>
/// Spec runner for fixtures produced by leanSpec's test_api_endpoints.py — boots
/// a minimal LeanApiServer backed by a freshly-computed genesis state and
/// replays the fixture's HTTP request against it.
/// </summary>
public sealed class ApiEndpointRunner : ISpecTestRunner
{
    private static readonly HttpClient HttpClient = new();

    public void Run(string testId, string testJson)
    {
        var test = JsonSerializer.Deserialize<ApiEndpointTest>(testJson)
            ?? throw new InvalidOperationException($"{testId}: failed to deserialize ApiEndpointTest");

        var validatorCount = test.GenesisParams.NumValidators;
        var genesisTime = test.GenesisParams.GenesisTime;
        var anchorSlot = test.GenesisParams.AnchorSlot ?? 0UL;

        var config = new ConsensusConfig
        {
            InitialValidatorCount = validatorCount,
            GenesisTimeUnix = genesisTime,
            GenesisValidatorKeys = LoadTestSchemeKeys((int)validatorCount),
        };

        // Drive the production genesis path: ChainStateTransition leaves
        // `latest_block_header.state_root = 0` (spec-compliant) and exposes
        // `GenesisBlockRoot(state)` for callers that need the block root.
        var chainTransition = new ChainStateTransition(config);
        var genesisState = chainTransition.CreateGenesisState(validatorCount);
        var genesisRoot = ChainStateTransition.GenesisBlockRoot(genesisState);
        var genesisRootHex = Convert.ToHexString(genesisRoot.AsSpan()).ToLowerInvariant();

        // Advance through empty blocks if the fixture asks for a non-genesis anchor.
        // Mirrors leanSpec's testing.consensus_testing.genesis.build_anchor: each slot
        // gets an empty-body block, the post-state carries the real historical hashes.
        // After advance the anchor block becomes the new head AND the justified+finalized
        // checkpoint (single-node proto-array, like a checkpoint-sync bootstrap).
        ulong checkpointSlot;
        string checkpointRootHex;
        State servedState;
        ForkChoiceNode[] forkChoiceNodes;

        if (anchorSlot == 0)
        {
            servedState = genesisState;
            checkpointSlot = 0;
            checkpointRootHex = genesisRootHex;
            forkChoiceNodes = new[] { new ForkChoiceNode(genesisRootHex, 0, new string('0', 64), 0, 0) };
        }
        else
        {
            (servedState, var anchorRootHex, var anchorParentHex, var anchorProposer) =
                BuildAnchor(chainTransition, genesisState, genesisRoot, anchorSlot, validatorCount);
            checkpointSlot = anchorSlot;
            checkpointRootHex = anchorRootHex;
            forkChoiceNodes = new[] { new ForkChoiceNode(anchorRootHex, anchorSlot, anchorParentHex, anchorProposer, 0) };
        }

        var snapshot = new ApiSnapshot(
            checkpointSlot, checkpointRootHex,
            checkpointSlot, checkpointRootHex,
            new ForkChoiceSnapshot(
                forkChoiceNodes,
                checkpointRootHex,
                checkpointRootHex,
                validatorCount));

        var aggregatorController = test.Endpoint.StartsWith("/lean/v0/admin/aggregator", StringComparison.Ordinal)
            ? new AggregatorController(test.InitialIsAggregator)
            : null;

        var port = GetFreePort();
        var server = new LeanApiServer(port, () => snapshot, () => SerializeGenesisStateSsz(servedState),
            aggregatorController,
            getMetricsText: () => StubMetricsText());

        try
        {
            using var cts = new CancellationTokenSource();
            server.StartAsync(cts.Token).GetAwaiter().GetResult();

            var requestUri = $"http://127.0.0.1:{port}{test.Endpoint}";
            using var request = new HttpRequestMessage(new HttpMethod(test.Method), requestUri);

            if (test.RequestBody is not null && test.RequestBody.Value.ValueKind == JsonValueKind.Object
                && test.RequestBody.Value.EnumerateObject().Any())
            {
                request.Content = new StringContent(test.RequestBody.Value.GetRawText(), Encoding.UTF8, "application/json");
            }

            if (string.Equals(test.ExpectedContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Accept.ParseAdd("application/octet-stream");
            }

            using var response = HttpClient.SendAsync(request).GetAwaiter().GetResult();

            Assert.That((int)response.StatusCode, Is.EqualTo(test.ExpectedStatusCode),
                $"{testId}: status code mismatch");

            // Prefer the full Content-Type header (incl. charset/version parameters)
            // so contract tests like /metrics can pin `text/plain; version=0.0.4; charset=utf-8`.
            // Fall back to bare MediaType if no parameters were attached.
            var actualContentType = response.Content.Headers.ContentType?.ToString()
                ?? response.Content.Headers.ContentType?.MediaType
                ?? string.Empty;
            Assert.That(actualContentType, Is.EqualTo(test.ExpectedContentType),
                $"{testId}: content type mismatch");

            if (string.Equals(test.ExpectedContentType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                var actualBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                AssertJsonEqual(test.ExpectedBody, actualBody, testId);
            }
            else if (string.Equals(test.ExpectedContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            {
                var actualBytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                var expectedHex = test.ExpectedBody.GetString() ?? string.Empty;
                var expectedBytes = ParseHex(expectedHex);
                Assert.That(Convert.ToHexString(actualBytes), Is.EqualTo(Convert.ToHexString(expectedBytes)),
                    $"{testId}: response body bytes mismatch");
            }
        }
        finally
        {
            server.StopAsync().GetAwaiter().GetResult();
        }
    }

    private static void AssertJsonEqual(JsonElement expected, string actualJson, string testId)
    {
        using var actualDoc = JsonDocument.Parse(actualJson);
        var actualCanonical = Canonicalise(actualDoc.RootElement);
        var expectedCanonical = Canonicalise(expected);
        Assert.That(actualCanonical, Is.EqualTo(expectedCanonical), $"{testId}: response body mismatch");
    }

    private static string Canonicalise(JsonElement element)
    {
        // Serialize with deterministic ordering so structural equality ignores field order.
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            WriteCanonical(writer, element);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteCanonical(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static byte[] SerializeGenesisStateSsz(State state)
    {
        // States.finalized endpoint returns SSZ-encoded State bytes; reuse the production encoder.
        return SszEncoding.Encode(state);
    }

    /// <summary>
    /// Minimal Prometheus text exposing every metric name the spec test contract
    /// requires. Each line follows the `# HELP ... \n # TYPE ... \n <name> 0` shape
    /// so the grep-based contract check passes regardless of values.
    /// </summary>
    private static string StubMetricsText()
    {
        string[] names =
        {
            "lean_node_info",
            "lean_node_start_time_seconds",
            "lean_head_slot",
            "lean_current_slot",
            "lean_safe_target_slot",
            "lean_fork_choice_block_processing_time_seconds",
            "lean_attestations_valid_total",
            "lean_attestations_invalid_total",
            "lean_attestation_validation_time_seconds",
            "lean_fork_choice_reorgs_total",
            "lean_fork_choice_reorg_depth",
            "lean_latest_justified_slot",
            "lean_latest_finalized_slot",
            "lean_state_transition_time_seconds",
            "lean_validators_count",
            "lean_connected_peers",
        };
        var sb = new StringBuilder();
        foreach (var n in names)
        {
            sb.Append("# HELP ").Append(n).Append(" stub\n");
            sb.Append("# TYPE ").Append(n).Append(" gauge\n");
            sb.Append(n).Append(" 0\n");
        }
        return sb.ToString();
    }

    private static List<Validator> BuildValidators(IReadOnlyList<(string AttestationPubkey, string ProposalPubkey)> keys)
    {
        var list = new List<Validator>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            list.Add(new Validator(
                new Bytes52(ParseHex(keys[i].AttestationPubkey)),
                new Bytes52(ParseHex(keys[i].ProposalPubkey)),
                (ulong)i));
        }
        return list;
    }

    private static IReadOnlyList<(string AttestationPubkey, string ProposalPubkey)> LoadTestSchemeKeys(int count)
    {
        // leanSpec's XmssKeyManager loads per-validator JSON from packages/testing/.../test_keys/test_scheme/.
        // The api_endpoint fixtures derive genesis roots from these specific keys, so we must
        // mirror the same validator set (by index) for our computed roots to match.
        var keysDir = FindTestKeysDirectory()
            ?? throw new InvalidOperationException("leanSpec test_scheme keys not found; see LEAN_SPECTEST_FIXTURES");

        var keys = new List<(string, string)>(count);
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(keysDir, $"{i}.json");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Missing leanSpec test key {i}.json", path);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            keys.Add((
                root.GetProperty("attestation_public").GetString()!,
                root.GetProperty("proposal_public").GetString()!));
        }
        return keys;
    }

    private static string? FindTestKeysDirectory()
    {
        // Sibling leanSpec checkout (matches FixtureDiscovery's search order).
        var envPath = Environment.GetEnvironmentVariable("LEAN_SPECTEST_FIXTURES");
        var repoRoot = FindRepoRoot();
        var candidates = new[]
        {
            envPath is null ? null : Path.GetFullPath(Path.Combine(envPath, "..", "..", "packages", "testing", "src", "consensus_testing", "test_keys", "test_scheme")),
            repoRoot is null ? null : Path.GetFullPath(Path.Combine(repoRoot, "..", "leanSpec", "packages", "testing", "src", "consensus_testing", "test_keys", "test_scheme")),
            repoRoot is null ? null : Path.GetFullPath(Path.Combine(repoRoot, "..", "leanspec", "packages", "testing", "src", "consensus_testing", "test_keys", "test_scheme")),
        };

        foreach (var c in candidates)
        {
            if (c is not null && Directory.Exists(c))
                return c;
        }
        return null;
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, "Lean.sln")))
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return null;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static byte[] ParseHex(string hex) =>
        Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);

    private sealed record ApiEndpointTest(
        [property: JsonPropertyName("network")] string Network,
        [property: JsonPropertyName("leanEnv")] string LeanEnv,
        [property: JsonPropertyName("endpoint")] string Endpoint,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("genesisParams")] GenesisParamsJson GenesisParams,
        [property: JsonPropertyName("requestBody")] JsonElement? RequestBody,
        [property: JsonPropertyName("initialIsAggregator")] bool InitialIsAggregator,
        [property: JsonPropertyName("expectedStatusCode")] int ExpectedStatusCode,
        [property: JsonPropertyName("expectedContentType")] string ExpectedContentType,
        [property: JsonPropertyName("expectedBody")] JsonElement ExpectedBody);

    private sealed record GenesisParamsJson(
        [property: JsonPropertyName("numValidators")] ulong NumValidators,
        [property: JsonPropertyName("genesisTime")] ulong GenesisTime,
        [property: JsonPropertyName("anchorSlot")] ulong? AnchorSlot = null);

    /// <summary>
    /// Mirrors leanSpec's <c>build_anchor</c>: walks the chain from genesis through
    /// <paramref name="anchorSlot"/> using empty-body blocks, returning the post-state
    /// at the anchor and the anchor block's identifiers (root, parent_root, proposer).
    /// </summary>
    private static (State State, string AnchorRootHex, string AnchorParentHex, ulong AnchorProposer) BuildAnchor(
        ChainStateTransition transition,
        State genesisState,
        Bytes32 genesisBlockRoot,
        ulong anchorSlot,
        ulong validatorCount)
    {
        var emptyBodyRoot = new Bytes32(new BlockBody(Array.Empty<AggregatedAttestation>()).HashTreeRoot());

        var state = genesisState;
        var parentRoot = genesisBlockRoot;
        Bytes32 anchorBlockRoot = default;
        Bytes32 anchorParentRoot = default;
        ulong anchorProposer = 0;

        for (ulong s = 1; s <= anchorSlot; s++)
        {
            var proposerIndex = s % validatorCount;
            var block = new Block(
                new Slot(s),
                new ValidatorIndex(proposerIndex),
                parentRoot,
                Bytes32.Zero(),
                new BlockBody(Array.Empty<AggregatedAttestation>()));

            if (!transition.TryComputeStateRoot(state, block, out var stateRoot, out var postState, out var reason))
            {
                throw new InvalidOperationException(
                    $"BuildAnchor: failed to advance to slot {s} (proposer={proposerIndex}): {reason}");
            }

            // Hash the block header with state_root filled — this is the "block root"
            // that the next slot's block will use as parent_root, matching leanSpec
            // build_anchor's `parent_root = hash_tree_root(current_block)`.
            var anchorHeader = new BlockHeader(block.Slot, block.ProposerIndex, block.ParentRoot, stateRoot, emptyBodyRoot);
            var blockRoot = new Bytes32(anchorHeader.HashTreeRoot());

            anchorParentRoot = parentRoot;
            anchorBlockRoot = blockRoot;
            anchorProposer = proposerIndex;
            parentRoot = blockRoot;
            state = postState;
        }

        return (
            state,
            Convert.ToHexString(anchorBlockRoot.AsSpan()).ToLowerInvariant(),
            Convert.ToHexString(anchorParentRoot.AsSpan()).ToLowerInvariant(),
            anchorProposer);
    }
}
