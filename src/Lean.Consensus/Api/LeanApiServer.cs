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
                    WriteJson(response, 200, """{"status":"ok"}""");
                    break;

                case "/lean/v0/checkpoints/justified":
                    var snap1 = _getSnapshot();
                    WriteJson(response, 200,
                        $$"""{"slot":{{snap1.JustifiedSlot}},"root":"{{snap1.JustifiedRoot}}"}""");
                    break;

                case "/lean/v0/checkpoints/finalized":
                    var snap2 = _getSnapshot();
                    WriteJson(response, 200,
                        $$"""{"slot":{{snap2.FinalizedSlot}},"root":"{{snap2.FinalizedRoot}}"}""");
                    break;

                case "/lean/v0/states/finalized":
                    var accept = context.Request.Headers["Accept"] ?? "";
                    if (!accept.Contains("application/octet-stream"))
                    {
                        WriteJson(response, 406, """{"error":"Accept: application/octet-stream required"}""");
                        break;
                    }

                    var ssz = _getFinalizedStateSsz();
                    if (ssz is null)
                    {
                        WriteJson(response, 404, """{"error":"finalized state not available"}""");
                        break;
                    }

                    response.StatusCode = 200;
                    response.ContentType = "application/octet-stream";
                    response.ContentLength64 = ssz.Length;
                    response.OutputStream.Write(ssz);
                    break;

                default:
                    WriteJson(response, 404, """{"error":"not found"}""");
                    break;
            }
        }
        catch
        {
            WriteJson(response, 500, """{"error":"internal server error"}""");
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

public sealed record ApiSnapshot(
    ulong JustifiedSlot, string JustifiedRoot,
    ulong FinalizedSlot, string FinalizedRoot);
