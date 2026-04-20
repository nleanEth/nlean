using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lean.Node.Configuration;

/// <summary>
/// Parser for the shared `annotated_validators.yaml` format used by the lean
/// hive simulator and interop tooling. Unlike nlean's native
/// `validator-config.yaml`, which infers validator indices from a per-node
/// `count`, the annotated format explicitly maps each node to a list of
/// `(index, pubkey_hex, privkey_file)` entries — one per role (attester,
/// proposer) under devnet4's dual-key scheme.
///
/// Sample:
/// <code>
/// nlean_0:
///   - index: 0
///     pubkey_hex: "…"
///     privkey_file: "validator_0_attester_key_sk.ssz"
///   - index: 0
///     pubkey_hex: "…"
///     privkey_file: "validator_0_proposer_key_sk.ssz"
/// </code>
/// </summary>
public sealed class AnnotatedValidatorsConfig
{
    private readonly IReadOnlyDictionary<string, List<AnnotatedValidatorEntry>> _entriesByNode;

    private AnnotatedValidatorsConfig(IReadOnlyDictionary<string, List<AnnotatedValidatorEntry>> entries)
    {
        _entriesByNode = entries;
    }

    public static AnnotatedValidatorsConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"annotated_validators.yaml not found: {path}");

        var yaml = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var raw = deserializer.Deserialize<Dictionary<string, List<AnnotatedValidatorEntry>>>(yaml)
                  ?? new Dictionary<string, List<AnnotatedValidatorEntry>>();

        // Preserve YAML ordering within each node so the positional fallback
        // (first entry of a same-index pair = attester) remains deterministic.
        var normalized = new Dictionary<string, List<AnnotatedValidatorEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entries) in raw)
        {
            normalized[name] = entries ?? new List<AnnotatedValidatorEntry>();
        }
        return new AnnotatedValidatorsConfig(normalized);
    }

    public bool ContainsNode(string? nodeName) =>
        !string.IsNullOrWhiteSpace(nodeName) && _entriesByNode.ContainsKey(nodeName);

    public IReadOnlyList<AnnotatedValidatorEntry> GetNodeEntries(string? nodeName)
    {
        if (string.IsNullOrWhiteSpace(nodeName)) return Array.Empty<AnnotatedValidatorEntry>();
        return _entriesByNode.TryGetValue(nodeName, out var entries)
            ? entries
            : Array.Empty<AnnotatedValidatorEntry>();
    }

    /// <summary>
    /// Split a node's entries into (attestation, proposal) key file paths per
    /// validator index. Identification rule (in priority order):
    ///   1. Filename contains `_proposer` — proposal key.
    ///   2. Filename contains `_attester` — attestation key.
    ///   3. Positional: within a same-index pair, first entry = attester,
    ///      second = proposer.
    ///   4. Single entry for an index — treated as attester; proposer copies it.
    /// </summary>
    public IReadOnlyList<ResolvedValidator> ResolveNodeValidators(string nodeName)
    {
        var entries = GetNodeEntries(nodeName);
        if (entries.Count == 0) return Array.Empty<ResolvedValidator>();

        var byIndex = new Dictionary<ulong, List<AnnotatedValidatorEntry>>();
        foreach (var entry in entries)
        {
            if (!byIndex.TryGetValue(entry.Index, out var list))
            {
                list = new List<AnnotatedValidatorEntry>();
                byIndex[entry.Index] = list;
            }
            list.Add(entry);
        }

        var resolved = new List<ResolvedValidator>(byIndex.Count);
        foreach (var (index, bucket) in byIndex.OrderBy(kv => kv.Key))
        {
            AnnotatedValidatorEntry? attester = null;
            AnnotatedValidatorEntry? proposer = null;
            foreach (var entry in bucket)
            {
                var file = entry.PrivkeyFile ?? string.Empty;
                if (file.Contains("_proposer", StringComparison.OrdinalIgnoreCase))
                    proposer ??= entry;
                else if (file.Contains("_attester", StringComparison.OrdinalIgnoreCase))
                    attester ??= entry;
            }

            // Positional fallback for entries without name hints.
            if (attester is null || proposer is null)
            {
                if (bucket.Count >= 2)
                {
                    attester ??= bucket[0];
                    proposer ??= bucket[1];
                }
                else if (bucket.Count == 1)
                {
                    attester ??= bucket[0];
                    proposer ??= bucket[0];
                }
            }

            resolved.Add(new ResolvedValidator(
                index,
                attester?.PubkeyHex ?? string.Empty,
                attester?.PrivkeyFile ?? string.Empty,
                proposer?.PubkeyHex ?? string.Empty,
                proposer?.PrivkeyFile ?? string.Empty));
        }
        return resolved;
    }
}

public sealed class AnnotatedValidatorEntry
{
    public ulong Index { get; set; }

    [YamlMember(Alias = "pubkey_hex")]
    public string? PubkeyHex { get; set; }

    [YamlMember(Alias = "privkey_file")]
    public string? PrivkeyFile { get; set; }
}

public sealed record ResolvedValidator(
    ulong Index,
    string AttestationPubkeyHex,
    string AttestationPrivkeyFile,
    string ProposalPubkeyHex,
    string ProposalPrivkeyFile);
