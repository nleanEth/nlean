using Lean.Crypto;
using Lean.Validator;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Lean.Validator.Tests;

public sealed class ValidatorServiceTests
{
    [Test]
    public async Task StartAsync_InitializesLeanMultiSigContexts()
    {
        var multiSig = new FakeLeanMultiSig();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            new FakeLeanSig(),
            multiSig);

        await service.StartAsync(CancellationToken.None);

        Assert.That(multiSig.SetupProverCalls, Is.EqualTo(1));
        Assert.That(multiSig.SetupVerifierCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task StartAsync_IsIdempotent_WhileRunning()
    {
        var multiSig = new FakeLeanMultiSig();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            new FakeLeanSig(),
            multiSig);

        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        Assert.That(multiSig.SetupProverCalls, Is.EqualTo(1));
        Assert.That(multiSig.SetupVerifierCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task StopAsync_AllowsStartToInitializeAgain()
    {
        var multiSig = new FakeLeanMultiSig();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            new FakeLeanSig(),
            multiSig);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);

        Assert.That(multiSig.SetupProverCalls, Is.EqualTo(2));
        Assert.That(multiSig.SetupVerifierCalls, Is.EqualTo(2));
    }

    private sealed class FakeLeanSig : ILeanSig
    {
        public LeanSigKeyPair GenerateKeyPair(uint activationEpoch, uint numActiveEpochs)
        {
            return new LeanSigKeyPair(Array.Empty<byte>(), Array.Empty<byte>());
        }

        public byte[] Sign(ReadOnlySpan<byte> secretKey, uint epoch, ReadOnlySpan<byte> message)
        {
            return Array.Empty<byte>();
        }

        public bool Verify(ReadOnlySpan<byte> publicKey, uint epoch, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
        {
            return true;
        }
    }

    private sealed class FakeLeanMultiSig : ILeanMultiSig
    {
        public int SetupProverCalls { get; private set; }
        public int SetupVerifierCalls { get; private set; }

        public void SetupProver()
        {
            SetupProverCalls++;
        }

        public void SetupVerifier()
        {
            SetupVerifierCalls++;
        }

        public byte[] AggregateSignatures(IReadOnlyList<ReadOnlyMemory<byte>> publicKeys,
            IReadOnlyList<ReadOnlyMemory<byte>> signatures,
            ReadOnlySpan<byte> message,
            uint epoch)
        {
            return Array.Empty<byte>();
        }

        public bool VerifyAggregate(IReadOnlyList<ReadOnlyMemory<byte>> publicKeys,
            ReadOnlySpan<byte> message,
            ReadOnlySpan<byte> aggregateSignature,
            uint epoch)
        {
            return true;
        }
    }
}
