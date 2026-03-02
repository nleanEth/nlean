namespace Lean.Network;

public interface IStatusRpcRouter
{
    void SetHandler(Func<CancellationToken, ValueTask<LeanStatusMessage>>? handler);
    void SetPeerStatusHandler(Func<LeanStatusMessage, string?, CancellationToken, ValueTask>? handler);
    void SetPeerConnectedHandler(Action<string>? handler);
    void SetPeerDisconnectedHandler(Action<string>? handler);
    ValueTask HandlePeerStatusAsync(LeanStatusMessage status, CancellationToken cancellationToken);
    ValueTask HandlePeerStatusAsync(LeanStatusMessage status, string? peerKey, CancellationToken cancellationToken);
    void NotifyPeerConnected(string peerKey);
    void NotifyPeerDisconnected(string peerKey);
    ValueTask<LeanStatusMessage> ResolveAsync(CancellationToken cancellationToken);
}
