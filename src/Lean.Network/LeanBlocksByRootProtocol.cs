using System.Text;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Lean.Network;

public sealed class LeanBlocksByRootProtocol : ISessionProtocol<byte[], byte[]?>
{
    private readonly IBlocksByRootRpcRouter _router;
    private readonly ILogger<LeanBlocksByRootProtocol> _logger;

    public LeanBlocksByRootProtocol(IBlocksByRootRpcRouter router, ILogger<LeanBlocksByRootProtocol> logger)
    {
        _router = router;
        _logger = logger;
    }

    public string Id => RpcProtocols.BlocksByRoot;

    public async Task<byte[]?> DialAsync(IChannel channel, ISessionContext context, byte[] request)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Length != LeanReqRespCodec.RootLength)
            {
                throw new ArgumentException($"block root must be {LeanReqRespCodec.RootLength} bytes.", nameof(request));
            }

            var requestPayload = LeanReqRespCodec.EncodeBlocksByRootRequest(new[] { request });
            await LeanReqRespCodec.WriteRequestAsync(channel, requestPayload, channel.CancellationToken);

            var response = await LeanReqRespCodec.TryReadResponseAsync(channel, channel.CancellationToken);
            if (response is null)
            {
                return null;
            }

            if (response.Value.Code != LeanRpcResponseCodes.Success)
            {
                throw new InvalidOperationException(
                    $"blocks_by_root request failed with code {response.Value.Code}: {Encoding.UTF8.GetString(response.Value.Payload)}");
            }

            return response.Value.Payload;
        }
        finally
        {
            await channel.CloseAsync();
        }
    }

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        var responseWritten = false;
        var responsesSent = 0;
        try
        {
            var requestPayload = await LeanReqRespCodec.ReadRequestPayloadAsync(channel, channel.CancellationToken);
            var requestedRoots = LeanReqRespCodec.DecodeBlocksByRootRequest(requestPayload);
            _logger.LogInformation(
                "blocks_by_root request received. RequestedRoots: {RequestedRoots}",
                requestedRoots.Length);

            if (requestedRoots.Length == 0)
            {
                await TrySendErrorAsync(channel, LeanRpcResponseCodes.InvalidRequest, "empty blocks_by_root request", channel.CancellationToken);
                return;
            }

            foreach (var root in requestedRoots)
            {
                var payload = await _router.ResolveAsync(root, channel.CancellationToken);
                if (payload is null || payload.Length == 0)
                {
                    _logger.LogInformation(
                        "blocks_by_root miss on listener. Root: {Root}",
                        Convert.ToHexString(root));
                    continue;
                }

                await LeanReqRespCodec.WriteResponseAsync(channel, LeanRpcResponseCodes.Success, payload, channel.CancellationToken);
                responseWritten = true;
                responsesSent++;
                _logger.LogInformation(
                    "blocks_by_root hit on listener. Root: {Root}, PayloadLength: {PayloadLength}",
                    Convert.ToHexString(root),
                    payload.Length);
            }

            _logger.LogInformation(
                "blocks_by_root request completed. RequestedRoots: {RequestedRoots}, ResponsesSent: {ResponsesSent}",
                requestedRoots.Length,
                responsesSent);

        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed handling blocks_by_root RPC request.");
            if (!responseWritten)
            {
                await TrySendErrorAsync(channel, LeanRpcResponseCodes.InvalidRequest, "invalid blocks_by_root request", channel.CancellationToken);
            }
        }
        finally
        {
            await channel.CloseAsync();
        }
    }

    private async Task TrySendErrorAsync(IChannel channel, byte code, string message, CancellationToken token)
    {
        try
        {
            await LeanReqRespCodec.WriteResponseAsync(channel, code, Encoding.UTF8.GetBytes(message), token);
        }
        catch
        {
            // Ignore best-effort error response failures.
        }
    }
}
