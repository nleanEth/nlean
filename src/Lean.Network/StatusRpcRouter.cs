namespace Lean.Network;

public sealed class StatusRpcRouter : IStatusRpcRouter
{
    private static readonly Func<CancellationToken, ValueTask<LeanStatusMessage>> EmptyHandler =
        static _ => ValueTask.FromResult(LeanStatusMessage.Zero());
    private static readonly Func<LeanStatusMessage, string?, CancellationToken, ValueTask> EmptyPeerStatusHandler =
        static (_, _, _) => ValueTask.CompletedTask;

    private Func<CancellationToken, ValueTask<LeanStatusMessage>> _handler = EmptyHandler;
    private Func<LeanStatusMessage, string?, CancellationToken, ValueTask> _peerStatusHandler = EmptyPeerStatusHandler;

    public void SetHandler(Func<CancellationToken, ValueTask<LeanStatusMessage>>? handler)
    {
        Interlocked.Exchange(ref _handler, handler ?? EmptyHandler);
    }

    public void SetPeerStatusHandler(Func<LeanStatusMessage, string?, CancellationToken, ValueTask>? handler)
    {
        Interlocked.Exchange(ref _peerStatusHandler, handler ?? EmptyPeerStatusHandler);
    }

    public ValueTask HandlePeerStatusAsync(LeanStatusMessage status, CancellationToken cancellationToken)
    {
        return HandlePeerStatusAsync(status, null, cancellationToken);
    }

    public ValueTask HandlePeerStatusAsync(LeanStatusMessage status, string? peerKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        var handler = Volatile.Read(ref _peerStatusHandler);
        return handler(status, NormalizePeerKey(peerKey), cancellationToken);
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
