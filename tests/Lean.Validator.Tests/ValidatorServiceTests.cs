using Lean.Consensus;
using Lean.Consensus.Types;
using Lean.Crypto;
using Lean.Network;
using Lean.Validator;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Lean.Validator.Tests;

public sealed class ValidatorServiceTests
{
    [Test]
    public async Task StartAsync_DoesNotInitializeLeanMultiSigContexts()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var multiSig = new FakeLeanMultiSig();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 2 },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            multiSig);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.That(multiSig.SetupProverCalls, Is.EqualTo(0));
        Assert.That(multiSig.SetupVerifierCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task StartAsync_IsIdempotent_WhileRunning()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var multiSig = new FakeLeanMultiSig();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 2 },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            multiSig);

        await service.StartAsync(CancellationToken.None);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.That(multiSig.SetupProverCalls, Is.EqualTo(0));
        Assert.That(multiSig.SetupVerifierCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task DutyLoop_TicksUntilStop()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 2 },
            new ValidatorDutyConfig { PublishAggregates = true },
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);

        var observedDutyTick = await WaitUntilAsync(
            () => consensus.CurrentSlotReadCalls > 0,
            TimeSpan.FromSeconds(3));
        Assert.That(observedDutyTick, Is.True);

        await service.StopAsync(CancellationToken.None);
        var readCallsAtStop = consensus.CurrentSlotReadCalls;
        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        Assert.That(consensus.CurrentSlotReadCalls, Is.EqualTo(readCallsAtStop));
    }

    [Test]
    public async Task DutyLoop_PublishesAttestation()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 2 },
            new ValidatorDutyConfig { PublishAggregates = true },
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var published = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.AttestationSubnet(GossipTopics.DefaultNetwork, 0)),
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(published, Is.True);
        Assert.That(network.PublishedMessages.Any(message => message.Topic == GossipTopics.AttestationSubnet("devnet0", 0)), Is.True);
    }

    [Test]
    public async Task DutyLoop_PublishedAttestationIsConsensusDecodable()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 2 },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var published = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.AttestationSubnet(GossipTopics.DefaultNetwork, 0)),
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(published, Is.True);
        var payload = network.PublishedMessages.First(message => message.Topic == GossipTopics.AttestationSubnet(GossipTopics.DefaultNetwork, 0)).Payload;
        var decodeResult = new SignedAttestationGossipDecoder().DecodeAndValidate(payload);
        Assert.That(decodeResult.IsSuccess, Is.True);
        Assert.That(decodeResult.Attestation, Is.Not.Null);
    }

    [Test]
    public async Task DutyLoop_PublishesOffsetSignedAttestationLayout()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig
            {
                SecondsPerSlot = 1,
                EnableGossipProcessing = false,
                InitialValidatorCount = 2
            },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var published = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.AttestationSubnet(GossipTopics.DefaultNetwork, 0)),
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(published, Is.True);
        var payload = network.PublishedMessages.First(message => message.Topic == GossipTopics.AttestationSubnet(GossipTopics.DefaultNetwork, 0)).Payload;
        var fixedSectionLength = SszEncoding.UInt64Length + SszEncoding.AttestationDataLength;
        Assert.That(payload.Length, Is.GreaterThanOrEqualTo(fixedSectionLength + SszEncoding.UInt32Length));

        var signatureOffset = BitConverter.ToUInt32(payload, fixedSectionLength);
        Assert.That(signatureOffset, Is.EqualTo((uint)(fixedSectionLength + SszEncoding.UInt32Length)));
    }

    [Test]
    public async Task DutyLoop_UsesConsensusAttestationDataSourceAndTarget()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 2 },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var published = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.AttestationSubnet(GossipTopics.DefaultNetwork, 0)),
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(published, Is.True);
        var payload = network.PublishedMessages.First(message => message.Topic == GossipTopics.AttestationSubnet(GossipTopics.DefaultNetwork, 0)).Payload;
        var decodeResult = new SignedAttestationGossipDecoder().DecodeAndValidate(payload);
        Assert.That(decodeResult.IsSuccess, Is.True);
        var message = decodeResult.Attestation!.Message;

        Assert.That(message.Head.Root, Is.EqualTo(new Bytes32(Enumerable.Repeat((byte)0x44, 32).ToArray())));
        Assert.That(message.Target.Root, Is.EqualTo(new Bytes32(Enumerable.Repeat((byte)0x55, 32).ToArray())));
        Assert.That(message.Source.Root, Is.EqualTo(new Bytes32(Enumerable.Repeat((byte)0x66, 32).ToArray())));
        Assert.That(message.Target.Slot.Value, Is.EqualTo(0));
        Assert.That(message.Source.Slot.Value, Is.EqualTo(0));
    }

    [Test]
    public async Task DutyLoop_UsesSlotAsXmssEpoch()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var leanSig = new FakeLeanSig();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, SlotsPerEpoch = 32, InitialValidatorCount = 2 },
            new ValidatorDutyConfig(),
            leanSig,
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var published = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.AttestationSubnet(GossipTopics.DefaultNetwork, 0)),
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(published, Is.True);
        Assert.That(leanSig.LastSignEpoch, Is.EqualTo(1U));
    }

    [Test]
    public async Task DutyLoop_SecretOnlyConfiguration_DisablesAggregatePublishing()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var multiSig = new FakeLeanMultiSig();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 2 },
            new ValidatorDutyConfig
            {
                SecretKeyHex = "0x" + new string('A', 64),
                PublishAggregates = true
            },
            new FakeLeanSig(),
            multiSig);

        await service.StartAsync(CancellationToken.None);
        var published = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.AttestationSubnet(GossipTopics.DefaultNetwork, 0)),
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(published, Is.True);
        Assert.That(network.PublishedMessages.Any(message => message.Topic == GossipTopics.Aggregates), Is.False);
        Assert.That(multiSig.AggregateCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task DutyLoop_ProposerSlot_PublishesBlockAndSkipsStandaloneAttestation()
    {
        var consensus = new FakeConsensusService { CurrentSlotValue = 1 };
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 3 },
            new ValidatorDutyConfig
            {
                ValidatorIndex = 1,
                GenesisValidatorPublicKeys = new[]
                {
                    HexRepeat(0x11, 52),
                    HexRepeat(0x22, 52),
                    HexRepeat(0x33, 52)
                }
            },
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var publishedBlock = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.Blocks),
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(publishedBlock, Is.True);
        Assert.That(consensus.TryApplyLocalBlockCalls, Is.GreaterThan(0));
        Assert.That(consensus.TryApplyLocalAttestationCalls, Is.EqualTo(0));
        Assert.That(network.PublishedMessages.Any(message => message.Topic == GossipTopics.AttestationSubnet(GossipTopics.DefaultNetwork, 0)), Is.False);

        var payload = network.PublishedMessages.First(message => message.Topic == GossipTopics.Blocks).Payload;
        var decodeResult = new SignedBlockWithAttestationGossipDecoder().DecodeAndValidate(payload);
        Assert.That(decodeResult.IsSuccess, Is.True);
        Assert.That(decodeResult.SignedBlock, Is.Not.Null);

        var signatureOffset = BitConverter.ToUInt32(payload, SszEncoding.UInt32Length);
        var signatureSection = payload.AsSpan((int)signatureOffset).ToArray();
        var attestationSignaturesOffset = BitConverter.ToUInt32(signatureSection, 0);
        Assert.That(attestationSignaturesOffset, Is.GreaterThanOrEqualTo((uint)(SszEncoding.UInt32Length * 2)));
    }

    [Test]
    public async Task DutyLoop_ProposerSlot_UsesDualOffsetSignatureLayout()
    {
        var consensus = new FakeConsensusService { CurrentSlotValue = 1 };
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig
            {
                SecondsPerSlot = 1,
                EnableGossipProcessing = false,
                InitialValidatorCount = 3
            },
            new ValidatorDutyConfig
            {
                ValidatorIndex = 1,
                GenesisValidatorPublicKeys = new[]
                {
                    HexRepeat(0x11, 52),
                    HexRepeat(0x22, 52),
                    HexRepeat(0x33, 52)
                }
            },
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var publishedBlock = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.Blocks),
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(publishedBlock, Is.True);
        var payload = network.PublishedMessages.First(message => message.Topic == GossipTopics.Blocks).Payload;
        var signatureOffset = BitConverter.ToUInt32(payload, SszEncoding.UInt32Length);
        var signatureSection = payload.AsSpan((int)signatureOffset).ToArray();
        var attestationSignaturesOffset = BitConverter.ToUInt32(signatureSection, 0);
        Assert.That(attestationSignaturesOffset, Is.EqualTo((uint)(SszEncoding.UInt32Length * 2)));
    }

    [Test]
    public async Task DutyLoop_NonProposerSlot_DoesNotPublishBlock()
    {
        var consensus = new FakeConsensusService { CurrentSlotValue = 2 };
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 3 },
            new ValidatorDutyConfig { ValidatorIndex = 1 },
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var publishedAttestation = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.AttestationSubnet(GossipTopics.DefaultNetwork, 0)),
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(publishedAttestation, Is.True);
        Assert.That(network.PublishedMessages.Any(message => message.Topic == GossipTopics.Blocks), Is.False);
        Assert.That(consensus.TryApplyLocalBlockCalls, Is.EqualTo(0));
        Assert.That(consensus.TryApplyLocalAttestationCalls, Is.GreaterThan(0));
    }

    [Test]
    public async Task DutyLoop_WhenLocalAttestationRejected_DoesNotPublishAttestation()
    {
        var consensus = new FakeConsensusService
        {
            CurrentSlotValue = 2,
            LocalAttestationApplyResult = false
        };
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 3 },
            new ValidatorDutyConfig { ValidatorIndex = 1 },
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var attemptedLocalApply = await WaitUntilAsync(
            () => consensus.TryApplyLocalAttestationCalls > 0,
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(attemptedLocalApply, Is.True);
        Assert.That(network.PublishedMessages.Any(message => message.Topic == GossipTopics.AttestationSubnet(GossipTopics.DefaultNetwork, 0)), Is.False);
    }

    [Test]
    public async Task DutyLoop_WhenUnknownRootRecoveryInFlight_SkipsVotingAndProposing()
    {
        var consensus = new FakeConsensusService
        {
            CurrentSlotValue = 1,
            HasUnknownBlockRootsInFlightValue = true
        };
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 3 },
            new ValidatorDutyConfig { ValidatorIndex = 1 },
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var dutyLoopObserved = await WaitUntilAsync(
            () => consensus.CurrentSlotReadCalls > 0,
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(dutyLoopObserved, Is.True);
        Assert.That(network.PublishedMessages.Any(message => message.Topic == GossipTopics.Blocks), Is.False);
        Assert.That(network.PublishedMessages.Any(message => message.Topic == GossipTopics.AttestationSubnet(GossipTopics.DefaultNetwork, 0)), Is.False);
        Assert.That(consensus.TryApplyLocalBlockCalls, Is.EqualTo(0));
        Assert.That(consensus.TryApplyLocalAttestationCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task DutyLoop_WhenSlotJumps_ProcessesIntermediateSlotsAndPublishesProposerBlock()
    {
        var consensus = new FakeConsensusService { CurrentSlotValue = 5 };
        consensus.EnqueueCurrentSlots(1, 5);
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 3 },
            new ValidatorDutyConfig { ValidatorIndex = 0 },
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var publishedBlock = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.Blocks),
            TimeSpan.FromSeconds(4));
        await service.StopAsync(CancellationToken.None);

        Assert.That(publishedBlock, Is.True);
        Assert.That(consensus.TryApplyLocalBlockCalls, Is.GreaterThan(0));
    }

    [Test]
    public async Task DutyLoop_SkipsGenesisSlotProposal()
    {
        var consensus = new FakeConsensusService { CurrentSlotValue = 0 };
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 3 },
            new ValidatorDutyConfig { ValidatorIndex = 0 },
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var publishedAttestation = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.AttestationSubnet(GossipTopics.DefaultNetwork, 0)),
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(publishedAttestation, Is.True);
        Assert.That(network.PublishedMessages.Any(message => message.Topic == GossipTopics.Blocks), Is.False);
    }

    [Test]
    public async Task DutyLoop_ProposerSlot_SkipsInvalidFallbackProofLearnedFromBlockGossip()
    {
        var consensus = new FakeConsensusService { CurrentSlotValue = 10 };
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 3 },
            new ValidatorDutyConfig { ValidatorIndex = 1 },
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);

        var attestationData = new AttestationData(
            new Slot(9),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0x44, 32).ToArray()), new Slot(1)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0x55, 32).ToArray()), new Slot(0)),
            new Checkpoint(new Bytes32(Enumerable.Repeat((byte)0x66, 32).ToArray()), new Slot(0)));
        var fallbackProof = new AggregatedSignatureProof(
            new AggregationBits(new[] { true, false, true }),
            new byte[] { 0xAB, 0xCD, 0xEF });
        var fallbackSignedBlock = BuildSignedBlockWithAggregateProof(attestationData, fallbackProof);
        network.Emit(GossipTopics.Blocks, SszEncoding.Encode(fallbackSignedBlock));

        var publishedBlock = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.Blocks),
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(publishedBlock, Is.True);
        var decoder = new SignedBlockWithAttestationGossipDecoder();
        var fallbackProofIncluded = false;
        foreach (var payload in network.PublishedMessages.Where(message => message.Topic == GossipTopics.Blocks).Select(message => message.Payload))
        {
            var decodeResult = decoder.DecodeAndValidate(payload);
            Assert.That(decodeResult.IsSuccess, Is.True);
            Assert.That(decodeResult.SignedBlock, Is.Not.Null);

            var published = decodeResult.SignedBlock!;
            Assert.That(published.Message.Block.Body.Attestations.Count, Is.EqualTo(published.Signature.AttestationSignatures.Count));
            if (published.Signature.AttestationSignatures.Any(
                    proof => proof.ProofData.AsSpan().SequenceEqual(fallbackProof.ProofData)))
            {
                fallbackProofIncluded = true;
                break;
            }
        }

        Assert.That(fallbackProofIncluded, Is.False);
    }

    [Test]
    public async Task DutyLoop_ProposerSlot_UsesConsensusAggregatesWithoutSelfAggregation()
    {
        var consensus = new FakeConsensusService { CurrentSlotValue = 3 };
        consensus.EnqueueCurrentSlots(2, 3);
        var network = new FakeNetworkService();
        var multiSig = new FakeLeanMultiSig();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 3 },
            new ValidatorDutyConfig
            {
                ValidatorIndex = 0,
                GenesisValidatorPublicKeys = new[]
                {
                    HexRepeat(0x11, 52),
                    HexRepeat(0x22, 52),
                    HexRepeat(0x33, 52)
                }
            },
            new FakeLeanSig(),
            multiSig);

        var slotTwoData = consensus.CreateAttestationData(2);
        var expectedProof = new AggregatedSignatureProof(
            new AggregationBits(new[] { true, true, false }),
            new byte[] { 0xCA, 0xFE, 0xBA, 0xBE });
        consensus.KnownAggregatedPayloads = (
            new[]
            {
                new AggregatedAttestation(expectedProof.Participants, slotTwoData)
            },
            new[]
            {
                expectedProof
            });

        await service.StartAsync(CancellationToken.None);

        var publishedBlock = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.Blocks),
            TimeSpan.FromSeconds(4));
        await service.StopAsync(CancellationToken.None);

        Assert.That(publishedBlock, Is.True);
        Assert.That(consensus.GetKnownAggregatedPayloadsCalls, Is.GreaterThan(0));
        Assert.That(multiSig.AggregateCalls, Is.EqualTo(0));

        var decoder = new SignedBlockWithAttestationGossipDecoder();
        var proposedPayload = network.PublishedMessages
            .Where(message => message.Topic == GossipTopics.Blocks)
            .Select(message => message.Payload)
            .First(payload =>
            {
                var decode = decoder.DecodeAndValidate(payload);
                return decode.IsSuccess && decode.SignedBlock?.Message.Block.Slot.Value == 3;
            });
        var proposed = decoder.DecodeAndValidate(proposedPayload).SignedBlock!;
        Assert.That(
            proposed.Signature.AttestationSignatures.Any(proof =>
                proof.ProofData.AsSpan().SequenceEqual(expectedProof.ProofData)),
            Is.True);
    }

    [Test]
    public async Task DutyLoop_ProposerSlot_DeduplicatesAttestationMessagesAcrossProofSources()
    {
        var consensus = new FakeConsensusService { CurrentSlotValue = 3 };
        consensus.EnqueueCurrentSlots(2, 3);
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 3 },
            new ValidatorDutyConfig
            {
                ValidatorIndex = 0,
                GenesisValidatorPublicKeys = new[]
                {
                    HexRepeat(0x11, 52),
                    HexRepeat(0x22, 52),
                    HexRepeat(0x33, 52)
                }
            },
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        var slotTwoData = consensus.CreateAttestationData(2);
        var largerProofForSameData = new AggregatedSignatureProof(
            new AggregationBits(new[] { true, true, false }),
            new byte[] { 0xFA, 0x11, 0xBA, 0xCC });
        var smallerProofForSameData = new AggregatedSignatureProof(
            new AggregationBits(new[] { false, true, false }),
            new byte[] { 0xDD, 0xEE });
        var otherData = slotTwoData with
        {
            Slot = new Slot(1)
        };
        var otherProof = new AggregatedSignatureProof(
            new AggregationBits(new[] { true, false, false }),
            new byte[] { 0x11, 0x22, 0x33 });
        consensus.KnownAggregatedPayloads = (
            new[]
            {
                new AggregatedAttestation(largerProofForSameData.Participants, slotTwoData),
                new AggregatedAttestation(smallerProofForSameData.Participants, slotTwoData),
                new AggregatedAttestation(otherProof.Participants, otherData)
            },
            new[]
            {
                largerProofForSameData,
                smallerProofForSameData,
                otherProof
            });

        await service.StartAsync(CancellationToken.None);

        var publishedBlock = await WaitUntilAsync(
            () => network.PublishedMessages.Any(message => message.Topic == GossipTopics.Blocks),
            TimeSpan.FromSeconds(4));
        await service.StopAsync(CancellationToken.None);

        Assert.That(publishedBlock, Is.True);

        var decoder = new SignedBlockWithAttestationGossipDecoder();
        var sawProposedBlock = false;
        foreach (var payload in network.PublishedMessages
                     .Where(message => message.Topic == GossipTopics.Blocks)
                     .Select(message => message.Payload))
        {
            var decodeResult = decoder.DecodeAndValidate(payload);
            Assert.That(decodeResult.IsSuccess, Is.True);
            Assert.That(decodeResult.SignedBlock, Is.Not.Null);

            var published = decodeResult.SignedBlock!;
            if (published.Message.Block.Slot.Value != 3)
            {
                continue;
            }

            sawProposedBlock = true;
            var dataRoots = published.Message.Block.Body.Attestations
                .Select(attestation => Convert.ToHexString(attestation.Data.HashTreeRoot()))
                .ToList();
            Assert.That(dataRoots.Count, Is.EqualTo(dataRoots.Distinct(StringComparer.Ordinal).Count()));
        }

        Assert.That(sawProposedBlock, Is.True);
    }

    [Test]
    public async Task StopAsync_AllowsStartToInitializeAgain()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var multiSig = new FakeLeanMultiSig();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 2 },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            multiSig);

        await service.StartAsync(CancellationToken.None);
        var firstLifecycleTicked = await WaitUntilAsync(
            () => consensus.CurrentSlotReadCalls > 0,
            TimeSpan.FromSeconds(3));
        Assert.That(firstLifecycleTicked, Is.True);

        await service.StopAsync(CancellationToken.None);
        var readCallsAfterFirstLifecycle = consensus.CurrentSlotReadCalls;
        await service.StartAsync(CancellationToken.None);
        var secondLifecycleTicked = await WaitUntilAsync(
            () => consensus.CurrentSlotReadCalls > readCallsAfterFirstLifecycle,
            TimeSpan.FromSeconds(3));
        await service.StopAsync(CancellationToken.None);

        Assert.That(secondLifecycleTicked, Is.True);
        Assert.That(multiSig.SetupProverCalls, Is.EqualTo(0));
        Assert.That(multiSig.SetupVerifierCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task StopAsync_IsIdempotent()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 2 },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task StopAsync_WithCancelledToken_StillStopsDutyLoop()
    {
        var consensus = new FakeConsensusService();
        var network = new FakeNetworkService();
        var service = new ValidatorService(
            NullLogger<ValidatorService>.Instance,
            consensus,
            network,
            new ConsensusConfig { SecondsPerSlot = 1, EnableGossipProcessing = false, InitialValidatorCount = 2 },
            new ValidatorDutyConfig(),
            new FakeLeanSig(),
            new FakeLeanMultiSig());

        await service.StartAsync(CancellationToken.None);
        var observedDutyTick = await WaitUntilAsync(
            () => consensus.CurrentSlotReadCalls > 0,
            TimeSpan.FromSeconds(3));
        Assert.That(observedDutyTick, Is.True);

        using var cancelledStopToken = new CancellationTokenSource();
        cancelledStopToken.Cancel();
        await service.StopAsync(cancelledStopToken.Token);

        var readCallsAtStop = consensus.CurrentSlotReadCalls;
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        Assert.That(consensus.CurrentSlotReadCalls, Is.EqualTo(readCallsAtStop));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var startedAt = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - startedAt > timeout)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        return true;
    }

    private static SignedBlockWithAttestation BuildSignedBlockWithAggregateProof(
        AttestationData attestationData,
        AggregatedSignatureProof proof)
    {
        var block = new Block(
            new Slot(9),
            2,
            new Bytes32(Enumerable.Repeat((byte)0x99, 32).ToArray()),
            Bytes32.Zero(),
            new BlockBody(new[]
            {
                new AggregatedAttestation(
                    new AggregationBits(new[] { true, false, true }),
                    attestationData)
            }));

        return new SignedBlockWithAttestation(
            new BlockWithAttestation(
                block,
                new Attestation(2, attestationData)),
            new BlockSignatures(
                new[] { proof },
                XmssSignature.Empty()));
    }

    private static string HexRepeat(byte value, int length)
    {
        return Convert.ToHexString(Enumerable.Repeat(value, length).ToArray());
    }

    private sealed class FakeLeanSig : ILeanSig
    {
        public uint LastSignEpoch { get; private set; }

        public LeanSigKeyPair GenerateKeyPair(uint activationEpoch, uint numActiveEpochs)
        {
            return new LeanSigKeyPair(
                Enumerable.Repeat((byte)0x11, 64).ToArray(),
                Enumerable.Repeat((byte)0x22, 128).ToArray());
        }

        public byte[] Sign(ReadOnlySpan<byte> secretKey, uint epoch, ReadOnlySpan<byte> message)
        {
            LastSignEpoch = epoch;
            return XmssSignature.Empty().EncodeBytes();
        }

        public bool Verify(ReadOnlySpan<byte> publicKey, uint epoch, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
        {
            return true;
        }
    }

    private sealed class FakeConsensusService : IConsensusService
    {
        private readonly Queue<ulong> _currentSlotSequence = new();
        private readonly object _slotSequenceLock = new();
        private long _currentSlotReadCalls;
        private readonly Bytes32 _headRoot = new(Enumerable.Repeat((byte)0x44, 32).ToArray());
        private readonly Bytes32 _targetRoot = new(Enumerable.Repeat((byte)0x55, 32).ToArray());
        private readonly Bytes32 _sourceRoot = new(Enumerable.Repeat((byte)0x66, 32).ToArray());

        public long CurrentSlotReadCalls => Interlocked.Read(ref _currentSlotReadCalls);
        public int TryApplyLocalBlockCalls { get; private set; }
        public int TryApplyLocalAttestationCalls { get; private set; }
        public int TryComputeBlockStateRootCalls { get; private set; }
        public int GetKnownAggregatedPayloadsCalls { get; private set; }
        public ulong CurrentSlotValue { get; set; } = 1;
        public bool HasUnknownBlockRootsInFlightValue { get; set; }
        public bool LocalBlockApplyResult { get; set; } = true;
        public bool LocalAttestationApplyResult { get; set; } = true;
        public (IReadOnlyList<AggregatedAttestation> Attestations, IReadOnlyList<AggregatedSignatureProof> Proofs)
            KnownAggregatedPayloads { get; set; } =
                (Array.Empty<AggregatedAttestation>(), Array.Empty<AggregatedSignatureProof>());

        public ulong CurrentSlot
        {
            get
            {
                Interlocked.Increment(ref _currentSlotReadCalls);
                lock (_slotSequenceLock)
                {
                    if (_currentSlotSequence.Count > 0)
                    {
                        CurrentSlotValue = _currentSlotSequence.Dequeue();
                    }
                }

                return CurrentSlotValue;
            }
        }

        public void EnqueueCurrentSlots(params ulong[] slots)
        {
            lock (_slotSequenceLock)
            {
                foreach (var slot in slots)
                {
                    _currentSlotSequence.Enqueue(slot);
                }
            }
        }

        public ulong HeadSlot => 1;

        public ulong JustifiedSlot => 0;

        public ulong FinalizedSlot => 0;

        public bool HasUnknownBlockRootsInFlight => HasUnknownBlockRootsInFlightValue;

        public byte[] HeadRoot => _headRoot.AsSpan().ToArray();

        public byte[] GetProposalHeadRoot()
        {
            return HeadRoot;
        }

        public AttestationData CreateAttestationData(ulong slot)
        {
            return new AttestationData(
                new Slot(slot),
                new Checkpoint(_headRoot, new Slot(1)),
                new Checkpoint(_targetRoot, new Slot(0)),
                new Checkpoint(_sourceRoot, new Slot(0)));
        }

        public bool TryComputeBlockStateRoot(Block candidateBlock, out Bytes32 stateRoot, out string reason)
        {
            TryComputeBlockStateRootCalls++;
            stateRoot = Bytes32.Zero();
            reason = string.Empty;
            return true;
        }

        public bool TryApplyLocalBlock(SignedBlockWithAttestation signedBlock, out string reason)
        {
            TryApplyLocalBlockCalls++;
            reason = LocalBlockApplyResult ? string.Empty : "rejected";
            return LocalBlockApplyResult;
        }

        public bool TryApplyLocalAttestation(SignedAttestation signedAttestation, out string reason)
        {
            TryApplyLocalAttestationCalls++;
            reason = LocalAttestationApplyResult ? string.Empty : "rejected";
            return LocalAttestationApplyResult;
        }

        public bool TryApplyLocalAggregatedAttestation(SignedAggregatedAttestation signed, out string reason)
        {
            reason = string.Empty;
            return true;
        }

        public (IReadOnlyList<AggregatedAttestation> Attestations, IReadOnlyList<AggregatedSignatureProof> Proofs) GetKnownAggregatedPayloadsForBlock(ulong slot, Checkpoint requiredSource)
        {
            GetKnownAggregatedPayloadsCalls++;
            return KnownAggregatedPayloads;
        }

        public List<(AttestationData Data, List<ulong> ValidatorIds, List<XmssSignature> Signatures)> CollectAttestationsForAggregation()
        {
            return new List<(AttestationData Data, List<ulong> ValidatorIds, List<XmssSignature> Signatures)>();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLeanMultiSig : ILeanMultiSig
    {
        public int SetupProverCalls { get; private set; }
        public int SetupVerifierCalls { get; private set; }
        public int AggregateCalls { get; private set; }
        public List<IReadOnlyList<byte[]>> AggregatePublicKeyHistory { get; } = new();

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
            AggregateCalls++;
            AggregatePublicKeyHistory.Add(publicKeys.Select(key => key.ToArray()).ToList());
            return new byte[] { 0xAA, 0xBB, 0xCC };
        }

        public bool VerifyAggregate(IReadOnlyList<ReadOnlyMemory<byte>> publicKeys,
            ReadOnlySpan<byte> message,
            ReadOnlySpan<byte> aggregateSignature,
            uint epoch)
        {
            return true;
        }
    }

    private sealed class FakeNetworkService : INetworkService
    {
        public List<(string Topic, byte[] Payload)> PublishedMessages { get; } = new();
        private readonly Dictionary<string, List<Action<byte[]>>> _subscriptions = new(StringComparer.Ordinal);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            PublishedMessages.Add((topic, payload.ToArray()));
            return Task.CompletedTask;
        }

        public Task SubscribeAsync(string topic, Action<byte[]> handler, CancellationToken cancellationToken = default)
        {
            if (!_subscriptions.TryGetValue(topic, out var handlers))
            {
                handlers = new List<Action<byte[]>>();
                _subscriptions[topic] = handlers;
            }

            handlers.Add(handler);
            return Task.CompletedTask;
        }

        public void Emit(string topic, byte[] payload)
        {
            if (!_subscriptions.TryGetValue(topic, out var handlers))
            {
                return;
            }

            foreach (var handler in handlers)
            {
                handler(payload);
            }
        }

        public Task<byte[]?> RequestBlockByRootAsync(ReadOnlyMemory<byte> blockRoot, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<byte[]?>(null);
        }

        public Task<byte[]?> RequestBlockByRootAsync(
            ReadOnlyMemory<byte> blockRoot,
            string preferredPeerKey,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<byte[]?>(null);
        }

        public Task ProbePeerStatusesAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ConnectToPeersAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<List<(byte[] Root, byte[] Payload)>> RequestBlocksByRootBatchAsync(
            List<byte[]> roots, string? preferredPeerKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new List<(byte[] Root, byte[] Payload)>());
        }
    }
}
