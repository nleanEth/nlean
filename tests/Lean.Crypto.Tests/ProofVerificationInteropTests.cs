using Lean.Consensus;
using Lean.Crypto;
using NUnit.Framework;

namespace Lean.Crypto.Tests;

public sealed class ProofVerificationInteropTests
{
    [Test]
    public void DumpedNleanBlockProofsVerifyWithVerifierSetupOnly()
    {
        var (payloadPath, keysDir) = ResolveInputs();
        if (!File.Exists(payloadPath) || !Directory.Exists(keysDir))
        {
            Assert.Ignore("Proof interop inputs are missing.");
            return;
        }

        var payload = File.ReadAllBytes(payloadPath);
        var decoder = new SignedBlockWithAttestationGossipDecoder();
        var decodeResult = decoder.DecodeAndValidate(payload);
        Assert.That(decodeResult.IsSuccess, Is.True, decodeResult.Reason);
        Assert.That(decodeResult.SignedBlock, Is.Not.Null);
        var signedBlock = decodeResult.SignedBlock!;

        var multisig = new RustLeanMultiSig();
        multisig.SetupVerifier();

        VerifyAllProofs(multisig, signedBlock, keysDir);
    }

    [Test]
    public void DumpedNleanBlockProofsVerifyWithProverAndVerifierSetup()
    {
        var (payloadPath, keysDir) = ResolveInputs();
        if (!File.Exists(payloadPath) || !Directory.Exists(keysDir))
        {
            Assert.Ignore("Proof interop inputs are missing.");
            return;
        }

        var payload = File.ReadAllBytes(payloadPath);
        var decoder = new SignedBlockWithAttestationGossipDecoder();
        var decodeResult = decoder.DecodeAndValidate(payload);
        Assert.That(decodeResult.IsSuccess, Is.True, decodeResult.Reason);
        Assert.That(decodeResult.SignedBlock, Is.Not.Null);
        var signedBlock = decodeResult.SignedBlock!;

        var multisig = new RustLeanMultiSig();
        multisig.SetupProver();
        multisig.SetupVerifier();

        VerifyAllProofs(multisig, signedBlock, keysDir);
    }

    private static (string PayloadPath, string KeysDir) ResolveInputs()
    {
        var runDir = Environment.GetEnvironmentVariable("NLEAN_QUICKSTART_RUN_DIR")
            ?? "/Users/grapebaba/conductor/workspaces/zeam/lima-v1/lean-quickstart/local-devnet-nlean";

        var payloadPath = Environment.GetEnvironmentVariable("NLEAN_BLOCK_DUMP_PATH")
            ?? Path.Combine(runDir, "data", "nlean_0", "block-debug", "validator-0-slot-000003-block.ssz");

        var keysDir = Environment.GetEnvironmentVariable("NLEAN_GENESIS_KEYS_DIR")
            ?? Path.Combine(runDir, "genesis", "hash-sig-keys");

        return (payloadPath, keysDir);
    }

    private static void VerifyAllProofs(RustLeanMultiSig multisig, Lean.Consensus.Types.SignedBlockWithAttestation signedBlock, string keysDir)
    {
        var attestations = signedBlock.Message.Block.Body.Attestations;
        var proofs = signedBlock.Signature.AttestationSignatures;
        Assert.That(attestations.Count, Is.EqualTo(proofs.Count));

        for (var i = 0; i < proofs.Count; i++)
        {
            var attestation = attestations[i];
            var proof = proofs[i];
            Assert.That(attestation.AggregationBits.TryToValidatorIndices(out var validatorIds), Is.True);
            Assert.That(validatorIds, Is.Not.Null);

            var publicKeys = validatorIds!
                .Select(validatorId => Path.Combine(keysDir, $"validator_{validatorId}_pk.ssz"))
                .Select(path => new ReadOnlyMemory<byte>(File.ReadAllBytes(path)))
                .ToList();

            var messageRoot = attestation.Data.HashTreeRoot();
            var ok = multisig.VerifyAggregate(publicKeys, messageRoot, proof.ProofData, checked((uint)attestation.Data.Slot.Value));
            Assert.That(
                ok,
                Is.True,
                $"aggregate verification failed for attestation index {i}, slot {attestation.Data.Slot.Value}");
        }
    }
}
