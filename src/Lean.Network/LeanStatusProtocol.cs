using System.Text;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Lean.Network;

public sealed class LeanStatusProtocol : ISessionProtocol<LeanStatusMessage, LeanStatusMessage>
{
    private readonly IStatusRpcRouter _router;
    private readonly ILogger<LeanStatusProtocol> _logger;

    public LeanStatusProtocol(IStatusRpcRouter router, ILogger<LeanStatusProtocol> logger)
    {
        _router = router;
        _logger = logger;
    }

    public string Id => RpcProtocols.Status;

    public async Task<LeanStatusMessage> DialAsync(IChannel channel, ISessionContext context, LeanStatusMessage request)
    {
        try
        {
            var requestPayload = LeanReqRespCodec.EncodeStatus(request);
            await LeanReqRespCodec.WriteRequestAsync(channel, requestPayload, channel.CancellationToken);
            await channel.WriteEofAsync(channel.CancellationToken);

            var response = await LeanReqRespCodec.TryReadResponseAsync(channel, channel.CancellationToken);
            if (response is null)
            {
                throw new InvalidOperationException("status request ended before receiving a response.");
            }

            if (response.Value.Code != LeanRpcResponseCodes.Success)
            {
                throw new InvalidOperationException(
                    $"status request failed with code {response.Value.Code}: {Encoding.UTF8.GetString(response.Value.Payload)}");
            }

            return LeanReqRespCodec.DecodeStatus(response.Value.Payload);
        }
        finally
        {
            await channel.CloseAsync();
        }
    }

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        var responseWritten = false;
        try
        {
            byte[] requestPayload;
            try
            {
                requestPayload = await LeanReqRespCodec.ReadRequestPayloadAsync(channel, channel.CancellationToken);
                _logger.LogInformation("status RPC request payload read. PayloadLength={PayloadLength}", requestPayload.Length);
            }
            catch (Exception ex)
            {
                // Keep status RPC progressing even when peer request framing is non-canonical.
                _logger.LogInformation(
                    ex,
                    "status RPC request decode failed; continuing with local status response.");
                requestPayload = [];
            }

            LeanStatusMessage? peerStatus = null;
            if (requestPayload.Length > 0)
            {
                try
                {
                    peerStatus = LeanReqRespCodec.DecodeStatus(requestPayload);
                    _logger.LogInformation(
                        "status RPC request decoded. PeerFinalizedSlot={FinalizedSlot}, PeerHeadSlot={HeadSlot}",
                        peerStatus.FinalizedSlot,
                        peerStatus.HeadSlot);
                }
                catch (Exception ex)
                {
                    // Ream/Zeam interop can send status payload variants during startup.
                    // We serve our local status regardless to keep negotiation forward-progressing.
                    _logger.LogInformation(
                        ex,
                        "status RPC request payload is non-canonical. PayloadLength={PayloadLength}",
                        requestPayload.Length);
                }
            }

            if (peerStatus is not null)
            {
                await _router.HandlePeerStatusAsync(peerStatus, ResolvePeerKey(context), channel.CancellationToken);
            }

            var status = await _router.ResolveAsync(channel.CancellationToken);
            var responsePayload = LeanReqRespCodec.EncodeStatus(status);
            _logger.LogInformation(
                "status RPC response writing. FinalizedSlot={FinalizedSlot}, HeadSlot={HeadSlot}, PayloadLength={PayloadLength}",
                status.FinalizedSlot,
                status.HeadSlot,
                responsePayload.Length);
            await LeanReqRespCodec.WriteResponseAsync(channel, LeanRpcResponseCodes.Success, responsePayload, channel.CancellationToken);
            responseWritten = true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "status RPC handling failed.");
            if (!responseWritten)
            {
                await TrySendErrorAsync(channel, LeanRpcResponseCodes.InvalidRequest, "invalid status request", channel.CancellationToken);
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

    private static string? ResolvePeerKey(ISessionContext context)
    {
        var remoteAddress = context.State.RemoteAddress?.ToString();
        if (!string.IsNullOrWhiteSpace(remoteAddress))
        {
            return remoteAddress;
        }

        return context.State.RemotePeerId?.ToString();
    }
}
