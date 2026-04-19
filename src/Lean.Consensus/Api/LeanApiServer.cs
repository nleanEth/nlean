using System.Net;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Types;

namespace Lean.Consensus.Api;

/// <summary>
/// Lightweight HTTP API server for lean consensus endpoints.
/// Uses HttpListener to avoid ASP.NET Core dependency.
/// </summary>
public sealed class LeanApiServer
{
    private readonly string _prefix;
    private readonly Func<ApiSnapshot> _getSnapshot;
    private readonly Func<byte[]?> _getFinalizedStateSsz;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public LeanApiServer(string prefix, Func<ApiSnapshot> getSnapshot,
        Func<byte[]?> getFinalizedStateSsz)
    {
        _prefix = prefix.EndsWith('/') ? prefix : prefix + '/';
        _getSnapshot = getSnapshot;
        _getFinalizedStateSsz = getFinalizedStateSsz;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenTask = ListenLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
            await _cts.CancelAsync();
        _listener?.Stop();
        if (_listenTask is not null)
        {
            try { await _listenTask; }
            catch (OperationCanceledException) { }
        }

        _listener?.Close();
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequest(context), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) { break; }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "";
        var response = context.Response;

        try
        {
            switch (path)
            {
                case "/lean/v0/health":
                    WriteJson(response, 200, "{\"status\":\"healthy\",\"service\":\"lean-rpc-api\"}");
                    break;

                case "/lean/v0/checkpoints/justified":
                    var snap1 = _getSnapshot();
                    WriteJson(response, 200,
                        $"{{\"slot\":{snap1.JustifiedSlot},\"root\":\"0x{snap1.JustifiedRoot}\"}}");
                    break;

                case "/lean/v0/checkpoints/finalized":
                    var snap2 = _getSnapshot();
                    WriteJson(response, 200,
                        $"{{\"slot\":{snap2.FinalizedSlot},\"root\":\"0x{snap2.FinalizedRoot}\"}}");
                    break;

                case "/lean/v0/states/finalized":
                    var accept = context.Request.Headers["Accept"] ?? "";
                    if (!accept.Contains("application/octet-stream"))
                    {
                        WriteJson(response, 406, "{\"error\":\"Accept: application/octet-stream required\"}");
                        break;
                    }

                    var ssz = _getFinalizedStateSsz();
                    if (ssz is null)
                    {
                        WriteJson(response, 404, "{\"error\":\"finalized state not available\"}");
                        break;
                    }

                    response.StatusCode = 200;
                    response.ContentType = "application/octet-stream";
                    response.ContentLength64 = ssz.Length;
                    response.OutputStream.Write(ssz);
                    break;

                case "/lean/v0/fork_choice":
                    var fcSnap = _getSnapshot();
                    if (fcSnap.ForkChoice is null)
                    {
                        WriteJson(response, 503, "{\"error\":\"fork choice not available\"}");
                        break;
                    }
                    var fc = fcSnap.ForkChoice;
                    var sb = new System.Text.StringBuilder(4096);
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
                    sb.Append(fcSnap.JustifiedSlot);
                    sb.Append(",\"root\":\"0x");
                    sb.Append(fcSnap.JustifiedRoot);
                    sb.Append("\"},\"finalized\":{\"slot\":");
                    sb.Append(fcSnap.FinalizedSlot);
                    sb.Append(",\"root\":\"0x");
                    sb.Append(fcSnap.FinalizedRoot);
                    sb.Append("\"},\"safe_target\":\"0x");
                    sb.Append(fc.SafeTarget);
                    sb.Append("\",\"validator_count\":");
                    sb.Append(fc.ValidatorCount);
                    sb.Append('}');
                    WriteJson(response, 200, sb.ToString());
                    break;

                case "/lean/v0/fork_choice/ui":
                    var html = ForkChoiceHtml.Content;
                    response.StatusCode = 200;
                    response.ContentType = "text/html; charset=utf-8";
                    var htmlBytes = System.Text.Encoding.UTF8.GetBytes(html);
                    response.ContentLength64 = htmlBytes.Length;
                    response.OutputStream.Write(htmlBytes);
                    break;

                default:
                    WriteJson(response, 404, "{\"error\":\"not found\"}");
                    break;
            }
        }
        catch
        {
            WriteJson(response, 500, "{\"error\":\"internal server error\"}");
        }
        finally
        {
            response.Close();
        }
    }

    private static void WriteJson(HttpListenerResponse response, int statusCode, string json)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
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
