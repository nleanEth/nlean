using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Security.Cryptography;
using Lean.Consensus;
using Lean.Consensus.ForkChoice;
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
    private readonly SignedBlockWithAttestationGossipDecoder _signedBlockDecoder = new();
    private readonly Dictionary<ulong, byte[]> _validatorPublicKeys = new();
    private readonly object _lifecycleLock = new();
    private readonly object _dutyStateLock = new();
    private CancellationTokenSource? _dutyLoopCts;
    private Task? _dutyLoopTask;
    private int _started;
    private byte[] _validatorPublicKey = Array.Empty<byte>();
    private byte[] _validatorSecretKey = Array.Empty<byte>();
    private ulong _validatorId;
    private ulong _validatorCount;
    private int _observedBlockDumpCounter;
    private ulong _lastUnknownRootSuppressedSlot = ulong.MaxValue;

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

            lock (_lifecycleLock)
            {
                _dutyLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // Use LongRunning so crypto FFI calls (XMSS sign ~10ms,
                // aggregate ~700ms) don't block ThreadPool workers.
                _dutyLoopTask = Task.Factory.StartNew(
                    () => RunDutyLoopAsync(_dutyLoopCts.Token),
                    _dutyLoopCts.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap();
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
        ulong? lastAggregatedSlot = null;

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
                        if (ShouldSuppressDutyForUnknownRoots(slot))
                        {
                            continue;
                        }

                        var publishedBlock = await TryPublishProposerBlockAsync(slot, cancellationToken);
                        if (publishedBlock)
                        {
                            // Proposer attestation is bundled in the block wrapper.
                            lastPublishedProposerSlot = slot;
                            lastAttestedSlot = slot;
                            DutyRunsTotal.Add(1);
                            continue;
                        }
                    }
                }

                if (intervalInSlot >= 1 && lastAttestedSlot != slot)
                {
                    var proposerAttestedInBlock = lastPublishedProposerSlot == slot;
                    if (!proposerAttestedInBlock && !IsProposerSlot(slot))
                    {
                        if (ShouldSuppressDutyForUnknownRoots(slot))
                        {
                            lastAttestedSlot = slot;
                            continue;
                        }

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

                if (_validatorDutyConfig.PublishAggregates &&
                    intervalInSlot >= 2 &&
                    lastAggregatedSlot != slot)
                {
                    lastAggregatedSlot = slot;
                    try
                    {
                        await ExecuteAggregationDutyAsync(slot, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Validator aggregation duty execution failed.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Validator duty loop terminated unexpectedly.");
            throw;
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
        var intervalDurationMs = Math.Max(1L, slotDurationMs / ProtoArrayForkChoiceStore.IntervalsPerSlot);

        var nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var elapsedMs = nowUnixMs - checked(genesisUnix * 1000L);
        if (elapsedMs < 0)
        {
            intervalInSlot = 0;
            return true;
        }

        var elapsedInSlotMs = elapsedMs % slotDurationMs;
        var interval = (int)(elapsedInSlotMs / intervalDurationMs);
        intervalInSlot = Math.Clamp(interval, 0, ProtoArrayForkChoiceStore.IntervalsPerSlot - 1);
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

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Attestation checkpoint tuple. Slot: {Slot}, ValidatorId: {ValidatorId}, SourceSlot: {SourceSlot}, TargetSlot: {TargetSlot}, JustifiedSlot: {JustifiedSlot}, FinalizedSlot: {FinalizedSlot}, SourceRoot: {SourceRoot}, TargetRoot: {TargetRoot}",
                slot,
                _validatorId,
                attestationData.Source.Slot.Value,
                attestationData.Target.Slot.Value,
                justifiedSlot,
                finalizedSlot,
                Convert.ToHexString(attestationData.Source.Root.AsSpan()),
                Convert.ToHexString(attestationData.Target.Root.AsSpan()));
        }

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
        if (!_consensusService.TryApplyLocalAttestation(signedAttestation, out var localAttestationReason))
        {
            _logger.LogWarning(
                "Local attestation rejected by consensus. Slot: {Slot}, ValidatorId: {ValidatorId}, Reason: {Reason}",
                slot,
                _validatorId,
                localAttestationReason);
            return;
        }

        var attestationPayload = SszEncoding.Encode(signedAttestation);
        var subnetId = new ValidatorIndex(_validatorId).ComputeSubnetId(_consensusConfig.AttestationCommitteeCount);
        await PublishToTopicAsync(_gossipTopics.AttestationSubnetTopic(subnetId), attestationPayload, cancellationToken);

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
        if (ShouldSuppressDutyForUnknownRoots(slot))
            return;

        if (slot > 0 && IsProposerSlot(slot))
        {
            var publishedBlock = await TryPublishProposerBlockAsync(slot, cancellationToken);
            if (publishedBlock)
            {
                DutyRunsTotal.Add(1);
                return;
            }
            return;
        }

        await PublishStandaloneAttestationAsync(slot, cancellationToken);
        DutyRunsTotal.Add(1);
    }

    private bool IsProposerSlot(ulong slot)
    {
        var validatorCount = Math.Max(1UL, _validatorCount);
        return slot % validatorCount == _validatorId;
    }

    private async Task ExecuteAggregationDutyAsync(ulong slot, CancellationToken cancellationToken)
    {
        if (!_validatorDutyConfig.PublishAggregates)
            return;

        var groups = _consensusService.CollectAttestationsForAggregation();
        if (groups.Count == 0)
        {
            _logger.LogDebug("Aggregation duty skipped: no groups. Slot: {Slot}", slot);
            return;
        }

        _logger.LogDebug("Aggregation duty started. Slot: {Slot}, Groups: {Groups}", slot, groups.Count);

        Dictionary<ulong, byte[]> knownPublicKeys;
        lock (_dutyStateLock)
        {
            knownPublicKeys = new Dictionary<ulong, byte[]>(_validatorPublicKeys);
        }

        foreach (var (data, validatorIds, signatures) in groups)
        {
            if (validatorIds.Count == 0)
                continue;

            var publicKeys = new List<ReadOnlyMemory<byte>>();
            var signatureBytes = new List<ReadOnlyMemory<byte>>();
            var participantIds = new List<ulong>();

            for (var i = 0; i < validatorIds.Count; i++)
            {
                if (!knownPublicKeys.TryGetValue(validatorIds[i], out var pk))
                    continue;

                publicKeys.Add(pk);
                signatureBytes.Add(signatures[i].EncodeBytes());
                participantIds.Add(validatorIds[i]);
            }

            if (publicKeys.Count == 0)
                continue;

            byte[] proofData;
            try
            {
                var messageRoot = data.HashTreeRoot();
                var epoch = ToSignatureEpoch(data.Slot.Value);
                var aggregationStopwatch = System.Diagnostics.Stopwatch.StartNew();
                proofData = _leanMultiSig.AggregateSignatures(publicKeys, signatureBytes, messageRoot, epoch);
                aggregationStopwatch.Stop();
                LeanMetrics.RecordPqAggregatedSignatureBuilt(publicKeys.Count, aggregationStopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MultiSig aggregation failed for slot {Slot}.", data.Slot.Value);
                continue;
            }

            var participants = AggregationBits.FromValidatorIndices(participantIds);
            var proof = new AggregatedSignatureProof(participants, proofData);
            var signed = new SignedAggregatedAttestation(data, proof);
            var payload = SszEncoding.Encode(signed);

            // Store locally first so the proposer can include this aggregation
            // in the next block even if gossipsub does not deliver self-published
            // messages back to this node.
            _consensusService.TryApplyLocalAggregatedAttestation(signed, out _);

            await PublishToTopicAsync(_gossipTopics.AggregateTopic, payload, cancellationToken);

            _logger.LogDebug(
                "Published aggregated attestation. Slot: {Slot}, Participants: [{Participants}], ProofBytes: {ProofBytes}",
                data.Slot.Value,
                string.Join(",", participantIds),
                proofData.Length);
        }
    }

    private bool ShouldSuppressDutyForUnknownRoots(ulong slot)
    {
        if (!_consensusService.HasUnknownBlockRootsInFlight)
        {
            return false;
        }

        if (_lastUnknownRootSuppressedSlot == slot)
        {
            return true;
        }

        _lastUnknownRootSuppressedSlot = slot;
        _logger.LogInformation(
            "Skipping validator duty while unknown-root recovery is in flight. Slot: {Slot}, ValidatorId: {ValidatorId}, HeadSlot: {HeadSlot}, JustifiedSlot: {JustifiedSlot}, FinalizedSlot: {FinalizedSlot}",
            slot,
            _validatorId,
            _consensusService.HeadSlot,
            _consensusService.JustifiedSlot,
            _consensusService.FinalizedSlot);
        return true;
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
        var (knownAttestations, knownProofs) = _consensusService.GetKnownAggregatedPayloadsForBlock(slot, requiredSource);
        if (knownAttestations.Count == 0 || knownProofs.Count == 0)
        {
            return (Array.Empty<AggregatedAttestation>(), Array.Empty<AggregatedSignatureProof>());
        }

        var selectedAttestations = new List<AggregatedAttestation>();
        var selectedProofs = new List<AggregatedSignatureProof>();
        SelectBestProofs(knownAttestations, knownProofs, selectedAttestations, selectedProofs, "consensus");

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var (att, proof) in selectedAttestations.Zip(selectedProofs))
            {
                if (proof.Participants.TryToValidatorIndices(out var pids))
                {
                    _logger.LogDebug(
                        "Block aggregated attestation. Slot: {Slot}, SourceSlot: {SourceSlot}, TargetSlot: {TargetSlot}, Participants: [{Participants}], TargetRoot: {TargetRoot}",
                        att.Data.Slot.Value,
                        att.Data.Source.Slot.Value,
                        att.Data.Target.Slot.Value,
                        string.Join(",", pids),
                        Convert.ToHexString(att.Data.Target.Root.AsSpan()));
                }
            }
        }

        return (selectedAttestations, selectedProofs);
    }

    private void SelectBestProofs(
        IReadOnlyList<AggregatedAttestation> inputAttestations,
        IReadOnlyList<AggregatedSignatureProof> inputProofs,
        List<AggregatedAttestation> outputAttestations,
        List<AggregatedSignatureProof> outputProofs,
        string source)
    {
        var candidateProofs = new List<(string DataRootKey, string TargetRootKey, AttestationData Data, AggregatedSignatureProof Proof, List<ulong> Participants)>();
        var limit = Math.Min(inputAttestations.Count, inputProofs.Count);
        var bestPerDataRoot = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < limit; i++)
        {
            var attestation = inputAttestations[i];
            var proof = inputProofs[i];
            if (!proof.Participants.TryToValidatorIndices(out var participantIds) || participantIds.Count == 0)
            {
                continue;
            }

            var dataRootKey = Convert.ToHexString(attestation.Data.HashTreeRoot());
            var targetRootKey = Convert.ToHexString(attestation.Data.Target.Root.AsSpan());

            if (bestPerDataRoot.TryGetValue(dataRootKey, out var existingIndex))
            {
                var existingCount = candidateProofs[existingIndex].Participants.Count;
                if (participantIds.Count <= existingCount)
                {
                    continue;
                }

                candidateProofs[existingIndex] = (dataRootKey, targetRootKey, attestation.Data, proof, participantIds.ToList());
            }
            else
            {
                bestPerDataRoot[dataRootKey] = candidateProofs.Count;
                candidateProofs.Add((dataRootKey, targetRootKey, attestation.Data, proof, participantIds.ToList()));
            }
        }

        if (candidateProofs.Count == 0)
        {
            return;
        }

        var quorumThreshold = ComputeTwoThirdsThreshold(Math.Max(1UL, _validatorCount));
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
            if (outputAttestations.Count >= MaxAggregatedProofsPerBlock)
            {
                break;
            }

            var groupCoveredParticipants = new HashSet<ulong>();
            var remainingGroupCandidates = group.Candidates
                .Where(candidate => !selectedDataRoots.Contains(candidate.DataRootKey))
                .ToList();

            while (outputAttestations.Count < MaxAggregatedProofsPerBlock && remainingGroupCandidates.Count > 0)
            {
                var bestIndex = -1;
                var bestGroupCoverageGain = -1;
                var bestGlobalCoverageGain = -1;
                var bestParticipantCount = -1;
                ulong bestSlot = 0;

                for (var index = 0; index < remainingGroupCandidates.Count; index++)
                {
                    var candidate = remainingGroupCandidates[index];
                    var groupCoverageGain = candidate.Participants.Count(id => !groupCoveredParticipants.Contains(id));
                    var globalCoverageGain = candidate.Participants.Count(id => !globallyCoveredParticipants.Contains(id));
                    var participantCount = candidate.Participants.Count;
                    var slotValue = candidate.Data.Slot.Value;

                    if (groupCoverageGain > bestGroupCoverageGain ||
                        (groupCoverageGain == bestGroupCoverageGain && globalCoverageGain > bestGlobalCoverageGain) ||
                        (groupCoverageGain == bestGroupCoverageGain && globalCoverageGain == bestGlobalCoverageGain && participantCount > bestParticipantCount) ||
                        (groupCoverageGain == bestGroupCoverageGain && globalCoverageGain == bestGlobalCoverageGain && participantCount == bestParticipantCount && slotValue > bestSlot))
                    {
                        bestIndex = index;
                        bestGroupCoverageGain = groupCoverageGain;
                        bestGlobalCoverageGain = globalCoverageGain;
                        bestParticipantCount = participantCount;
                        bestSlot = slotValue;
                    }
                }

                if (bestIndex < 0 || bestGroupCoverageGain <= 0)
                {
                    break;
                }

                var selected = remainingGroupCandidates[bestIndex];
                var participants = CloneAggregationBits(selected.Proof.Participants);
                outputAttestations.Add(new AggregatedAttestation(participants, selected.Data));
                outputProofs.Add(CloneAggregatedProof(selected.Proof));
                selectedDataRoots.Add(selected.DataRootKey);
                foreach (var participant in selected.Participants)
                {
                    groupCoveredParticipants.Add(participant);
                    globallyCoveredParticipants.Add(participant);
                }

                var participantIds = participants.TryToValidatorIndices(out var ids)
                    ? string.Join(",", ids)
                    : "none";
                _logger.LogDebug(
                    "Prepared block aggregate proof. Source: {Source}, Slot: {Slot}, TargetSlot: {TargetSlot}, Participants: [{Participants}], DataRoot: {DataRoot}, TargetRoot: {TargetRoot}, ProofBytes: {ProofBytes}",
                    source,
                    selected.Data.Slot.Value,
                    selected.Data.Target.Slot.Value,
                    participantIds,
                    Convert.ToHexString(selected.Data.HashTreeRoot()),
                    Convert.ToHexString(selected.Data.Target.Root.AsSpan()),
                    selected.Proof.ProofData.Length);

                remainingGroupCandidates.RemoveAt(bestIndex);
                if (group.CanReachQuorum && groupCoveredParticipants.Count >= quorumThreshold)
                {
                    break;
                }
            }
        }
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
        }
        else if (configuredSecret is not null)
        {
            _validatorPublicKey = Array.Empty<byte>();
            _validatorSecretKey = configuredSecret;
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

    private async Task SubscribeBlockTopicsAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_gossipTopics.BlockTopic))
        {
            await _networkService.SubscribeAsync(
                _gossipTopics.BlockTopic,
                ObserveBlockPayload,
                cancellationToken);
        }

        var subnetCount = Math.Max(1, _consensusConfig.AttestationCommitteeCount);
        for (var subnetId = 0; subnetId < subnetCount; subnetId++)
        {
            var subnetTopic = _gossipTopics.AttestationSubnetTopic(subnetId);
            if (!string.IsNullOrWhiteSpace(subnetTopic))
            {
                await _networkService.SubscribeAsync(subnetTopic, _ => { }, cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(_gossipTopics.AggregateTopic))
        {
            await _networkService.SubscribeAsync(_gossipTopics.AggregateTopic, _ => { }, cancellationToken);
        }
    }

    private void ObserveBlockPayload(byte[] payload)
    {
        var decodeResult = _signedBlockDecoder.DecodeAndValidate(payload);
        TryDumpObservedBlockPayload(payload, decodeResult);
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

    private static int ComputeTwoThirdsThreshold(ulong validatorCount)
    {
        return checked((int)((validatorCount * 2 + 2) / 3));
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
}
