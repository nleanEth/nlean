using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Security.Cryptography;
using Lean.Consensus;
using Lean.Consensus.Types;
using Lean.Crypto;
using Lean.Metrics;
using Lean.Network;
using Microsoft.Extensions.Logging;

namespace Lean.Validator;

public sealed class ValidatorService : IValidatorService
{
    // Keep block gossip payloads comfortably below 1 MiB across mixed-client devnets.
    // Individual aggregate proofs are often ~250 KiB, so 3 proofs keeps the full block SSZ size bounded.
    private const int MaxAggregatedProofsPerBlock = 3;

    private static readonly Meter ValidatorMeter = new("Lean.Validator");
    private static readonly Counter<long> DutyRunsTotal = ValidatorMeter.CreateCounter<long>(
        "lean_validator_duty_runs_total",
        description: "Total number of validator duty loop ticks executed.");
    private static readonly bool DumpAttestationsEnabled = IsTruthyEnvironmentValue("NLEAN_DEBUG_DUMP_ATTESTATIONS");
    private static readonly bool DumpBlocksEnabled = IsTruthyEnvironmentValue("NLEAN_DEBUG_DUMP_BLOCKS");
    private static readonly bool DumpObservedBlocksEnabled = IsTruthyEnvironmentValue("NLEAN_DEBUG_DUMP_OBSERVED_BLOCKS");
    private static readonly string DumpAttestationsDirectory =
        ResolveDumpDirectory("NLEAN_DEBUG_DUMP_DIR", "nlean-attestations");
    private static readonly string DumpBlocksDirectory =
        ResolveDumpDirectory("NLEAN_DEBUG_DUMP_BLOCK_DIR", "nlean-blocks");
    private static readonly string DumpObservedBlocksDirectory =
        ResolveDumpDirectory("NLEAN_DEBUG_DUMP_OBSERVED_BLOCKS_DIR", "nlean-observed-blocks");

    private readonly ILogger<ValidatorService> _logger;
    private readonly IConsensusService _consensusService;
    private readonly INetworkService _networkService;
    private readonly IGossipTopicProvider _gossipTopics;
    private readonly ConsensusConfig _consensusConfig;
    private readonly ValidatorDutyConfig _validatorDutyConfig;
    private readonly ILeanSig _leanSig;
    private readonly ILeanMultiSig _leanMultiSig;
    private readonly SignedAttestationGossipDecoder _signedAttestationDecoder = new();
    private readonly SignedBlockWithAttestationGossipDecoder _signedBlockDecoder = new();
    private readonly Dictionary<ulong, List<ValidatorSignature>> _slotSignatures = new();
    private readonly Dictionary<ulong, byte[]> _validatorPublicKeys = new();
    private readonly Dictionary<string, ObservedAttestationGroup> _observedAttestationGroups = new(StringComparer.Ordinal);
    private readonly object _lifecycleLock = new();
    private readonly object _dutyStateLock = new();
    private CancellationTokenSource? _dutyLoopCts;
    private Task? _dutyLoopTask;
    private int _started;
    private byte[] _validatorPublicKey = Array.Empty<byte>();
    private byte[] _validatorSecretKey = Array.Empty<byte>();
    private ulong _validatorId;
    private ulong _validatorCount;
    private bool _aggregateEnabled;
    private int _observedBlockDumpCounter;

    public ValidatorService(
        ILogger<ValidatorService> logger,
        IConsensusService consensusService,
        INetworkService networkService,
        ConsensusConfig consensusConfig,
        ValidatorDutyConfig validatorDutyConfig,
        ILeanSig leanSig,
        ILeanMultiSig leanMultiSig,
        IGossipTopicProvider? gossipTopics = null)
    {
        _logger = logger;
        _consensusService = consensusService;
        _networkService = networkService;
        _gossipTopics = gossipTopics ?? new GossipTopicProvider(GossipTopics.DefaultNetwork);
        _consensusConfig = consensusConfig;
        _validatorDutyConfig = validatorDutyConfig;
        _leanSig = leanSig;
        _leanMultiSig = leanMultiSig;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        try
        {
            InitializeValidatorKeyMaterial();
            await SubscribeBlockTopicsAsync(cancellationToken);
            await SubscribeAttestationTopicsAsync(cancellationToken);

            lock (_lifecycleLock)
            {
                _dutyLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _dutyLoopTask = RunDutyLoopAsync(_dutyLoopCts.Token);
            }
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            throw;
        }

        _logger.LogInformation(
            "Validator service started. SecondsPerSlot: {SecondsPerSlot}, GenesisTimeUnix: {GenesisTimeUnix}",
            Math.Max(1, _consensusConfig.SecondsPerSlot),
            _consensusConfig.GenesisTimeUnix);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        CancellationTokenSource? dutyLoopCts;
        Task? dutyLoopTask;
        lock (_lifecycleLock)
        {
            dutyLoopCts = _dutyLoopCts;
            dutyLoopTask = _dutyLoopTask;
            _dutyLoopCts = null;
            _dutyLoopTask = null;
        }

        if (dutyLoopCts is not null)
        {
            dutyLoopCts.Cancel();
            dutyLoopCts.Dispose();
        }

        if (dutyLoopTask is not null)
        {
            try
            {
                await dutyLoopTask;
            }
            catch (OperationCanceledException)
            {
                // Shutdown path.
            }
        }

        _logger.LogInformation("Validator service stopped.");
    }

    private async Task RunDutyLoopAsync(CancellationToken cancellationToken)
    {
        if (_consensusConfig.GenesisTimeUnix > 0)
        {
            await WaitForGenesisAsync(cancellationToken);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        ulong? lastCatchupProcessedSlot = null;
        ulong? lastAttestedSlot = null;
        ulong? lastProposerAttemptSlot = null;
        ulong? lastPublishedProposerSlot = null;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var currentSlot = _consensusService.CurrentSlot;
                if (!TryGetCurrentIntervalInSlot(out var intervalInSlot))
                {
                    // Slot catch-up mode for harnesses without wall-clock genesis configuration.
                    var nextSlot = lastCatchupProcessedSlot.HasValue
                        ? lastCatchupProcessedSlot.Value + 1
                        : currentSlot;
                    if (currentSlot < nextSlot)
                    {
                        continue;
                    }

                    for (var catchupSlot = nextSlot; catchupSlot <= currentSlot; catchupSlot++)
                    {
                        try
                        {
                            await ExecuteSlotDutyAsync(catchupSlot, cancellationToken);
                            lastCatchupProcessedSlot = catchupSlot;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Validator duty execution failed in slot catch-up mode.");
                        }
                    }

                    continue;
                }

                var slot = currentSlot;

                if (intervalInSlot == 0 && lastProposerAttemptSlot != slot)
                {
                    lastProposerAttemptSlot = slot;

                    // Match ream/zeam behavior: skip proposing at genesis slot 0.
                    if (slot > 0 && IsProposerSlot(slot))
                    {
                        var publishedBlock = await TryPublishProposerBlockAsync(slot, cancellationToken);
                        if (publishedBlock)
                        {
                            // Proposer attestation is bundled in the block wrapper.
                            lastPublishedProposerSlot = slot;
                            lastAttestedSlot = slot;
                            DutyRunsTotal.Add(1);
                            PruneOldSlots(slot);
                            continue;
                        }

                        _logger.LogWarning(
                            "Failed to publish proposer block for slot {Slot}; falling back to standalone attestation.",
                            slot);
                    }
                }

                if (intervalInSlot >= 1 && lastAttestedSlot != slot)
                {
                    var proposerAttestedInBlock = lastPublishedProposerSlot == slot;
                    if (!proposerAttestedInBlock)
                    {
                        try
                        {
                            await PublishStandaloneAttestationAsync(slot, cancellationToken);
                            DutyRunsTotal.Add(1);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Validator attestation duty execution failed.");
                        }
                    }

                    lastAttestedSlot = slot;
                }

                PruneOldSlots(slot);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path.
        }
    }

    private bool TryGetCurrentIntervalInSlot(out int intervalInSlot)
    {
        var genesisUnix = (long)_consensusConfig.GenesisTimeUnix;
        if (genesisUnix <= 0)
        {
            intervalInSlot = 0;
            return false;
        }

        var secondsPerSlot = Math.Max(1, _consensusConfig.SecondsPerSlot);
        var slotDurationMs = checked(secondsPerSlot * 1000L);
        var intervalDurationMs = Math.Max(1L, slotDurationMs / ForkChoiceStore.IntervalsPerSlot);

        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var elapsedMs = nowUnixMs - checked(genesisUnix * 1000L);
        if (elapsedMs < 0)
        {
            intervalInSlot = 0;
            return true;
        }

        var elapsedInSlotMs = elapsedMs % slotDurationMs;
        var interval = (int)(elapsedInSlotMs / intervalDurationMs);
        intervalInSlot = Math.Clamp(interval, 0, ForkChoiceStore.IntervalsPerSlot - 1);
        return true;
    }

    private async Task WaitForGenesisAsync(CancellationToken cancellationToken)
    {
        var genesis = (long)_consensusConfig.GenesisTimeUnix;
        if (genesis <= 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now >= genesis)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(genesis - now), cancellationToken);
    }

    private async Task PublishStandaloneAttestationAsync(ulong slot, CancellationToken cancellationToken)
    {
        var attestationData = _consensusService.CreateAttestationData(slot);
        var headSlot = attestationData.Head.Slot.Value;
        var justifiedSlot = _consensusService.JustifiedSlot;
        var finalizedSlot = _consensusService.FinalizedSlot;
        var epoch = ToSignatureEpoch(slot);

        _logger.LogInformation(
            "Attestation checkpoint tuple. Slot: {Slot}, ValidatorId: {ValidatorId}, SourceSlot: {SourceSlot}, TargetSlot: {TargetSlot}, JustifiedSlot: {JustifiedSlot}, FinalizedSlot: {FinalizedSlot}, SourceRoot: {SourceRoot}, TargetRoot: {TargetRoot}",
            slot,
            _validatorId,
            attestationData.Source.Slot.Value,
            attestationData.Target.Slot.Value,
            justifiedSlot,
            finalizedSlot,
            Convert.ToHexString(attestationData.Source.Root.AsSpan()),
            Convert.ToHexString(attestationData.Target.Root.AsSpan()));

        var messageRoot = attestationData.HashTreeRoot();
        var signingStopwatch = Stopwatch.StartNew();
        var signatureBytes = _leanSig.Sign(_validatorSecretKey, epoch, messageRoot);
        signingStopwatch.Stop();
        LeanMetrics.RecordPqAttestationSigning(signingStopwatch.Elapsed);

        var selfVerificationOk = true;
        if (_validatorPublicKey.Length > 0)
        {
            var verificationStopwatch = Stopwatch.StartNew();
            selfVerificationOk = _leanSig.Verify(_validatorPublicKey, epoch, messageRoot, signatureBytes);
            verificationStopwatch.Stop();
            LeanMetrics.RecordPqAttestationVerification(verificationStopwatch.Elapsed);
        }

        var signature = XmssSignature.FromBytes(signatureBytes);
        var signedAttestation = new SignedAttestation(_validatorId, attestationData, signature);
        RecordObservedAttestation(signedAttestation);
        if (!_consensusService.TryApplyLocalAttestation(signedAttestation, out var localAttestationReason))
        {
            _logger.LogWarning(
                "Local attestation rejected by consensus. Slot: {Slot}, ValidatorId: {ValidatorId}, Reason: {Reason}",
                slot,
                _validatorId,
                localAttestationReason);
        }

        var attestationPayload = SszEncoding.Encode(signedAttestation);
        await PublishToTopicAsync(_gossipTopics.AttestationTopic, attestationPayload, cancellationToken);
        TrackSignature(slot, messageRoot, epoch, signatureBytes);

        if (_aggregateEnabled)
        {
            await PublishAggregateAsync(slot, messageRoot, epoch, cancellationToken);
        }

        if (!selfVerificationOk)
        {
            _logger.LogWarning(
                "Local attestation signature self-verification failed. Slot: {Slot}, ValidatorId: {ValidatorId}",
                slot,
                _validatorId);
        }

        _logger.LogDebug(
            "Attestation signed. Slot: {Slot}, ValidatorId: {ValidatorId}, HeadSlot: {HeadSlot}, TargetSlot: {TargetSlot}, SourceSlot: {SourceSlot}, HeadRoot: {HeadRoot}, TargetRoot: {TargetRoot}, SourceRoot: {SourceRoot}, MessageRoot: {MessageRoot}, SignatureBytes: {SignatureBytes}, SelfVerified: {SelfVerified}",
            slot,
            _validatorId,
            attestationData.Head.Slot.Value,
            attestationData.Target.Slot.Value,
            attestationData.Source.Slot.Value,
            Convert.ToHexString(attestationData.Head.Root.AsSpan()),
            Convert.ToHexString(attestationData.Target.Root.AsSpan()),
            Convert.ToHexString(attestationData.Source.Root.AsSpan()),
            Convert.ToHexString(messageRoot),
            signatureBytes.Length,
            selfVerificationOk);

        TryDumpAttestation(slot, attestationPayload, messageRoot, signatureBytes, selfVerificationOk);

        _logger.LogDebug(
            "Executed validator attestation duty for slot {Slot}. HeadSlot: {HeadSlot}, ValidatorId: {ValidatorId}",
            slot,
            headSlot,
            _validatorId);
    }

    private async Task ExecuteSlotDutyAsync(ulong slot, CancellationToken cancellationToken)
    {
        if (slot > 0 && IsProposerSlot(slot))
        {
            var publishedBlock = await TryPublishProposerBlockAsync(slot, cancellationToken);
            if (publishedBlock)
            {
                PruneOldSlots(slot);
                DutyRunsTotal.Add(1);
                return;
            }

            _logger.LogWarning(
                "Failed to publish proposer block for slot {Slot}; falling back to standalone attestation.",
                slot);
        }

        await PublishStandaloneAttestationAsync(slot, cancellationToken);
        PruneOldSlots(slot);
        DutyRunsTotal.Add(1);
    }

    private bool IsProposerSlot(ulong slot)
    {
        var validatorCount = Math.Max(1UL, _validatorCount);
        return slot % validatorCount == _validatorId;
    }

    private async Task<bool> TryPublishProposerBlockAsync(ulong slot, CancellationToken cancellationToken)
    {
        var parentRootBytes = _consensusService.GetProposalHeadRoot();
        if (parentRootBytes.Length != SszEncoding.Bytes32Length)
        {
            _logger.LogWarning(
                "Cannot construct proposer block for slot {Slot}: unexpected head root length {Length}.",
                slot,
                parentRootBytes.Length);
            return false;
        }

        var parentRoot = new Bytes32(parentRootBytes);
        var baseAttestationData = _consensusService.CreateAttestationData(slot);
        _logger.LogInformation(
            "Proposer checkpoint tuple. Slot: {Slot}, ValidatorId: {ValidatorId}, SourceSlot: {SourceSlot}, TargetSlot: {TargetSlot}, JustifiedSlot: {JustifiedSlot}, FinalizedSlot: {FinalizedSlot}, SourceRoot: {SourceRoot}, TargetRoot: {TargetRoot}",
            slot,
            _validatorId,
            baseAttestationData.Source.Slot.Value,
            baseAttestationData.Target.Slot.Value,
            _consensusService.JustifiedSlot,
            _consensusService.FinalizedSlot,
            Convert.ToHexString(baseAttestationData.Source.Root.AsSpan()),
            Convert.ToHexString(baseAttestationData.Target.Root.AsSpan()));
        var (aggregatedAttestations, aggregatedProofs) = BuildAggregatedAttestations(slot, baseAttestationData.Source);

        var candidateBlock = new Block(
            new Slot(slot),
            _validatorId,
            parentRoot,
            Bytes32.Zero(),
            new BlockBody(aggregatedAttestations));

        if (!_consensusService.TryComputeBlockStateRoot(candidateBlock, out var computedStateRoot, out var computeReason))
        {
            _logger.LogWarning(
                "Cannot construct proposer block for slot {Slot}: failed to compute state root. Reason: {Reason}",
                slot,
                computeReason);
            return false;
        }

        var block = candidateBlock with { StateRoot = computedStateRoot };
        var blockRoot = new Bytes32(block.HashTreeRoot());

        var proposerAttestationData = baseAttestationData with
        {
            Head = new Checkpoint(blockRoot, new Slot(slot)),
        };

        var proposerMessageRoot = proposerAttestationData.HashTreeRoot();
        var proposerSigningStopwatch = Stopwatch.StartNew();
        var proposerSignatureBytes = _leanSig.Sign(_validatorSecretKey, ToSignatureEpoch(slot), proposerMessageRoot);
        proposerSigningStopwatch.Stop();
        LeanMetrics.RecordPqAttestationSigning(proposerSigningStopwatch.Elapsed);
        var proposerSignature = XmssSignature.FromBytes(proposerSignatureBytes);
        var proposerAttestation = new Attestation(_validatorId, proposerAttestationData);
        var signedProposerAttestation = new SignedAttestation(_validatorId, proposerAttestationData, proposerSignature);
        RecordObservedAttestation(signedProposerAttestation);

        var signedBlock = new SignedBlockWithAttestation(
            new BlockWithAttestation(block, proposerAttestation),
            new BlockSignatures(aggregatedProofs, proposerSignature));

        if (!_consensusService.TryApplyLocalBlock(signedBlock, out var applyReason))
        {
            _logger.LogWarning(
                "Proposer block rejected locally. Slot: {Slot}, ValidatorId: {ValidatorId}, Reason: {Reason}",
                slot,
                _validatorId,
                applyReason);
            return false;
        }

        var payload = SszEncoding.Encode(signedBlock);
        TryDumpProposerBlock(slot, payload, parentRoot, blockRoot, signedBlock);
        await PublishToTopicAsync(_gossipTopics.BlockTopic, payload, cancellationToken);

        _logger.LogInformation(
            "Published proposer block. Slot: {Slot}, ValidatorId: {ValidatorId}, ParentRoot: {ParentRoot}, BlockRoot: {BlockRoot}, AggregatedAttestations: {AggregatedAttestations}, SignatureProofs: {SignatureProofs}",
            slot,
            _validatorId,
            Convert.ToHexString(parentRoot.AsSpan()),
            Convert.ToHexString(blockRoot.AsSpan()),
            aggregatedAttestations.Count,
            aggregatedProofs.Count);
        return true;
    }

    private (IReadOnlyList<AggregatedAttestation> Attestations, IReadOnlyList<AggregatedSignatureProof> Proofs) BuildAggregatedAttestations(
        ulong slot,
        Checkpoint requiredSource)
    {
        List<ObservedAttestationGroup> candidates;
        lock (_dutyStateLock)
        {
            candidates = _observedAttestationGroups.Values
                .Where(group =>
                    group.Data.Slot.Value < slot &&
                    CheckpointEquals(group.Data.Source, requiredSource) &&
                    group.SignaturesByValidator.Count > 0)
                .OrderByDescending(group => group.Data.Slot.Value)
                .ThenByDescending(group => group.SignaturesByValidator.Count)
                .Take(128)
                .Select(group => group.Clone())
                .ToList();
        }

        if (candidates.Count == 0)
        {
            return (Array.Empty<AggregatedAttestation>(), Array.Empty<AggregatedSignatureProof>());
        }

        var attestationProofs = new Dictionary<string, (AttestationData Data, AggregatedSignatureProof Proof, string Source)>(StringComparer.Ordinal);
        foreach (var group in candidates)
        {
            var messageRoot = group.Data.HashTreeRoot();
            var epoch = ToSignatureEpoch(group.Data.Slot.Value);
            var participants = new List<ulong>();
            var publicKeys = new List<ReadOnlyMemory<byte>>();
            var signatures = new List<ReadOnlyMemory<byte>>();

            // Keep validator ordering canonical (ascending index) so multisig
            // proofs verify across clients that reconstruct pubkeys from bitlists.
            foreach (var (validatorId, signatureBytes) in group.SignaturesByValidator.OrderBy(entry => entry.Key))
            {
                if (!_validatorPublicKeys.TryGetValue(validatorId, out var publicKey))
                {
                    continue;
                }

                var verificationStopwatch = Stopwatch.StartNew();
                var signatureValid = _leanSig.Verify(publicKey, epoch, messageRoot, signatureBytes);
                verificationStopwatch.Stop();
                LeanMetrics.RecordPqAttestationVerification(verificationStopwatch.Elapsed);
                if (!signatureValid)
                {
                    continue;
                }

                participants.Add(validatorId);
                publicKeys.Add(publicKey);
                signatures.Add(signatureBytes);
            }

            if (participants.Count == 0)
            {
                continue;
            }

            byte[] aggregateSignature;
            var aggregateBuildStopwatch = Stopwatch.StartNew();
            try
            {
                aggregateSignature = _leanMultiSig.AggregateSignatures(publicKeys, signatures, messageRoot, epoch);
            }
            catch (Exception ex)
            {
                aggregateBuildStopwatch.Stop();
                _logger.LogDebug(
                    ex,
                    "Failed aggregating attestation signatures. Slot: {Slot}, Participants: {Participants}",
                    group.Data.Slot.Value,
                    participants.Count);
                continue;
            }
            aggregateBuildStopwatch.Stop();
            LeanMetrics.RecordPqAggregatedSignatureBuilt(participants.Count, aggregateBuildStopwatch.Elapsed);

            var aggregateVerificationStopwatch = Stopwatch.StartNew();
            var aggregateValid = _leanMultiSig.VerifyAggregate(publicKeys, messageRoot, aggregateSignature, epoch);
            aggregateVerificationStopwatch.Stop();
            LeanMetrics.RecordPqAggregatedSignatureVerification(aggregateValid, aggregateVerificationStopwatch.Elapsed);
            if (!aggregateValid)
            {
                continue;
            }

            var bits = AggregationBits.FromValidatorIndices(participants);
            var generatedProof = new AggregatedSignatureProof(bits, aggregateSignature);
            var candidateKey = BuildAttestationProofKey(group.Data, generatedProof);
            attestationProofs[candidateKey] = (group.Data, generatedProof, "generated");
        }

        if (attestationProofs.Count == 0)
        {
            return (Array.Empty<AggregatedAttestation>(), Array.Empty<AggregatedSignatureProof>());
        }

        // Lean peers reject blocks with duplicate attestation messages.
        // Keep only one proof per attestation-data root (best participant coverage first).
        var bestPerDataRoot = new Dictionary<string, (AttestationData Data, AggregatedSignatureProof Proof, string Source)>(StringComparer.Ordinal);
        foreach (var candidate in attestationProofs.Values)
        {
            var dataRootKey = Convert.ToHexString(candidate.Data.HashTreeRoot());
            if (!bestPerDataRoot.TryGetValue(dataRootKey, out var existing) ||
                IsPreferredAggregateCandidate(candidate, existing))
            {
                bestPerDataRoot[dataRootKey] = candidate;
            }
        }

        var candidateProofs = new List<(string DataRootKey, string TargetRootKey, AttestationData Data, AggregatedSignatureProof Proof, string Source, List<ulong> Participants)>();
        foreach (var (dataRootKey, candidate) in bestPerDataRoot)
        {
            if (!candidate.Proof.Participants.TryToValidatorIndices(out var participantIds) || participantIds.Count == 0)
            {
                continue;
            }

            candidateProofs.Add((
                dataRootKey,
                Convert.ToHexString(candidate.Data.Target.Root.AsSpan()),
                candidate.Data,
                candidate.Proof,
                candidate.Source,
                participantIds.ToList()));
        }

        if (candidateProofs.Count == 0)
        {
            return (Array.Empty<AggregatedAttestation>(), Array.Empty<AggregatedSignatureProof>());
        }

        var quorumThreshold = ComputeTwoThirdsThreshold(Math.Max(1UL, _validatorCount));
        var orderedProofs = new List<(AttestationData Data, AggregatedSignatureProof Proof, string Source)>(MaxAggregatedProofsPerBlock);
        var selectedDataRoots = new HashSet<string>(StringComparer.Ordinal);
        var globallyCoveredParticipants = new HashSet<ulong>();

        var targetGroups = candidateProofs
            .GroupBy(candidate => candidate.TargetRootKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var coverage = new HashSet<ulong>(group.SelectMany(candidate => candidate.Participants));
                var sortedCandidates = group
                    .OrderByDescending(candidate => candidate.Participants.Count)
                    .ThenByDescending(candidate => candidate.Data.Slot.Value)
                    .ThenByDescending(candidate => SourceRank(candidate.Source))
                    .ThenBy(candidate => candidate.DataRootKey, StringComparer.Ordinal)
                    .ToList();

                return (
                    TargetRootKey: group.Key,
                    CoverageCount: coverage.Count,
                    MaxTargetSlot: group.Max(candidate => candidate.Data.Target.Slot.Value),
                    CanReachQuorum: coverage.Count >= quorumThreshold,
                    Candidates: sortedCandidates);
            })
            .OrderByDescending(group => group.CanReachQuorum)
            .ThenByDescending(group => group.CoverageCount)
            .ThenByDescending(group => group.MaxTargetSlot)
            .ThenBy(group => group.TargetRootKey, StringComparer.Ordinal)
            .ToList();

        foreach (var group in targetGroups)
        {
            if (orderedProofs.Count >= MaxAggregatedProofsPerBlock)
            {
                break;
            }

            var groupCoveredParticipants = new HashSet<ulong>();
            var remainingGroupCandidates = group.Candidates
                .Where(candidate => !selectedDataRoots.Contains(candidate.DataRootKey))
                .ToList();

            while (orderedProofs.Count < MaxAggregatedProofsPerBlock && remainingGroupCandidates.Count > 0)
            {
                var bestIndex = -1;
                var bestGroupCoverageGain = -1;
                var bestGlobalCoverageGain = -1;
                var bestParticipantCount = -1;
                ulong bestSlot = 0;
                var bestSourceRank = -1;
                var bestDataRootKey = string.Empty;

                for (var index = 0; index < remainingGroupCandidates.Count; index++)
                {
                    var candidate = remainingGroupCandidates[index];
                    var groupCoverageGain = candidate.Participants.Count(id => !groupCoveredParticipants.Contains(id));
                    var globalCoverageGain = candidate.Participants.Count(id => !globallyCoveredParticipants.Contains(id));
                    var participantCount = candidate.Participants.Count;
                    var slotValue = candidate.Data.Slot.Value;
                    var sourceRank = SourceRank(candidate.Source);

                    if (groupCoverageGain > bestGroupCoverageGain ||
                        (groupCoverageGain == bestGroupCoverageGain && globalCoverageGain > bestGlobalCoverageGain) ||
                        (groupCoverageGain == bestGroupCoverageGain && globalCoverageGain == bestGlobalCoverageGain && participantCount > bestParticipantCount) ||
                        (groupCoverageGain == bestGroupCoverageGain && globalCoverageGain == bestGlobalCoverageGain && participantCount == bestParticipantCount && slotValue > bestSlot) ||
                        (groupCoverageGain == bestGroupCoverageGain && globalCoverageGain == bestGlobalCoverageGain && participantCount == bestParticipantCount && slotValue == bestSlot && sourceRank > bestSourceRank) ||
                        (groupCoverageGain == bestGroupCoverageGain && globalCoverageGain == bestGlobalCoverageGain && participantCount == bestParticipantCount && slotValue == bestSlot && sourceRank == bestSourceRank && string.CompareOrdinal(candidate.DataRootKey, bestDataRootKey) < 0))
                    {
                        bestIndex = index;
                        bestGroupCoverageGain = groupCoverageGain;
                        bestGlobalCoverageGain = globalCoverageGain;
                        bestParticipantCount = participantCount;
                        bestSlot = slotValue;
                        bestSourceRank = sourceRank;
                        bestDataRootKey = candidate.DataRootKey;
                    }
                }

                if (bestIndex < 0 || bestGroupCoverageGain <= 0)
                {
                    break;
                }

                var selected = remainingGroupCandidates[bestIndex];
                orderedProofs.Add((selected.Data, selected.Proof, selected.Source));
                selectedDataRoots.Add(selected.DataRootKey);
                foreach (var participant in selected.Participants)
                {
                    groupCoveredParticipants.Add(participant);
                    globallyCoveredParticipants.Add(participant);
                }

                remainingGroupCandidates.RemoveAt(bestIndex);
                if (group.CanReachQuorum && groupCoveredParticipants.Count >= quorumThreshold)
                {
                    break;
                }
            }
        }

        var remainingCandidates = candidateProofs
            .Where(candidate => !selectedDataRoots.Contains(candidate.DataRootKey))
            .ToList();

        while (orderedProofs.Count < MaxAggregatedProofsPerBlock && remainingCandidates.Count > 0)
        {
            var bestIndex = 0;
            var bestNewCoverage = -1;
            var bestParticipantCount = -1;
            ulong bestSlot = 0;
            var bestSourceRank = -1;
            var bestDataRootKey = string.Empty;

            for (var index = 0; index < remainingCandidates.Count; index++)
            {
                var candidate = remainingCandidates[index];
                var newCoverage = candidate.Participants.Count(id => !globallyCoveredParticipants.Contains(id));
                var participantCount = candidate.Participants.Count;
                var slotValue = candidate.Data.Slot.Value;
                var sourceRank = SourceRank(candidate.Source);

                if (newCoverage > bestNewCoverage ||
                    (newCoverage == bestNewCoverage && participantCount > bestParticipantCount) ||
                    (newCoverage == bestNewCoverage && participantCount == bestParticipantCount && slotValue > bestSlot) ||
                    (newCoverage == bestNewCoverage && participantCount == bestParticipantCount && slotValue == bestSlot && sourceRank > bestSourceRank) ||
                    (newCoverage == bestNewCoverage && participantCount == bestParticipantCount && slotValue == bestSlot && sourceRank == bestSourceRank && string.CompareOrdinal(candidate.DataRootKey, bestDataRootKey) < 0))
                {
                    bestIndex = index;
                    bestNewCoverage = newCoverage;
                    bestParticipantCount = participantCount;
                    bestSlot = slotValue;
                    bestSourceRank = sourceRank;
                    bestDataRootKey = candidate.DataRootKey;
                }
            }

            if (bestNewCoverage <= 0)
            {
                break;
            }

            var selected = remainingCandidates[bestIndex];
            orderedProofs.Add((selected.Data, selected.Proof, selected.Source));
            foreach (var participant in selected.Participants)
            {
                globallyCoveredParticipants.Add(participant);
            }

            remainingCandidates.RemoveAt(bestIndex);
        }

        var attestations = new List<AggregatedAttestation>(orderedProofs.Count);
        var proofs = new List<AggregatedSignatureProof>(orderedProofs.Count);
        foreach (var (data, proof, source) in orderedProofs)
        {
            var participants = CloneAggregationBits(proof.Participants);
            attestations.Add(new AggregatedAttestation(participants, data));
            proofs.Add(CloneAggregatedProof(proof));

            var participantIds = participants.TryToValidatorIndices(out var ids)
                ? string.Join(",", ids)
                : "none";
            _logger.LogInformation(
                "Prepared block aggregate proof. Source: {Source}, Slot: {Slot}, TargetSlot: {TargetSlot}, Participants: [{Participants}], DataRoot: {DataRoot}, TargetRoot: {TargetRoot}, ProofBytes: {ProofBytes}, ProofHash: {ProofHash}",
                source,
                data.Slot.Value,
                data.Target.Slot.Value,
                participantIds,
                Convert.ToHexString(data.HashTreeRoot()),
                Convert.ToHexString(data.Target.Root.AsSpan()),
                proof.ProofData.Length,
                Convert.ToHexString(SHA256.HashData(proof.ProofData)).Substring(0, 16));
        }

        return (attestations, proofs);
    }

    private void InitializeValidatorKeyMaterial()
    {
        _validatorId = _validatorDutyConfig.ValidatorIndex;
        _validatorCount = Math.Max(_consensusConfig.InitialValidatorCount, _validatorId + 1);
        LeanMetrics.SetValidatorsCount(_validatorCount);

        LoadKnownValidatorPublicKeysFromGenesisConfig();

        var configuredPublic = ParseHex(_validatorDutyConfig.PublicKeyHex)
            ?? ReadKeyFile(_validatorDutyConfig.PublicKeyPath, "public");
        var derivedPublic = configuredPublic;
        if (derivedPublic is null)
        {
            lock (_dutyStateLock)
            {
                if (_validatorPublicKeys.TryGetValue(_validatorId, out var knownPublic) && knownPublic.Length > 0)
                {
                    derivedPublic = knownPublic.ToArray();
                }
            }
        }

        var configuredSecret = ParseHex(_validatorDutyConfig.SecretKeyHex)
            ?? ReadKeyFile(_validatorDutyConfig.SecretKeyPath, "secret");
        if (derivedPublic is not null && configuredSecret is not null)
        {
            _validatorPublicKey = derivedPublic;
            _validatorSecretKey = configuredSecret;
            _aggregateEnabled = _validatorDutyConfig.PublishAggregates;
        }
        else if (configuredSecret is not null)
        {
            _validatorPublicKey = Array.Empty<byte>();
            _validatorSecretKey = configuredSecret;
            _aggregateEnabled = false;
            _logger.LogWarning(
                "Validator secret key configured without public key. Aggregate publishing is disabled for validator {ValidatorId}.",
                _validatorDutyConfig.ValidatorIndex);
        }
        else
        {
            var keyPair = _leanSig.GenerateKeyPair(
                _validatorDutyConfig.ActivationEpoch,
                _validatorDutyConfig.NumActiveEpochs);
            _validatorPublicKey = keyPair.PublicKey;
            _validatorSecretKey = keyPair.SecretKey;
            _aggregateEnabled = _validatorDutyConfig.PublishAggregates;
        }

        lock (_dutyStateLock)
        {
            if (_validatorPublicKey.Length > 0)
            {
                _validatorPublicKeys[_validatorId] = _validatorPublicKey.ToArray();
            }
        }

        LoadKnownValidatorPublicKeysFromDirectory();
        LogKnownValidatorKeyPrefixes();
    }

    private async Task SubscribeAttestationTopicsAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_gossipTopics.AttestationTopic))
        {
            await _networkService.SubscribeAsync(
                _gossipTopics.AttestationTopic,
                ObserveAttestationPayload,
                cancellationToken);
        }
    }

    private async Task SubscribeBlockTopicsAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_gossipTopics.BlockTopic))
        {
            await _networkService.SubscribeAsync(
                _gossipTopics.BlockTopic,
                ObserveBlockPayload,
                cancellationToken);
        }
    }

    private void ObserveAttestationPayload(byte[] payload)
    {
        var decodeResult = _signedAttestationDecoder.DecodeAndValidate(payload);
        if (!decodeResult.IsSuccess || decodeResult.Attestation is null)
        {
            return;
        }

        RecordObservedAttestation(decodeResult.Attestation);
    }

    private void ObserveBlockPayload(byte[] payload)
    {
        var decodeResult = _signedBlockDecoder.DecodeAndValidate(payload);
        TryDumpObservedBlockPayload(payload, decodeResult);
    }

    private void RecordObservedAttestation(SignedAttestation attestation)
    {
        var dataRootKey = Convert.ToHexString(attestation.Message.HashTreeRoot());
        var signatureBytes = attestation.Signature.Bytes.ToArray();

        lock (_dutyStateLock)
        {
            if (!_observedAttestationGroups.TryGetValue(dataRootKey, out var group))
            {
                group = new ObservedAttestationGroup(attestation.Message);
                _observedAttestationGroups[dataRootKey] = group;
            }

            group.SignaturesByValidator[attestation.ValidatorId] = signatureBytes;
        }
    }

    private void LoadKnownValidatorPublicKeysFromDirectory()
    {
        if (string.IsNullOrWhiteSpace(_validatorDutyConfig.PublicKeyPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_validatorDutyConfig.PublicKeyPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(directory, "validator_*_pk.ssz"))
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (!TryParseValidatorIndexFromPublicKeyFileName(fileName, out var validatorId))
            {
                continue;
            }

            var publicKeyBytes = File.ReadAllBytes(filePath);
            if (publicKeyBytes.Length == 0)
            {
                continue;
            }

            lock (_dutyStateLock)
            {
                _validatorPublicKeys[validatorId] = publicKeyBytes;
            }
        }
    }

    private void LoadKnownValidatorPublicKeysFromGenesisConfig()
    {
        if (_validatorDutyConfig.GenesisValidatorPublicKeys.Count == 0)
        {
            return;
        }

        lock (_dutyStateLock)
        {
            for (var i = 0; i < _validatorDutyConfig.GenesisValidatorPublicKeys.Count; i++)
            {
                var parsed = ParseHex(_validatorDutyConfig.GenesisValidatorPublicKeys[i]);
                if (parsed is null || parsed.Length == 0)
                {
                    continue;
                }

                _validatorPublicKeys[(ulong)i] = parsed;
            }
        }
    }

    private static bool TryParseValidatorIndexFromPublicKeyFileName(string fileName, out ulong validatorId)
    {
        validatorId = 0;
        const string prefix = "validator_";
        const string suffix = "_pk";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var span = fileName.AsSpan(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        return ulong.TryParse(span, out validatorId);
    }

    private byte[]? ReadKeyFile(string? path, string keyLabel)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var resolvedPath = path.Trim();
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"Configured {keyLabel} key file was not found: {resolvedPath}");
        }

        var bytes = File.ReadAllBytes(resolvedPath);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException($"Configured {keyLabel} key file is empty: {resolvedPath}");
        }

        _logger.LogInformation("Loaded validator {KeyLabel} key bytes from {Path}.", keyLabel, resolvedPath);
        return bytes;
    }

    private async Task PublishAggregateAsync(
        ulong slot,
        byte[] messageRoot,
        uint epoch,
        CancellationToken cancellationToken)
    {
        List<ValidatorSignature> signatures;
        lock (_dutyStateLock)
        {
            if (!_slotSignatures.TryGetValue(slot, out var currentSignatures))
            {
                return;
            }

            signatures = currentSignatures.ToList();
        }

        var publicKeys = signatures
            .Select(sig => new ReadOnlyMemory<byte>(sig.PublicKey))
            .ToList();
        var signatureBytes = signatures
            .Select(sig => new ReadOnlyMemory<byte>(sig.Signature))
            .ToList();

        var aggregateBuildStopwatch = Stopwatch.StartNew();
        var aggregate = _leanMultiSig.AggregateSignatures(publicKeys, signatureBytes, messageRoot, epoch);
        aggregateBuildStopwatch.Stop();
        LeanMetrics.RecordPqAggregatedSignatureBuilt(signatures.Count, aggregateBuildStopwatch.Elapsed);

        var aggregateVerificationStopwatch = Stopwatch.StartNew();
        var isValid = _leanMultiSig.VerifyAggregate(publicKeys, messageRoot, aggregate, epoch);
        aggregateVerificationStopwatch.Stop();
        LeanMetrics.RecordPqAggregatedSignatureVerification(isValid, aggregateVerificationStopwatch.Elapsed);
        if (!isValid)
        {
            _logger.LogWarning("Skipping aggregate publish due to local verification failure. Slot: {Slot}", slot);
            return;
        }

        await PublishToTopicAsync(_gossipTopics.AggregateTopic, aggregate, cancellationToken);
    }

    private void TrackSignature(ulong slot, byte[] messageRoot, uint epoch, byte[] signature)
    {
        if (_validatorPublicKey.Length == 0)
        {
            return;
        }

        lock (_dutyStateLock)
        {
            if (!_slotSignatures.TryGetValue(slot, out var signatures))
            {
                signatures = new List<ValidatorSignature>();
                _slotSignatures[slot] = signatures;
            }

            signatures.Add(new ValidatorSignature(
                _validatorPublicKey.ToArray(),
                signature.ToArray(),
                messageRoot.ToArray(),
                epoch));
        }
    }

    private void PruneOldSlots(ulong currentSlot)
    {
        var retainFrom = currentSlot > 8 ? currentSlot - 8 : 0;
        lock (_dutyStateLock)
        {
            var staleSlots = _slotSignatures.Keys.Where(slot => slot < retainFrom).ToList();
            foreach (var slot in staleSlots)
            {
                _slotSignatures.Remove(slot);
            }

            var staleAttestationRoots = _observedAttestationGroups
                .Where(pair => pair.Value.Data.Slot.Value < retainFrom)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var key in staleAttestationRoots)
            {
                _observedAttestationGroups.Remove(key);
            }
        }
    }

    private static bool CheckpointEquals(Checkpoint left, Checkpoint right)
    {
        return left.Slot.Value == right.Slot.Value &&
               left.Root.Equals(right.Root);
    }

    private static AggregationBits CloneAggregationBits(AggregationBits bits)
    {
        return new AggregationBits(bits.Bits.ToArray());
    }

    private static AggregatedSignatureProof CloneAggregatedProof(AggregatedSignatureProof proof)
    {
        return new AggregatedSignatureProof(
            CloneAggregationBits(proof.Participants),
            proof.ProofData.ToArray());
    }

    private static string BuildAttestationProofKey(AttestationData data, AggregatedSignatureProof proof)
    {
        var dataRoot = Convert.ToHexString(data.HashTreeRoot());
        var proofBytes = SszEncoding.Encode(proof);
        return $"{dataRoot}:{Convert.ToHexString(proofBytes)}";
    }

    private void LogKnownValidatorKeyPrefixes()
    {
        lock (_dutyStateLock)
        {
            foreach (var (validatorId, publicKey) in _validatorPublicKeys.OrderBy(entry => entry.Key).Take(16))
            {
                var prefixLength = Math.Min(publicKey.Length, 8);
                var prefix = Convert.ToHexString(publicKey.AsSpan(0, prefixLength));
                _logger.LogInformation(
                    "Known validator key loaded. ValidatorId: {ValidatorId}, Length: {Length}, Prefix: {Prefix}",
                    validatorId,
                    publicKey.Length,
                    prefix);
            }
        }
    }

    private static uint ToSignatureEpoch(ulong slot)
    {
        // Lean clients (ream/zeam) use slot as the XMSS epoch parameter.
        return checked((uint)slot);
    }

    private static byte[]? ParseHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        if (trimmed.Length == 0 || trimmed.Length % 2 != 0)
        {
            return null;
        }

        try
        {
            return Convert.FromHexString(trimmed);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task PublishToTopicAsync(
        string topic,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(topic))
        {
            await _networkService.PublishAsync(topic, payload, cancellationToken);
        }
    }

    private static bool IsTruthyEnvironmentValue(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDumpDirectory(string envVar, string defaultFolderName)
    {
        var configured = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return Path.Combine(Path.GetTempPath(), defaultFolderName);
    }

    private void TryDumpAttestation(
        ulong slot,
        ReadOnlyMemory<byte> payload,
        byte[] messageRoot,
        byte[] signatureBytes,
        bool selfVerificationOk)
    {
        if (!DumpAttestationsEnabled)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(DumpAttestationsDirectory);
            var prefix = $"validator-{_validatorId}-slot-{slot:D6}";
            var payloadPath = Path.Combine(DumpAttestationsDirectory, $"{prefix}.ssz");
            var metaPath = Path.Combine(DumpAttestationsDirectory, $"{prefix}.txt");

            File.WriteAllBytes(payloadPath, payload.ToArray());
            File.WriteAllText(
                metaPath,
                string.Join(
                    Environment.NewLine,
                    new[]
                    {
                        $"slot={slot}",
                        $"validator_id={_validatorId}",
                        $"message_root={Convert.ToHexString(messageRoot)}",
                        $"signature_length={signatureBytes.Length}",
                        $"signature_prefix={Convert.ToHexString(signatureBytes.AsSpan(0, Math.Min(32, signatureBytes.Length)))}",
                        $"self_verified={selfVerificationOk}"
                    }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dump attestation diagnostics for slot {Slot}.", slot);
        }
    }

    private void TryDumpProposerBlock(
        ulong slot,
        ReadOnlyMemory<byte> payload,
        Bytes32 parentRoot,
        Bytes32 blockRoot,
        SignedBlockWithAttestation signedBlock)
    {
        if (!DumpBlocksEnabled)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(DumpBlocksDirectory);
            var prefix = $"validator-{_validatorId}-slot-{slot:D6}-block";
            var payloadPath = Path.Combine(DumpBlocksDirectory, $"{prefix}.ssz");
            var metaPath = Path.Combine(DumpBlocksDirectory, $"{prefix}.txt");

            File.WriteAllBytes(payloadPath, payload.ToArray());

            var lines = new List<string>
            {
                $"slot={slot}",
                $"validator_id={_validatorId}",
                $"parent_root={Convert.ToHexString(parentRoot.AsSpan())}",
                $"block_root={Convert.ToHexString(blockRoot.AsSpan())}",
                $"attestation_count={signedBlock.Message.Block.Body.Attestations.Count}",
                $"proof_count={signedBlock.Signature.AttestationSignatures.Count}",
                $"proposer_signature_length={signedBlock.Signature.ProposerSignature.Bytes.Length}",
                $"proposer_signature_hash={Convert.ToHexString(SHA256.HashData(signedBlock.Signature.ProposerSignature.Bytes))}"
            };

            var attestations = signedBlock.Message.Block.Body.Attestations;
            var proofs = signedBlock.Signature.AttestationSignatures;
            var limit = Math.Min(attestations.Count, proofs.Count);
            for (var i = 0; i < limit; i++)
            {
                var attestation = attestations[i];
                var proof = proofs[i];

                var attestationParticipants = attestation.AggregationBits.TryToValidatorIndices(out var attIndices)
                    ? string.Join(",", attIndices)
                    : "none";
                var proofParticipants = proof.Participants.TryToValidatorIndices(out var proofIndices)
                    ? string.Join(",", proofIndices)
                    : "none";

                lines.Add($"agg[{i}].slot={attestation.Data.Slot.Value}");
                lines.Add($"agg[{i}].data_root={Convert.ToHexString(attestation.Data.HashTreeRoot())}");
                lines.Add($"agg[{i}].attestation_participants=[{attestationParticipants}]");
                lines.Add($"agg[{i}].proof_participants=[{proofParticipants}]");
                lines.Add($"agg[{i}].proof_length={proof.ProofData.Length}");
                lines.Add($"agg[{i}].proof_hash={Convert.ToHexString(SHA256.HashData(proof.ProofData))}");
            }

            File.WriteAllText(metaPath, string.Join(Environment.NewLine, lines));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dump proposer block diagnostics for slot {Slot}.", slot);
        }
    }

    private void TryDumpObservedBlockPayload(byte[] payload, BlockGossipDecodeResult decodeResult)
    {
        if (!DumpObservedBlocksEnabled)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(DumpObservedBlocksDirectory);
            var counter = Interlocked.Increment(ref _observedBlockDumpCounter);
            var prefix = $"observed-block-{counter:D6}";
            var payloadPath = Path.Combine(DumpObservedBlocksDirectory, $"{prefix}.ssz");
            var metaPath = Path.Combine(DumpObservedBlocksDirectory, $"{prefix}.txt");

            File.WriteAllBytes(payloadPath, payload);

            var lines = new List<string>
            {
                $"payload_bytes={payload.Length}",
                $"decode_success={decodeResult.IsSuccess}",
                $"decode_failure={decodeResult.Failure}",
                $"decode_reason={decodeResult.Reason}"
            };

            if (decodeResult.IsSuccess && decodeResult.SignedBlock is not null)
            {
                var block = decodeResult.SignedBlock.Message.Block;
                lines.Add($"block_slot={block.Slot.Value}");
                lines.Add($"block_root={Convert.ToHexString(block.HashTreeRoot())}");
                lines.Add($"attestation_count={block.Body.Attestations.Count}");
                lines.Add($"proof_count={decodeResult.SignedBlock.Signature.AttestationSignatures.Count}");
            }

            File.WriteAllText(metaPath, string.Join(Environment.NewLine, lines));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dump observed block payload.");
        }
    }

    private static bool IsPreferredAggregateCandidate(
        (AttestationData Data, AggregatedSignatureProof Proof, string Source) candidate,
        (AttestationData Data, AggregatedSignatureProof Proof, string Source) existing)
    {
        var candidateParticipants = CountParticipants(candidate.Proof.Participants);
        var existingParticipants = CountParticipants(existing.Proof.Participants);
        if (candidateParticipants != existingParticipants)
        {
            return candidateParticipants > existingParticipants;
        }

        var candidateSourceRank = SourceRank(candidate.Source);
        var existingSourceRank = SourceRank(existing.Source);
        if (candidateSourceRank != existingSourceRank)
        {
            return candidateSourceRank > existingSourceRank;
        }

        // Deterministic fallback for equal quality candidates.
        return string.CompareOrdinal(
            Convert.ToHexString(candidate.Proof.ProofData),
            Convert.ToHexString(existing.Proof.ProofData)) < 0;
    }

    private static int CountParticipants(AggregationBits bits)
    {
        return bits.TryToValidatorIndices(out var ids) ? ids.Count : 0;
    }

    private static int SourceRank(string source)
    {
        return string.Equals(source, "generated", StringComparison.Ordinal) ? 1 : 0;
    }

    private static int ComputeTwoThirdsThreshold(ulong validatorCount)
    {
        return checked((int)((validatorCount * 2 + 2) / 3));
    }

    private sealed class ObservedAttestationGroup
    {
        public ObservedAttestationGroup(AttestationData data)
        {
            Data = data;
        }

        public AttestationData Data { get; }

        public Dictionary<ulong, byte[]> SignaturesByValidator { get; } = new();

        public ObservedAttestationGroup Clone()
        {
            var cloned = new ObservedAttestationGroup(Data);
            foreach (var (validatorId, signature) in SignaturesByValidator)
            {
                cloned.SignaturesByValidator[validatorId] = signature.ToArray();
            }

            return cloned;
        }
    }

    private sealed record ValidatorSignature(
        byte[] PublicKey,
        byte[] Signature,
        byte[] MessageRoot,
        uint Epoch);
}
