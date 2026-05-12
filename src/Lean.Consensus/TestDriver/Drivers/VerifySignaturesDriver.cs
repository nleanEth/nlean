using Lean.Consensus.TestDriver.Fixtures;
using Lean.Consensus.Types;
using Lean.Crypto;

namespace Lean.Consensus.TestDriver.Drivers;

/// <summary>
/// Drives the leanSpec verify_signatures fixture: verifies both the proposer's
/// XMSS signature on the block root and every aggregated attestation signature
/// embedded in the block body. Returns a single succeeded/failure summary plus
/// a free-form reason so HTTP callers can echo it on failure.
/// </summary>
public static class VerifySignaturesDriver
{
    private static readonly ILeanSig Signer = new RustLeanSig();
    private static readonly ILeanMultiSig MultiSigner = new RustLeanMultiSig();

    public readonly record struct Result(bool Succeeded, string? Error);

    public static Result Run(VerifySignaturesTest test)
    {
        bool useProd;
        if (string.Equals(test.LeanEnv, "prod", StringComparison.OrdinalIgnoreCase))
            useProd = true;
        else if (string.Equals(test.LeanEnv, "test", StringComparison.OrdinalIgnoreCase))
            useProd = false;
        else
            return new Result(false, $"unsupported leanEnv={test.LeanEnv}");

        var proposerPassed = TryVerifyProposer(test, useProd, out var proposerReason);
        var aggregatesPassed = TryVerifyAggregateAttestations(test, useProd, out var aggregateReason);
        var allValid = proposerPassed && aggregatesPassed;

        if (allValid)
        {
            return new Result(true, null);
        }

        var reason = proposerPassed
            ? $"aggregates: {aggregateReason}"
            : $"proposer: {proposerReason}";
        return new Result(false, reason);
    }

    private static bool TryVerifyAggregateAttestations(VerifySignaturesTest test, bool useProd, out string reason)
    {
        reason = string.Empty;

        var attestations = test.SignedBlock.Block.Body.Attestations?.Data;
        var sigs = test.SignedBlock.Signature.AttestationSignatures?.Data;
        var attCount = attestations?.Count ?? 0;
        var sigCount = sigs?.Count ?? 0;

        if (attCount != sigCount)
        {
            reason = $"attestation count ({attCount}) != signature count ({sigCount})";
            return false;
        }

        if (attCount == 0) return true;

        var validators = test.AnchorState.Validators?.Data
            ?? throw new InvalidOperationException("anchor state has no validators");

        for (var i = 0; i < attCount; i++)
        {
            var att = attestations![i];
            var data = FixtureConverter.ConvertAttestationData(att.Data);
            var attestationRoot = data.HashTreeRoot();

            var participantPubkeys = new List<ReadOnlyMemory<byte>>();
            for (var bitIdx = 0; bitIdx < att.AggregationBits.Data.Count; bitIdx++)
            {
                if (!att.AggregationBits.Data[bitIdx]) continue;
                if (bitIdx >= validators.Count)
                {
                    reason = $"aggregation bit {bitIdx} out of range";
                    return false;
                }
                participantPubkeys.Add(FixtureConverter.ParseHex(validators[bitIdx].AttestationKeyHex));
            }

            // ProofData is a ByteList — JSON wrapper is { "data": "0x..." }.
            var sigEntry = sigs![i];
            var proofData = FixtureConverter.ParseHex(
                sigEntry.GetProperty("proofData").GetProperty("data").GetString() ?? string.Empty);

            bool valid;
            try
            {
                valid = useProd
                    ? MultiSigner.VerifyAggregate(
                        participantPubkeys,
                        attestationRoot,
                        proofData,
                        checked((uint)data.Slot.Value))
                    : MultiSigner.VerifyAggregateTest(
                        participantPubkeys,
                        attestationRoot,
                        proofData,
                        checked((uint)data.Slot.Value));
            }
            catch (Exception ex)
            {
                reason = $"aggregate verify threw: {ex.Message}";
                return false;
            }

            if (!valid)
            {
                reason = $"aggregate attestation {i} rejected";
                return false;
            }
        }

        return true;
    }

    private static bool TryVerifyProposer(VerifySignaturesTest test, bool useProd, out string reason)
    {
        reason = string.Empty;

        var block = FixtureConverter.ConvertBlock(test.SignedBlock.Block);
        var blockRoot = block.HashTreeRoot();

        var validators = test.AnchorState.Validators?.Data
            ?? throw new InvalidOperationException("anchor state has no validators");

        var proposerIndex = (ulong)block.ProposerIndex;
        if (proposerIndex >= (ulong)validators.Count)
        {
            reason = $"proposerIndex {proposerIndex} out of range (count={validators.Count})";
            return false;
        }

        var pubkey = FixtureConverter.ParseHex(validators[(int)proposerIndex].ProposalKeyHex);
        var signatureBytes = FixtureConverter.ParseHex(test.SignedBlock.Signature.ProposerSignature);
        var epoch = checked((uint)block.Slot.Value);

        try
        {
            var ok = useProd
                ? Signer.Verify(pubkey, epoch, blockRoot, signatureBytes)
                : Signer.VerifyTest(pubkey, epoch, blockRoot, signatureBytes);
            if (!ok)
            {
                reason = "proposer XMSS signature rejected";
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = $"proposer verify threw: {ex.Message}";
            return false;
        }
        return true;
    }
}
