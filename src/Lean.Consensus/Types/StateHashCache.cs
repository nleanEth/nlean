using System.Runtime.CompilerServices;

namespace Lean.Consensus.Types;

/// <summary>
/// Sub-tree merkle root cache keyed by reference identity. Mitigates the
/// `State.HashTreeRoot()` hot-path cost when called repeatedly with shared
/// immutable sub-state (Validators, Config, individual Validator records).
///
/// Why: nlean's proposer fixed-point loop in `ValidatorService.BuildAggregatedAttestations`
/// can re-run state transition up to 10 times per slot. Each STF re-hashes the
/// full State, and the Validators list dominates that cost. Validators is
/// reference-stable across State instances because `ChainStateTransition.TryComputePostState`
/// assigns it without copying — making a `ConditionalWeakTable` keyed by the
/// list reference an effective memoization layer with no correctness risk.
///
/// `ConditionalWeakTable` holds keys via weak refs so GC reclaims unused State
/// trees naturally; lookup is thread-safe.
/// </summary>
internal static class StateHashCache
{
    private static readonly ConditionalWeakTable<IReadOnlyList<Validator>, byte[]> ValidatorListRoots = new();
    private static readonly ConditionalWeakTable<Validator, byte[]> ValidatorRoots = new();
    private static readonly ConditionalWeakTable<Config, byte[]> ConfigRoots = new();

    public static byte[] GetConfigRoot(Config config)
        => ConfigRoots.GetValue(config, static c => c.HashTreeRoot());

    public static byte[] GetValidatorRoot(Validator validator)
        => ValidatorRoots.GetValue(validator, static v => v.HashTreeRoot());

    public static byte[] GetValidatorListRoot(IReadOnlyList<Validator> validators)
        => ValidatorListRoots.GetValue(validators, static list =>
        {
            var roots = new byte[list.Count][];
            for (var i = 0; i < list.Count; i++)
            {
                roots[i] = GetValidatorRoot(list[i]);
            }
            return SszInterop.HashList(roots, SszEncoding.ValidatorRegistryLimit);
        });
}
