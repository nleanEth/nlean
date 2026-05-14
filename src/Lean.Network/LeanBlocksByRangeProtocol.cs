using System.Text;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Lean.Network;

/// <summary>
/// Implements BlocksByRangeV1 (hive PR ethereum/hive#1489). Wire request is a
/// 16-byte fixed SSZ container { start_slot: u64, count: u64 }; response is a
/// stream of SSZ-encoded SignedBlock chunks in strictly increasing slot order,
/// gaps elided. count==0 and count&gt;MAX_REQUEST_BLOCKS are INVALID_REQUEST.
/// </summary>
public sealed class LeanBlocksByRangeProtocol : ISessionProtocol<(ulong StartSlot, ulong Count), byte[][]>
{
    private readonly IBlocksByRangeRpcRouter _router;
    private readonly ILogger<LeanBlocksByRangeProtocol> _logger;

    public LeanBlocksByRangeProtocol(IBlocksByRangeRpcRouter router, ILogger<LeanBlocksByRangeProtocol> logger)
    {
        _router = router;
        _logger = logger;
    }

    public string Id => RpcProtocols.BlocksByRange;

    public async Task<byte[][]> DialAsync(IChannel channel, ISessionContext context, (ulong StartSlot, ulong Count) request)
    {
        try
        {
            if (request.Count == 0 || request.Count > (ulong)LeanReqRespCodec.MaxBlocksByRangeRequestCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.Count,
                    $"blocks_by_range count must be in [1, {LeanReqRespCodec.MaxBlocksByRangeRequestCount}].");
            }

            var requestPayload = LeanReqRespCodec.EncodeBlocksByRangeRequest(request.StartSlot, request.Count);
            await LeanReqRespCodec.WriteRequestAsync(channel, requestPayload, channel.CancellationToken);
            await TryWriteEofAsync(channel);

            var results = new List<byte[]>();
            while (true)
            {
                var response = await LeanReqRespCodec.TryReadResponseAsync(channel, channel.CancellationToken);
                if (response is null)
                    break;

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
            (ulong startSlot, ulong count) = LeanReqRespCodec.DecodeBlocksByRangeRequest(requestPayload);
            _logger.LogInformation(
                "blocks_by_range request received. StartSlot: {StartSlot}, Count: {Count}",
                startSlot, count);

            if (count == 0 || count > (ulong)LeanReqRespCodec.MaxBlocksByRangeRequestCount)
            {
                await TrySendErrorAsync(
                    channel,
                    LeanRpcResponseCodes.InvalidRequest,
                    count == 0
                        ? "blocks_by_range count must be > 0"
                        : $"blocks_by_range count exceeds max ({LeanReqRespCodec.MaxBlocksByRangeRequestCount})",
                    channel.CancellationToken);
                responseWritten = true;
                return;
            }

            for (ulong i = 0; i < count; i++)
            {
                var slot = startSlot + i;
                var payload = await _router.ResolveAsync(slot, channel.CancellationToken);
                if (payload is null || payload.Length == 0)
                {
                    continue;
                }

                await LeanReqRespCodec.WriteResponseAsync(channel, LeanRpcResponseCodes.Success, payload, channel.CancellationToken);
                responseWritten = true;
                responsesSent++;
            }

            await TryWriteEofAsync(channel);
            _logger.LogInformation(
                "blocks_by_range request completed. StartSlot: {StartSlot}, Count: {Count}, ResponsesSent: {ResponsesSent}",
                startSlot, count, responsesSent);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed handling blocks_by_range RPC request.");
            if (!responseWritten)
            {
                await TrySendErrorAsync(channel, LeanRpcResponseCodes.InvalidRequest, "invalid blocks_by_range request", channel.CancellationToken);
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
            // Best-effort error response.
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
            _logger.LogDebug(ex, "blocks_by_range EOF write ignored during stream shutdown.");
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
            _logger.LogDebug(ex, "blocks_by_range channel close ignored during stream shutdown.");
        }
    }
}
