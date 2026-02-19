namespace Lean.Network;

public interface IStatusRpcRouter
{
    void SetHandler(Func<CancellationToken, ValueTask<LeanStatusMessage>>? handler);
    void SetPeerStatusHandler(Func<LeanStatusMessage, string?, CancellationToken, ValueTask>? handler);
    ValueTask HandlePeerStatusAsync(LeanStatusMessage status, CancellationToken cancellationToken);
    ValueTask HandlePeerStatusAsync(LeanStatusMessage status, string? peerKey, CancellationToken cancellationToken);
    ValueTask<LeanStatusMessage> ResolveAsync(CancellationToken cancellationToken);
}
