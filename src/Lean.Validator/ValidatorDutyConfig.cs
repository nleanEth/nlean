namespace Lean.Validator;

public sealed class ValidatorDutyConfig
{
    public string? PublicKeyHex { get; init; }
    public string? SecretKeyHex { get; init; }
    public ulong ValidatorIndex { get; init; }
    public uint ActivationEpoch { get; init; }
    public uint NumActiveEpochs { get; init; } = 1024;
    public bool PublishAggregates { get; init; } = true;
}
