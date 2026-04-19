using System.Text.Json;
using Lean.Consensus.Types;
using Lean.Crypto;
using Lean.SpecTests.Types;
using NUnit.Framework;

namespace Lean.SpecTests.Runners;

public sealed class VerifySignaturesRunner : ISpecTestRunner
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly ILeanSig Signer = new RustLeanSig();
    private static readonly ILeanMultiSig MultiSigner = new RustLeanMultiSig();

    public void Run(string testId, string testJson)
    {
        var test = JsonSerializer.Deserialize<VerifySignaturesTest>(testJson, SerializerOptions)
            ?? throw new InvalidOperationException($"{testId}: failed to deserialize VerifySignaturesTest");

        // Only the TEST scheme is wired through the FFI for verify_signatures fixtures.
        // leanEnv=prod would need real XMSS key fixtures, which the current suite doesn't publish.
        if (!string.Equals(test.LeanEnv, "test", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"Unsupported leanEnv={test.LeanEnv}. Only 'test' is wired through VerifyTest FFI.");
            return;
        }

        var expectFailure = !string.IsNullOrEmpty(test.ExpectException);
        var proposerPassed = TryVerifyProposer(test, out var proposerReason);
        var aggregatesPassed = TryVerifyAggregateAttestations(test, out var aggregateReason);
        var allValid = proposerPassed && aggregatesPassed;

        if (expectFailure)
        {
            Assert.That(allValid, Is.False,
                $"{testId}: expected exception {test.ExpectException} but all signatures verified as valid");
        }
        else
        {
            Assert.That(allValid, Is.True,
                $"{testId}: expected valid signatures but verification failed (proposer={proposerReason}; aggregates={aggregateReason})");
        }
    }

    private static bool TryVerifyAggregateAttestations(VerifySignaturesTest test, out string reason)
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
            var data = new AttestationData(
                new Slot(att.Data.Slot),
                new Checkpoint(new Bytes32(ParseHex(att.Data.Head.Root)), new Slot(att.Data.Head.Slot)),
                new Checkpoint(new Bytes32(ParseHex(att.Data.Target.Root)), new Slot(att.Data.Target.Slot)),
                new Checkpoint(new Bytes32(ParseHex(att.Data.Source.Root)), new Slot(att.Data.Source.Slot)));
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
                participantPubkeys.Add(ParseHex(validators[bitIdx].AttestationKeyHex));
            }

            // ProofData is a ByteList — JSON wrapper is { "data": "0x..." }.
            var sigEntry = sigs![i];
            var proofData = ParseHex(sigEntry.GetProperty("proofData").GetProperty("data").GetString() ?? string.Empty);

            bool valid;
            try
            {
                valid = MultiSigner.VerifyAggregateTest(
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

    private static bool TryVerifyProposer(VerifySignaturesTest test, out string reason)
    {
        reason = string.Empty;

        var block = ConvertBlock(test.SignedBlock.Block);
        var blockRoot = block.HashTreeRoot();

        var validators = test.AnchorState.Validators?.Data
            ?? throw new InvalidOperationException("anchor state has no validators");

        var proposerIndex = (ulong)block.ProposerIndex;
        if (proposerIndex >= (ulong)validators.Count)
        {
            reason = $"proposerIndex {proposerIndex} out of range (count={validators.Count})";
            return false;
        }

        var proposalKeyHex = validators[(int)proposerIndex].ProposalKeyHex;
        var pubkey = ParseHex(proposalKeyHex);

        // proposerSignature is SSZ-encoded XmssSignature bytes; the Rust FFI expects the same
        // canonical SSZ representation (Serializable::from_bytes uses from_ssz_bytes).
        var signatureBytes = ParseHex(test.SignedBlock.Signature.ProposerSignature);

        var epoch = checked((uint)block.Slot.Value);
        return Signer.VerifyTest(pubkey, epoch, blockRoot, signatureBytes);
    }

    private static Block ConvertBlock(TestBlock tb) => new(
        new Slot(tb.Slot),
        tb.ProposerIndex,
        new Bytes32(ParseHex(tb.ParentRoot)),
        new Bytes32(ParseHex(tb.StateRoot)),
        ConvertBlockBody(tb.Body));

    private static BlockBody ConvertBlockBody(TestBlockBody? body)
    {
        if (body?.Attestations?.Data is null or { Count: 0 })
            return new BlockBody(Array.Empty<AggregatedAttestation>());

        var attestations = body.Attestations.Data
            .Select(a => new AggregatedAttestation(
                new AggregationBits(a.AggregationBits.Data),
                ConvertAttestationData(a.Data)))
            .ToList();

        return new BlockBody(attestations);
    }

    private static AttestationData ConvertAttestationData(TestAttestationData td) => new(
        new Slot(td.Slot),
        new Checkpoint(new Bytes32(ParseHex(td.Head.Root)), new Slot(td.Head.Slot)),
        new Checkpoint(new Bytes32(ParseHex(td.Target.Root)), new Slot(td.Target.Slot)),
        new Checkpoint(new Bytes32(ParseHex(td.Source.Root)), new Slot(td.Source.Slot)));

    private static byte[] ParseHex(string hex) =>
        Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);
}
