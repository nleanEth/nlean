namespace Lean.Storage;

public interface IKeyValueStore
{
    byte[]? Get(ReadOnlySpan<byte> key);
    void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value);
    void Delete(ReadOnlySpan<byte> key);
}
