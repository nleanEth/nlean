using System.Text;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Lean.Network;

public sealed class LeanBlocksByRootProtocol : ISessionProtocol<byte[][], byte[][]>
{
    private readonly IBlocksByRootRpcRouter _router;
    private readonly ILogger<LeanBlocksByRootProtocol> _logger;

    public LeanBlocksByRootProtocol(IBlocksByRootRpcRouter router, ILogger<LeanBlocksByRootProtocol> logger)
    {
        _router = router;
        _logger = logger;
    }

    public string Id => RpcProtocols.BlocksByRoot;

    public async Task<byte[][]> DialAsync(IChannel channel, ISessionContext context, byte[][] roots)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(roots);
            if (roots.Length == 0)
                return [];

            foreach (var root in roots)
            {
                if (root is null || root.Length != LeanReqRespCodec.RootLength)
                    throw new ArgumentException($"Each root must be {LeanReqRespCodec.RootLength} bytes.", nameof(roots));
            }

            var requestPayload = LeanReqRespCodec.EncodeBlocksByRootRequest(roots);
            await LeanReqRespCodec.WriteRequestAsync(channel, requestPayload, channel.CancellationToken);
            await TryWriteEofAsync(channel);

            // Read streamed response chunks until EOF.
            // Each chunk: [response_code: 1 byte][varint: uncompressed_length][snappy_framed_payload]
            var results = new List<byte[]>();
            while (true)
            {
                var response = await LeanReqRespCodec.TryReadResponseAsync(channel, channel.CancellationToken);
                if (response is null)
                    break; // EOF — no more responses

                if (response.Value.Code == LeanRpcResponseCodes.Success)
                    results.Add(response.Value.Payload);
            }

            return results.ToArray();
        }
        finally
        {
            await TryCloseAsync(channel);
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

            await TryWriteEofAsync(channel);

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
            await TryCloseAsync(channel);
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

    private async Task TryWriteEofAsync(IChannel channel)
    {
        try
        {
            await channel.WriteEofAsync(channel.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "blocks_by_root EOF write ignored during stream shutdown.");
        }
    }

    private async Task TryCloseAsync(IChannel channel)
    {
        try
        {
            await channel.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "blocks_by_root channel close ignored during stream shutdown.");
        }
    }
}
