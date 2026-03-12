using System.Diagnostics.CodeAnalysis;
using System.Text;
using Lean.Consensus.Types;
using Lean.Storage;

namespace Lean.Consensus;

public sealed class SlotIndexStore : ISlotIndexStore
{
    private const string KeyPrefix = "consensus:slot_idx:";
    private static readonly byte[] KeyPrefixBytes = Encoding.ASCII.GetBytes(KeyPrefix);
    private readonly IKeyValueStore _store;

    public SlotIndexStore(IKeyValueStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public void Save(ulong slot, Bytes32 blockRoot)
    {
        _store.Put(BuildKey(slot), blockRoot.AsSpan());
    }

    public bool TryLoad(ulong slot, [NotNullWhen(true)] out Bytes32 blockRoot)
    {
        var payload = _store.Get(BuildKey(slot));
        if (payload is not null && payload.Length == SszEncoding.Bytes32Length)
        {
            blockRoot = new Bytes32(payload);
            return true;
        }

        blockRoot = Bytes32.Zero();
        return false;
    }

    public void DeleteBelow(ulong cutoffSlot)
    {
        var keysToDelete = new List<byte[]>();
        foreach (var (key, _) in _store.PrefixScan(KeyPrefixBytes))
        {
            var slot = SlotFromKey(key);
            if (slot < cutoffSlot)
                keysToDelete.Add(key);
            else
                break; // Keys are in lexicographic (= numeric) order
        }

        if (keysToDelete.Count == 0)
            return;

        using var batch = _store.StartBatch();
        foreach (var key in keysToDelete)
            batch.Delete(key);
        batch.Commit();
    }

    public IReadOnlyList<(ulong Slot, Bytes32 Root)> GetEntriesBelow(ulong cutoffSlot)
    {
        var entries = new List<(ulong, Bytes32)>();
        foreach (var (key, value) in _store.PrefixScan(KeyPrefixBytes))
        {
            var slot = SlotFromKey(key);
            if (slot < cutoffSlot)
            {
                if (value.Length == SszEncoding.Bytes32Length)
                    entries.Add((slot, new Bytes32(value)));
            }
            else
            {
                break;
            }
        }

        return entries;
    }

    private static byte[] BuildKey(ulong slot)
    {
        return Encoding.ASCII.GetBytes($"{KeyPrefix}{slot:X16}");
    }

    private static ulong SlotFromKey(byte[] key)
    {
        var hex = Encoding.ASCII.GetString(key, KeyPrefix.Length, 16);
        return ulong.Parse(hex, System.Globalization.NumberStyles.HexNumber);
    }
}
