using System.Text;
using System.Text.Json;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.TestDriver.Drivers;
using Lean.Consensus.TestDriver.Fixtures;
using Lean.Consensus.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lean.Consensus.Api;

public sealed class LeanApiServer
{
    private readonly int _port;
    private readonly Func<ApiSnapshot> _getSnapshot;
    private readonly Func<byte[]?> _getFinalizedStateSsz;
    private readonly AggregatorController? _aggregatorController;
    private readonly Func<string?>? _getMetricsText;
    private WebApplication? _app;

    public LeanApiServer(int port, Func<ApiSnapshot> getSnapshot,
        Func<byte[]?> getFinalizedStateSsz,
        AggregatorController? aggregatorController = null,
        Func<string?>? getMetricsText = null)
    {
        _port = port;
        _getSnapshot = getSnapshot;
        _getFinalizedStateSsz = getFinalizedStateSsz;
        _aggregatorController = aggregatorController;
        _getMetricsText = getMetricsText;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(opts => opts.ListenAnyIP(_port));
        builder.Logging.ClearProviders();

        _app = builder.Build();

        _app.MapGet("/lean/v0/health",
            () => Results.Content("{\"status\":\"healthy\",\"service\":\"lean-rpc-api\"}", "application/json"));

        // Hive test-driver endpoints: gated on HIVE_LEAN_TEST_DRIVER=1 so production
        // builds don't expose the in-process spec-test surface.
        var testDriverEnabled = string.Equals(
            Environment.GetEnvironmentVariable("HIVE_LEAN_TEST_DRIVER"),
            "1",
            StringComparison.Ordinal);
        if (testDriverEnabled)
        {
            var forkChoiceDriver = new ForkChoiceDriver();
            var testDriverJsonOpts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };

            _app.MapPost("/lean/v0/test_driver/fork_choice/init", async (HttpContext ctx) =>
            {
                var req = await JsonSerializer.DeserializeAsync<ForkChoiceDriver.InitRequest>(ctx.Request.Body);
                if (forkChoiceDriver.TryInit(req, out var err))
                {
                    return Results.NoContent();
                }
                return Results.Json(new { error = err }, statusCode: 400);
            });

            _app.MapPost("/lean/v0/test_driver/fork_choice/step", async (HttpContext ctx) =>
            {
                var step = await JsonSerializer.DeserializeAsync<ForkChoiceStep>(ctx.Request.Body)
                    ?? throw new InvalidOperationException("missing step body");
                var result = forkChoiceDriver.ApplyStep(step);
                return Results.Json(result, testDriverJsonOpts);
            });

            _app.MapPost("/lean/v0/test_driver/state_transition/run", async (HttpContext ctx) =>
            {
                var test = await JsonSerializer.DeserializeAsync<StateTransitionTest>(ctx.Request.Body)
                    ?? throw new InvalidOperationException("missing state_transition body");
                var result = StateTransitionDriver.Run(test);
                return Results.Json(result, testDriverJsonOpts);
            });

            _app.MapPost("/lean/v0/test_driver/verify_signatures/run", async (HttpContext ctx) =>
            {
                var test = await JsonSerializer.DeserializeAsync<VerifySignaturesTest>(ctx.Request.Body)
                    ?? throw new InvalidOperationException("missing verify_signatures body");
                var result = VerifySignaturesDriver.Run(test);
                return Results.Json(result, testDriverJsonOpts);
            });
        }

        if (_getMetricsText is not null)
        {
            // Prometheus scrape endpoint exposed on the consensus API port. Production
            // routes /metrics through Lean.Metrics on a dedicated port; this getter is
            // populated only in test/spec contexts where the suite expects a single
            // shared port. Set Content-Type directly to preserve the `version=0.0.4`
            // parameter required by the Prometheus scrape contract — `Results.Content`
            // round-trips through MediaTypeHeaderValue which drops unrecognized params.
            _app.MapGet("/metrics", async (HttpContext ctx) =>
            {
                ctx.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
                await ctx.Response.WriteAsync(_getMetricsText() ?? string.Empty);
            });
        }

        _app.MapGet("/lean/v0/checkpoints/justified", () =>
        {
            var s = _getSnapshot();
            return Results.Content($"{{\"slot\":{s.JustifiedSlot},\"root\":\"0x{s.JustifiedRoot}\"}}", "application/json");
        });

        _app.MapGet("/lean/v0/checkpoints/finalized", () =>
        {
            var s = _getSnapshot();
            return Results.Content($"{{\"slot\":{s.FinalizedSlot},\"root\":\"0x{s.FinalizedRoot}\"}}", "application/json");
        });

        _app.MapGet("/lean/v0/states/finalized", () =>
        {
            var ssz = _getFinalizedStateSsz();
            return ssz is null
                ? Results.Json(new { error = "finalized state not available" }, statusCode: 404)
                : Results.Bytes(ssz, "application/octet-stream");
        });

        _app.MapGet("/lean/v0/fork_choice", () =>
        {
            var snap = _getSnapshot();
            if (snap.ForkChoice is null)
            {
                return Results.Json(new { error = "fork choice not available" }, statusCode: 503);
            }
            return Results.Content(BuildForkChoiceJson(snap), "application/json");
        });

        _app.MapGet("/lean/v0/fork_choice/ui",
            () => Results.Content(ForkChoiceHtml.Content, "text/html; charset=utf-8"));

        _app.MapGet("/lean/v0/admin/aggregator", () =>
        {
            if (_aggregatorController is null)
            {
                return Results.Json(new { error = "aggregator controller not available" }, statusCode: 503);
            }
            var flag = _aggregatorController.IsEnabled ? "true" : "false";
            return Results.Content($"{{\"is_aggregator\":{flag}}}", "application/json");
        });

        _app.MapPost("/lean/v0/admin/aggregator", async (HttpContext ctx) =>
        {
            if (_aggregatorController is null)
            {
                return Results.Json(new { error = "aggregator controller not available" }, statusCode: 503);
            }

            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            bool enabled;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("enabled", out var enabledElement))
                {
                    return Results.Json(new { error = "missing 'enabled' field in body" }, statusCode: 400);
                }

                if (enabledElement.ValueKind != JsonValueKind.True &&
                    enabledElement.ValueKind != JsonValueKind.False)
                {
                    return Results.Json(new { error = "'enabled' must be a boolean" }, statusCode: 400);
                }

                enabled = enabledElement.GetBoolean();
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "invalid JSON body" }, statusCode: 400);
            }

            var previous = _aggregatorController.SetEnabled(enabled);
            var enabledFlag = enabled ? "true" : "false";
            var previousFlag = previous ? "true" : "false";
            return Results.Content(
                $"{{\"is_aggregator\":{enabledFlag},\"previous\":{previousFlag}}}",
                "application/json");
        });

        await _app.StartAsync(ct);
    }

    public async Task StopAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private static string BuildForkChoiceJson(ApiSnapshot snap)
    {
        var fc = snap.ForkChoice!;
        var sb = new StringBuilder(4096);
        sb.Append("{\"nodes\":[");
        for (int i = 0; i < fc.Nodes.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var n = fc.Nodes[i];
            sb.Append("{\"root\":\"0x");
            sb.Append(n.Root);
            sb.Append("\",\"slot\":");
            sb.Append(n.Slot);
            sb.Append(",\"parent_root\":\"0x");
            sb.Append(n.ParentRoot);
            sb.Append("\",\"proposer_index\":");
            sb.Append(n.ProposerIndex);
            sb.Append(",\"weight\":");
            sb.Append(n.Weight);
            sb.Append('}');
        }
        sb.Append("],\"head\":\"0x");
        sb.Append(fc.Head);
        sb.Append("\",\"justified\":{\"slot\":");
        sb.Append(snap.JustifiedSlot);
        sb.Append(",\"root\":\"0x");
        sb.Append(snap.JustifiedRoot);
        sb.Append("\"},\"finalized\":{\"slot\":");
        sb.Append(snap.FinalizedSlot);
        sb.Append(",\"root\":\"0x");
        sb.Append(snap.FinalizedRoot);
        sb.Append("\"},\"safe_target\":\"0x");
        sb.Append(fc.SafeTarget);
        sb.Append("\",\"validator_count\":");
        sb.Append(fc.ValidatorCount);
        sb.Append('}');
        return sb.ToString();
    }
}

public sealed record ForkChoiceNode(
    string Root, ulong Slot, string ParentRoot, ulong ProposerIndex, long Weight);

public sealed record ForkChoiceSnapshot(
    IReadOnlyList<ForkChoiceNode> Nodes,
    string Head, string SafeTarget, ulong ValidatorCount);

public sealed record ApiSnapshot(
    ulong JustifiedSlot, string JustifiedRoot,
    ulong FinalizedSlot, string FinalizedRoot,
    ForkChoiceSnapshot? ForkChoice = null);
