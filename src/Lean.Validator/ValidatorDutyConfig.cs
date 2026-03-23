namespace Lean.Validator;

public sealed class ValidatorDutyConfig
{
    // Dual-key fields for devnet4
    public string? AttestationPublicKeyHex { get; init; }
    public string? AttestationSecretKeyHex { get; init; }
    public string? AttestationPublicKeyPath { get; init; }
    public string? AttestationSecretKeyPath { get; init; }
    public string? ProposalPublicKeyHex { get; init; }
    public string? ProposalSecretKeyHex { get; init; }
    public string? ProposalPublicKeyPath { get; init; }
    public string? ProposalSecretKeyPath { get; init; }

    // Legacy single-key fields for backward compatibility
    public string? PublicKeyHex { get; init; }
    public string? SecretKeyHex { get; init; }
    public string? PublicKeyPath { get; init; }
    public string? SecretKeyPath { get; init; }

    public IReadOnlyList<(string AttestationPubkey, string ProposalPubkey)> GenesisValidatorKeys { get; init; }
        = Array.Empty<(string, string)>();
    public ulong ValidatorIndex { get; init; }
    public IReadOnlyList<ulong> ValidatorIndices { get; init; } = Array.Empty<ulong>();

    // Legacy single-key path lists
    public IReadOnlyList<string> AllPublicKeyPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllSecretKeyPaths { get; init; } = Array.Empty<string>();

    // Dual-key path lists for devnet4
    public IReadOnlyList<string> AllAttestationPublicKeyPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllAttestationSecretKeyPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllProposalPublicKeyPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllProposalSecretKeyPaths { get; init; } = Array.Empty<string>();

    public uint ActivationEpoch { get; init; }
    public uint NumActiveEpochs { get; init; } = 1024;
    public bool PublishAggregates { get; init; } = false;
}
