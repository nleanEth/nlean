namespace Lean.Network;

public sealed class BlocksByRootRpcRouter : IBlocksByRootRpcRouter
{
    private static readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]?>> EmptyHandler =
        static (_, _) => ValueTask.FromResult<byte[]?>(null);

    private Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]?>> _handler = EmptyHandler;

    public void SetHandler(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<byte[]?>>? handler)
    {
        Interlocked.Exchange(ref _handler, handler ?? EmptyHandler);
    }

    public ValueTask<byte[]?> ResolveAsync(ReadOnlyMemory<byte> blockRoot, CancellationToken cancellationToken)
    {
        var handler = Volatile.Read(ref _handler);
        return handler(blockRoot, cancellationToken);
    }
}
