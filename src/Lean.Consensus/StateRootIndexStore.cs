using System.Diagnostics.CodeAnalysis;
using System.Text;
using Lean.Consensus.Types;
using Lean.Storage;

namespace Lean.Consensus;

public sealed class StateRootIndexStore : IStateRootIndexStore
{
    private const string KeyPrefix = "consensus:sr_idx:";
    private readonly IKeyValueStore _store;

    public StateRootIndexStore(IKeyValueStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public void Save(Bytes32 stateRoot, Bytes32 blockRoot)
    {
        _store.Put(BuildKey(stateRoot), blockRoot.AsSpan());
    }

    public bool TryLoad(Bytes32 stateRoot, [NotNullWhen(true)] out Bytes32 blockRoot)
    {
        var payload = _store.Get(BuildKey(stateRoot));
        if (payload is not null && payload.Length == SszEncoding.Bytes32Length)
        {
            blockRoot = new Bytes32(payload);
            return true;
        }

        blockRoot = Bytes32.Zero();
        return false;
    }

    private static byte[] BuildKey(Bytes32 stateRoot)
    {
        var rootHex = Convert.ToHexString(stateRoot.AsSpan());
        return Encoding.ASCII.GetBytes($"{KeyPrefix}{rootHex}");
    }
}
