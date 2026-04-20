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
        var forkChoiceNode = new ForkChoiceNode(
            genesisRootHex, 0, new string('0', 64), 0, 0);

        var snapshot = new ApiSnapshot(
            0, genesisRootHex,
            0, genesisRootHex,
            new ForkChoiceSnapshot(
                new[] { forkChoiceNode },
                genesisRootHex,
                genesisRootHex,
                validatorCount));

        var aggregatorController = test.Endpoint.StartsWith("/lean/v0/admin/aggregator", StringComparison.Ordinal)
            ? new AggregatorController(test.InitialIsAggregator)
            : null;

        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        var server = new LeanApiServer(prefix, () => snapshot, () => SerializeGenesisStateSsz(genesisState),
            aggregatorController);

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

            var actualContentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
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
        [property: JsonPropertyName("genesisTime")] ulong GenesisTime);
}
