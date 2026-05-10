using System.Text;
using System.Text.Json;
using Lean.Consensus.ForkChoice;
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
    private WebApplication? _app;

    public LeanApiServer(int port, Func<ApiSnapshot> getSnapshot,
        Func<byte[]?> getFinalizedStateSsz,
        AggregatorController? aggregatorController = null)
    {
        _port = port;
        _getSnapshot = getSnapshot;
        _getFinalizedStateSsz = getFinalizedStateSsz;
        _aggregatorController = aggregatorController;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(opts => opts.ListenAnyIP(_port));
        builder.Logging.ClearProviders();

        _app = builder.Build();

        _app.MapGet("/lean/v0/health",
            () => Results.Content("{\"status\":\"healthy\",\"service\":\"lean-rpc-api\"}", "application/json"));

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
