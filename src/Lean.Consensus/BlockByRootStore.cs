using System.Diagnostics.CodeAnalysis;
using System.Text;
using Lean.Consensus.Types;
using Lean.Storage;

namespace Lean.Consensus;

public sealed class BlockByRootStore : IBlockByRootStore
{
    private const string KeyPrefix = "consensus:block:";
    private readonly IKeyValueStore _store;

    public BlockByRootStore(IKeyValueStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public void Save(Bytes32 blockRoot, ReadOnlySpan<byte> payload)
    {
        _store.Put(BuildKey(blockRoot), payload.ToArray());
    }

    public bool TryLoad(Bytes32 blockRoot, [NotNullWhen(true)] out byte[]? payload)
    {
        payload = _store.Get(BuildKey(blockRoot));
        return payload is not null;
    }

    public void Delete(Bytes32 blockRoot)
    {
        _store.Delete(BuildKey(blockRoot));
    }

    private static byte[] BuildKey(Bytes32 blockRoot)
    {
        var rootHex = Convert.ToHexString(blockRoot.AsSpan());
        return Encoding.ASCII.GetBytes($"{KeyPrefix}{rootHex}");
    }
}
