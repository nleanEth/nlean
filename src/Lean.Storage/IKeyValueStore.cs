namespace Lean.Storage;

public interface IWriteBatch : IDisposable
{
    void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value);
    void Delete(ReadOnlySpan<byte> key);
    void Commit();
}

public interface IKeyValueStore
{
    byte[]? Get(ReadOnlySpan<byte> key);
    void Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value);
    void Delete(ReadOnlySpan<byte> key);
    IWriteBatch StartBatch();
}
