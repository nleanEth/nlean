namespace Lean.Network;

public sealed class BlocksByRangeRpcRouter : IBlocksByRangeRpcRouter
{
    private static readonly Func<ulong, CancellationToken, ValueTask<byte[]?>> EmptyHandler =
        static (_, _) => ValueTask.FromResult<byte[]?>(null);

    private Func<ulong, CancellationToken, ValueTask<byte[]?>> _handler = EmptyHandler;

    public void SetHandler(Func<ulong, CancellationToken, ValueTask<byte[]?>>? handler)
    {
        Interlocked.Exchange(ref _handler, handler ?? EmptyHandler);
    }

    public ValueTask<byte[]?> ResolveAsync(ulong slot, CancellationToken cancellationToken)
    {
        var handler = Volatile.Read(ref _handler);
        return handler(slot, cancellationToken);
    }
}
