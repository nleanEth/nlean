namespace Lean.Storage;

public sealed class StorageConfig
{
    public string DataDir { get; set; } = "data";
    public int CacheSizeMb { get; set; } = 512;
}
