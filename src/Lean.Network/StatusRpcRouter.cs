namespace Lean.Network;

public sealed class StatusRpcRouter : IStatusRpcRouter
{
    private static readonly Func<CancellationToken, ValueTask<LeanStatusMessage>> EmptyHandler =
        static _ => ValueTask.FromResult(LeanStatusMessage.Zero());
    private static readonly Func<LeanStatusMessage, string?, CancellationToken, ValueTask> EmptyPeerStatusHandler =
        static (_, _, _) => ValueTask.CompletedTask;
    private static readonly Action<string> EmptyPeerLifecycleHandler = static _ => { };

    private Func<CancellationToken, ValueTask<LeanStatusMessage>> _handler = EmptyHandler;
    private Func<LeanStatusMessage, string?, CancellationToken, ValueTask> _peerStatusHandler = EmptyPeerStatusHandler;
    private Action<string> _peerConnectedHandler = EmptyPeerLifecycleHandler;
    private Action<string> _peerDisconnectedHandler = EmptyPeerLifecycleHandler;

    public void SetHandler(Func<CancellationToken, ValueTask<LeanStatusMessage>>? handler)
    {
        Interlocked.Exchange(ref _handler, handler ?? EmptyHandler);
    }

    public void SetPeerStatusHandler(Func<LeanStatusMessage, string?, CancellationToken, ValueTask>? handler)
    {
        Interlocked.Exchange(ref _peerStatusHandler, handler ?? EmptyPeerStatusHandler);
    }

    public void SetPeerConnectedHandler(Action<string>? handler)
    {
        Interlocked.Exchange(ref _peerConnectedHandler, handler ?? EmptyPeerLifecycleHandler);
    }

    public void SetPeerDisconnectedHandler(Action<string>? handler)
    {
        Interlocked.Exchange(ref _peerDisconnectedHandler, handler ?? EmptyPeerLifecycleHandler);
    }

    public ValueTask HandlePeerStatusAsync(LeanStatusMessage status, string? peerKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        var handler = Volatile.Read(ref _peerStatusHandler);
        return handler(status, NormalizePeerKey(peerKey), cancellationToken);
    }

    public void NotifyPeerConnected(string peerKey)
    {
        var normalized = NormalizePeerKey(peerKey);
        if (normalized is not null)
            Volatile.Read(ref _peerConnectedHandler)(normalized);
    }

    public void NotifyPeerDisconnected(string peerKey)
    {
        var normalized = NormalizePeerKey(peerKey);
        if (normalized is not null)
            Volatile.Read(ref _peerDisconnectedHandler)(normalized);
    }

    public ValueTask<LeanStatusMessage> ResolveAsync(CancellationToken cancellationToken)
    {
        var handler = Volatile.Read(ref _handler);
        return handler(cancellationToken);
    }

    private static string? NormalizePeerKey(string? peerKey)
    {
        return string.IsNullOrWhiteSpace(peerKey) ? null : peerKey.Trim();
    }
}
