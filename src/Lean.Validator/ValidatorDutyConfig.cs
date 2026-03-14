namespace Lean.Validator;

public sealed class ValidatorDutyConfig
{
    public string? PublicKeyHex { get; init; }
    public string? SecretKeyHex { get; init; }
    public string? PublicKeyPath { get; init; }
    public string? SecretKeyPath { get; init; }
    public IReadOnlyList<string> GenesisValidatorPublicKeys { get; init; } = Array.Empty<string>();
    public ulong ValidatorIndex { get; init; }
    public IReadOnlyList<ulong> ValidatorIndices { get; init; } = Array.Empty<ulong>();
    public IReadOnlyList<string> AllPublicKeyPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllSecretKeyPaths { get; init; } = Array.Empty<string>();
    public uint ActivationEpoch { get; init; }
    public uint NumActiveEpochs { get; init; } = 1024;
    public bool PublishAggregates { get; init; } = false;
}
