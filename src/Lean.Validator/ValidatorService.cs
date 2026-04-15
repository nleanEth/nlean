using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Security.Cryptography;
using Lean.Consensus;
using Lean.Consensus.Chain;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Sync;
using Lean.Consensus.Types;
using Lean.Crypto;
using Lean.Metrics;
using Lean.Network;
using Microsoft.Extensions.Logging;

namespace Lean.Validator;

public sealed record ValidatorKeyMaterial(
    byte[] AttestationPublicKey,
    byte[] AttestationSecretKey,
    byte[] ProposalPublicKey,
    byte[] ProposalSecretKey);

public sealed class ValidatorService : IValidatorService, IIntervalDutyTarget
{
    // Keep block gossip payloads comfortably below 1 MiB across mixed-client devnets.
    // Individual aggregate proofs are often ~250 KiB, so 3 proofs keeps the full block SSZ size bounded.
    private const int MaxAggregatedProofsPerBlock = SszEncoding.MaxAttestationsData;

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
    private readonly ISyncService? _syncService;
    private readonly SignedBlockGossipDecoder _signedBlockDecoder = new();
    private readonly Dictionary<ulong, byte[]> _validatorPublicKeys = new();
    private readonly object _dutyStateLock = new();
    private CancellationToken _shutdownToken;
    private int _started;
    private readonly Dictionary<ulong, ValidatorKeyMaterial> _localValidators = new();
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
        IGossipTopicProvider? gossipTopics = null,
        ISyncService? syncService = null)
    {
        _logger = logger;
        _consensusService = consensusService;
        _networkService = networkService;
        _gossipTopics = gossipTopics ?? new GossipTopicProvider(GossipTopics.DefaultNetwork);
        _consensusConfig = consensusConfig;
        _validatorDutyConfig = validatorDutyConfig;
        _leanSig = leanSig;
        _leanMultiSig = leanMultiSig;
        _syncService = syncService;
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
            _shutdownToken = cancellationToken;
            InitializeValidatorKeyMaterial();
            await SubscribeBlockTopicsAsync(cancellationToken);
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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("Validator service stopped.");
        return Task.CompletedTask;
    }

    public async Task OnIntervalAsync(ulong slot, int intervalInSlot)
    {
        if (_started == 0)
        {
            return;
        }

        var cancellationToken = _shutdownToken;

        if (_syncService is not null && _syncService.State != SyncState.Synced)
        {
            return;
        }

        if (ShouldSkipStaleDuty(slot, intervalInSlot))
        {
            return;
        }

        if (intervalInSlot == 0 && slot > 0 && TryGetProposerForSlot(slot, out var proposerValidatorId))
        {
            if (ShouldSuppressDutyForUnknownRoots(slot))
            {
                return;
            }

            if (await TryPublishProposerBlockAsync(slot, proposerValidatorId, cancellationToken))
            {
                DutyRunsTotal.Add(1);
            }

            return;
        }

        if (intervalInSlot == 1)
        {
            if (ShouldSuppressDutyForUnknownRoots(slot))
            {
                return;
            }

            foreach (var validatorId in _localValidators.Keys)
            {
                await PublishStandaloneAttestationAsync(slot, validatorId, cancellationToken);
                DutyRunsTotal.Add(1);
            }

            return;
        }

        if (_validatorDutyConfig.PublishAggregates && intervalInSlot == 2)
        {
            await ExecuteAggregationDutyAsync(slot, cancellationToken);
        }
    }

    private bool ShouldSkipStaleDuty(ulong slot, int intervalInSlot)
    {
        var currentSlot = _consensusService.CurrentSlot;
        if (slot >= currentSlot)
        {
            return false;
        }

        _logger.LogDebug(
            "Skipping stale validator duty. DutySlot: {DutySlot}, CurrentSlot: {CurrentSlot}, IntervalInSlot: {IntervalInSlot}, HeadSlot: {HeadSlot}",
            slot,
            currentSlot,
            intervalInSlot,
            _consensusService.HeadSlot);
        return true;
    }

    internal static bool ShouldAttemptStandaloneAttestation(
        int intervalInSlot,
        ulong? lastAttestedSlot,
        ulong slot,
        bool proposerAttestedInBlock,
        bool isProposerSlot)
    {
        if (intervalInSlot != 1)
        {
            return false;
        }

        if (lastAttestedSlot == slot)
        {
            return false;
        }

        if (proposerAttestedInBlock || isProposerSlot)
        {
            return false;
        }

        return true;
    }

    private async Task PublishStandaloneAttestationAsync(ulong slot, ulong validatorId, CancellationToken cancellationToken)
    {
        var keys = _localValidators[validatorId];
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
                validatorId,
                attestationData.Source.Slot.Value,
                attestationData.Target.Slot.Value,
                justifiedSlot,
                finalizedSlot,
                Convert.ToHexString(attestationData.Source.Root.AsSpan()),
                Convert.ToHexString(attestationData.Target.Root.AsSpan()));
        }

        var messageRoot = attestationData.HashTreeRoot();
        var signingStopwatch = Stopwatch.StartNew();
        var signatureBytes = _leanSig.Sign(keys.AttestationSecretKey, epoch, messageRoot);
        signingStopwatch.Stop();
        LeanMetrics.RecordPqAttestationSigning(signingStopwatch.Elapsed);

        var selfVerificationOk = true;
        if (keys.AttestationPublicKey.Length > 0)
        {
            var verificationStopwatch = Stopwatch.StartNew();
            selfVerificationOk = _leanSig.Verify(keys.AttestationPublicKey, epoch, messageRoot, signatureBytes);
            verificationStopwatch.Stop();
            LeanMetrics.RecordPqAttestationVerification(selfVerificationOk, verificationStopwatch.Elapsed);
        }

        var signature = XmssSignature.FromBytes(signatureBytes);
        var signedAttestation = new SignedAttestation(validatorId, attestationData, signature);
        if (!_consensusService.TryApplyLocalAttestation(signedAttestation, out var localAttestationReason))
        {
            _logger.LogWarning(
                "Local attestation rejected by consensus. Slot: {Slot}, ValidatorId: {ValidatorId}, Reason: {Reason}",
                slot,
                validatorId,
                localAttestationReason);
            return;
        }

        var attestationPayload = SszEncoding.Encode(signedAttestation);
        var subnetId = new ValidatorIndex(validatorId).ComputeSubnetId(_consensusConfig.AttestationCommitteeCount);
        await PublishToTopicAsync(_gossipTopics.AttestationSubnetTopic(subnetId), attestationPayload, cancellationToken);

        _logger.LogDebug(
            "Published attestation. Slot: {Slot}, ValidatorId: {ValidatorId}, HeadSlot: {HeadSlot}, TargetSlot: {TargetSlot}, SourceSlot: {SourceSlot}",
            slot,
            validatorId,
            attestationData.Head.Slot.Value,
            attestationData.Target.Slot.Value,
            attestationData.Source.Slot.Value);

        if (!selfVerificationOk)
        {
            _logger.LogError(
                "Local attestation signature self-verification failed. Slot: {Slot}, ValidatorId: {ValidatorId}",
                slot,
                validatorId);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Attestation signed. Slot: {Slot}, ValidatorId: {ValidatorId}, HeadSlot: {HeadSlot}, TargetSlot: {TargetSlot}, SourceSlot: {SourceSlot}, HeadRoot: {HeadRoot}, TargetRoot: {TargetRoot}, SourceRoot: {SourceRoot}, MessageRoot: {MessageRoot}, SignatureBytes: {SignatureBytes}, SelfVerified: {SelfVerified}",
                slot,
                validatorId,
                attestationData.Head.Slot.Value,
                attestationData.Target.Slot.Value,
                attestationData.Source.Slot.Value,
                Convert.ToHexString(attestationData.Head.Root.AsSpan()),
                Convert.ToHexString(attestationData.Target.Root.AsSpan()),
                Convert.ToHexString(attestationData.Source.Root.AsSpan()),
                Convert.ToHexString(messageRoot),
                signatureBytes.Length,
                selfVerificationOk);
        }

        TryDumpAttestation(slot, attestationPayload, messageRoot, signatureBytes, selfVerificationOk);

        _logger.LogDebug(
            "Executed validator attestation duty for slot {Slot}. HeadSlot: {HeadSlot}, ValidatorId: {ValidatorId}",
            slot,
            headSlot,
            validatorId);
    }

    private bool IsProposerSlot(ulong slot)
    {
        return TryGetProposerForSlot(slot, out _);
    }

    private bool TryGetProposerForSlot(ulong slot, out ulong proposerValidatorId)
    {
        var validatorCount = Math.Max(1UL, _validatorCount);
        foreach (var validatorId in _localValidators.Keys)
        {
            if (slot % validatorCount == validatorId)
            {
                proposerValidatorId = validatorId;
                return true;
            }
        }

        proposerValidatorId = 0;
        return false;
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
                LeanMetrics.RecordCommitteeSignaturesAggregation(aggregationStopwatch.Elapsed);
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
            _consensusService.ApplyLocalAggregationResult(signed, out _);

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

    private async Task<bool> TryPublishProposerBlockAsync(ulong slot, ulong validatorId, CancellationToken cancellationToken)
    {
        var keys = _localValidators[validatorId];
        var (parentRootBytes, baseAttestationData) = _consensusService.GetProposalContext(slot);
        if (parentRootBytes.Length != SszEncoding.Bytes32Length)
        {
            _logger.LogWarning(
                "Cannot construct proposer block for slot {Slot}: unexpected head root length {Length}.",
                slot,
                parentRootBytes.Length);
            return false;
        }

        var parentRoot = new Bytes32(parentRootBytes);
        _logger.LogDebug(
            "Proposer checkpoint tuple. Slot: {Slot}, ValidatorId: {ValidatorId}, SourceSlot: {SourceSlot}, TargetSlot: {TargetSlot}, JustifiedSlot: {JustifiedSlot}, FinalizedSlot: {FinalizedSlot}, SourceRoot: {SourceRoot}, TargetRoot: {TargetRoot}",
            slot,
            validatorId,
            baseAttestationData.Source.Slot.Value,
            baseAttestationData.Target.Slot.Value,
            _consensusService.JustifiedSlot,
            _consensusService.FinalizedSlot,
            Convert.ToHexString(baseAttestationData.Source.Root.AsSpan()),
            Convert.ToHexString(baseAttestationData.Target.Root.AsSpan()));
        var (aggregatedAttestations, aggregatedProofs) = BuildAggregatedAttestations(slot, validatorId, parentRoot, baseAttestationData.Source);

        var candidateBlock = new Block(
            new Slot(slot),
            validatorId,
            parentRoot,
            Bytes32.Zero(),
            new BlockBody(aggregatedAttestations));

        if (!_consensusService.TryComputeBlockStateRoot(candidateBlock, out var computedStateRoot, out var postJustified, out var computeReason))
        {
            _logger.LogWarning(
                "Cannot construct proposer block for slot {Slot}: failed to compute state root. Reason: {Reason}",
                slot,
                computeReason);
            return false;
        }

        // leanSpec PR 595 invariant: produced block must close any justified
        // divergence between the store and the head chain. If the fixed-point
        // attestation loop failed to converge (e.g. pool lacks attestations
        // from a minority fork that advanced store.latest_justified), publishing
        // this block would leave peers unable to see the justify advance —
        // degrading liveness. Skip the slot rather than broadcast a stale block.
        var storeJustifiedSlot = _consensusService.JustifiedSlot;
        if (postJustified.Slot.Value < storeJustifiedSlot)
        {
            _logger.LogWarning(
                "Skipping proposer block for slot {Slot}: produced block justified={ProducedJustified} < store justified={StoreJustified}. Fixed-point attestation loop did not converge.",
                slot,
                postJustified.Slot.Value,
                storeJustifiedSlot);
            return false;
        }

        var block = candidateBlock with { StateRoot = computedStateRoot };
        var blockRoot = block.HashTreeRoot();

        // Sign hash_tree_root(block) with PROPOSAL key
        var proposerSigningStopwatch = Stopwatch.StartNew();
        var proposerSignatureBytes = _leanSig.Sign(keys.ProposalSecretKey, ToSignatureEpoch(slot), blockRoot);
        proposerSigningStopwatch.Stop();
        LeanMetrics.RecordPqAttestationSigning(proposerSigningStopwatch.Elapsed);
        var proposerSignature = XmssSignature.FromBytes(proposerSignatureBytes);

        var signedBlock = new SignedBlock(
            block,
            new BlockSignatures(aggregatedProofs, proposerSignature));

        if (!_consensusService.TryApplyLocalBlock(signedBlock, out var applyReason))
        {
            _logger.LogWarning(
                "Proposer block rejected locally. Slot: {Slot}, ValidatorId: {ValidatorId}, Reason: {Reason}",
                slot,
                validatorId,
                applyReason);
            return false;
        }

        var payload = SszEncoding.Encode(signedBlock);
        var blockRootBytes32 = new Bytes32(blockRoot);
        TryDumpProposerBlock(slot, payload, parentRoot, blockRootBytes32, signedBlock);
        await PublishToTopicAsync(_gossipTopics.BlockTopic, payload, cancellationToken);

        _logger.LogInformation(
            "Published proposer block. Slot: {Slot}, ValidatorId: {ValidatorId}, ParentRoot: {ParentRoot}, BlockRoot: {BlockRoot}, AggregatedAttestations: {AggregatedAttestations}, SignatureProofs: {SignatureProofs}",
            slot,
            validatorId,
            Convert.ToHexString(parentRoot.AsSpan()),
            Convert.ToHexString(blockRoot),
            aggregatedAttestations.Count,
            aggregatedProofs.Count);
        return true;
    }

    /// <summary>
    /// Fixed-point attestation collection matching leanSpec build_block (state.py:780-837).
    /// Iteratively discovers attestations across source levels as justified advances.
    /// </summary>
    private (IReadOnlyList<AggregatedAttestation> Attestations, IReadOnlyList<AggregatedSignatureProof> Proofs) BuildAggregatedAttestations(
        ulong slot,
        ulong validatorId,
        Bytes32 parentRoot,
        Checkpoint initialSource)
    {
        var allAttestations = new List<AggregatedAttestation>();
        var allProofs = new List<AggregatedSignatureProof>();
        var currentSource = initialSource;

        // Fixed-point loop: each iteration may advance justified, unlocking new attestations.
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var (iterAttestations, iterProofs) = _consensusService.GetKnownAggregatedPayloadsForBlock(slot, currentSource);
            if (iterAttestations.Count == 0)
                break;

            allAttestations.AddRange(iterAttestations);
            allProofs.AddRange(iterProofs);

            // Select best from ALL accumulated candidates, build candidate block, run STF.
            var tempAttestations = new List<AggregatedAttestation>();
            var tempProofs = new List<AggregatedSignatureProof>();
            SelectBestProofs(allAttestations, allProofs, tempAttestations, tempProofs, "consensus");
            if (tempAttestations.Count == 0)
                break;

            var candidateBlock = new Block(
                new Slot(slot), validatorId, parentRoot, Bytes32.Zero(),
                new BlockBody(tempAttestations));

            if (!_consensusService.TryComputeBlockStateRoot(candidateBlock, out _, out var postJustified, out _))
                break;

            if (postJustified.Slot.Value <= currentSource.Slot.Value)
                break; // Fixed point reached — justified did not advance.

            _logger.LogDebug(
                "Fixed-point iteration {Iteration}: justified advanced {OldSlot} -> {NewSlot}",
                iteration, currentSource.Slot.Value, postJustified.Slot.Value);

            currentSource = postJustified;
        }

        // Final selection from all accumulated candidates.
        var selectedAttestations = new List<AggregatedAttestation>();
        var selectedProofs = new List<AggregatedSignatureProof>();
        if (allAttestations.Count > 0)
            SelectBestProofs(allAttestations, allProofs, selectedAttestations, selectedProofs, "consensus");

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
        var groups = new Dictionary<string, (AttestationData Data, HashSet<ulong> Allowed, List<AggregatedSignatureProof> Proofs)>(
            StringComparer.Ordinal);

        var limit = Math.Min(inputAttestations.Count, inputProofs.Count);
        for (var i = 0; i < limit; i++)
        {
            var attestation = inputAttestations[i];
            var proof = inputProofs[i];

            if (!proof.Participants.TryToValidatorIndices(out var proofParticipants) ||
                proofParticipants.Count == 0)
            {
                continue;
            }

            if (!attestation.AggregationBits.TryToValidatorIndices(out var allowedIds))
            {
                continue;
            }

            var dataRootKey = Convert.ToHexString(attestation.Data.HashTreeRoot());
            if (!groups.TryGetValue(dataRootKey, out var group))
            {
                group = (attestation.Data, new HashSet<ulong>(), new List<AggregatedSignatureProof>());
            }

            foreach (var allowedId in allowedIds)
            {
                group.Allowed.Add(allowedId);
            }
            group.Proofs.Add(proof);
            groups[dataRootKey] = group;
        }

        if (groups.Count == 0)
        {
            return;
        }

        // For each attestation data root, produce a single proof.
        // leanSpec PR #510: each AttestationData must appear at most once per block.
        // When multiple proofs exist for the same data, merge non-overlapping ones
        // via recursive aggregation to maximize validator coverage.
        foreach (var entry in groups.OrderByDescending(g => g.Value.Data.Slot.Value).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            if (outputAttestations.Count >= MaxAggregatedProofsPerBlock)
            {
                break;
            }

            var data = entry.Value.Data;
            var proofs = entry.Value.Proofs;

            AggregatedSignatureProof? mergedProof = TryMergeProofs(proofs, data);

            if (mergedProof is null)
            {
                continue;
            }

            var participants = CloneAggregationBits(mergedProof.Participants);
            outputAttestations.Add(new AggregatedAttestation(participants, data));
            outputProofs.Add(CloneAggregatedProof(mergedProof));

            if (mergedProof.Participants.TryToValidatorIndices(out var ids))
            {
                _logger.LogDebug(
                    "Prepared block aggregate proof. Source: {Source}, Slot: {Slot}, TargetSlot: {TargetSlot}, Participants: [{Participants}], DataRoot: {DataRoot}, TargetRoot: {TargetRoot}, ProofBytes: {ProofBytes}",
                    source,
                    data.Slot.Value,
                    data.Target.Slot.Value,
                    string.Join(",", ids),
                    Convert.ToHexString(data.HashTreeRoot()),
                    Convert.ToHexString(data.Target.Root.AsSpan()),
                    mergedProof.ProofData.Length);
            }
        }
    }

    /// <summary>
    /// Given multiple proofs for the same AttestationData, greedily select non-overlapping
    /// proofs and merge them via recursive aggregation. Returns the merged proof, or the
    /// single best proof if only one is usable or merging fails.
    /// </summary>
    private AggregatedSignatureProof? TryMergeProofs(
        List<AggregatedSignatureProof> proofs,
        AttestationData data)
    {
        // Filter to proofs with valid participants.
        var validProofs = new List<(AggregatedSignatureProof Proof, HashSet<ulong> Ids)>();
        foreach (var proof in proofs)
        {
            if (proof.Participants.TryToValidatorIndices(out var ids) && ids.Count > 0)
            {
                validProofs.Add((proof, new HashSet<ulong>(ids)));
            }
        }

        if (validProofs.Count == 0)
            return null;

        // Sort by coverage descending for greedy selection.
        validProofs.Sort((a, b) => b.Ids.Count.CompareTo(a.Ids.Count));

        if (validProofs.Count == 1)
            return validProofs[0].Proof;

        // Greedily select non-overlapping proofs to maximize coverage.
        var selected = new List<(AggregatedSignatureProof Proof, HashSet<ulong> Ids)> { validProofs[0] };
        var coveredIds = new HashSet<ulong>(validProofs[0].Ids);

        for (var i = 1; i < validProofs.Count; i++)
        {
            var candidate = validProofs[i];
            if (!candidate.Ids.Overlaps(coveredIds))
            {
                selected.Add(candidate);
                coveredIds.UnionWith(candidate.Ids);
            }
        }

        // If only one proof was selected (all others overlap), return it directly.
        if (selected.Count == 1)
            return selected[0].Proof;

        // Merge via recursive aggregation.
        // Each child needs its public keys (looked up from _validatorPublicKeys).
        try
        {
            var children = new List<(IReadOnlyList<ReadOnlyMemory<byte>> PublicKeys, byte[] ProofData)>();
            foreach (var (proof, ids) in selected)
            {
                var sortedIds = ids.OrderBy(id => id).ToList();
                var pks = new List<ReadOnlyMemory<byte>>();
                foreach (var id in sortedIds)
                {
                    if (_validatorPublicKeys.TryGetValue(id, out var pk))
                    {
                        pks.Add(pk);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Missing public key for validator {ValidatorId} during proof merge, falling back to best single proof",
                            id);
                        return selected[0].Proof;
                    }
                }
                children.Add((pks, proof.ProofData));
            }

            var messageRoot = data.HashTreeRoot();
            var epoch = ToSignatureEpoch(data.Slot.Value);

            var mergedProofData = _leanMultiSig.AggregateRecursive(children, messageRoot, epoch);
            var mergedParticipants = AggregationBits.FromValidatorIndices(coveredIds);

            _logger.LogDebug(
                "Merged {Count} proofs for AttestationData slot {Slot}: {OldCounts} -> {MergedCount} validators",
                selected.Count,
                data.Slot.Value,
                string.Join("+", selected.Select(s => s.Ids.Count)),
                coveredIds.Count);

            return new AggregatedSignatureProof(mergedParticipants, mergedProofData);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to merge {Count} proofs via recursive aggregation, falling back to best single proof",
                selected.Count);
            return selected[0].Proof;
        }
    }

    private void InitializeValidatorKeyMaterial()
    {
        _localValidators.Clear();

        var indices = _validatorDutyConfig.ValidatorIndices;
        if (indices.Count == 0)
        {
            indices = new[] { _validatorDutyConfig.ValidatorIndex };
        }

        _validatorId = indices[0];
        _validatorCount = Math.Max(_consensusConfig.InitialValidatorCount, indices.Max() + 1);
        LeanMetrics.SetValidatorsCount((ulong)indices.Count);
        LeanMetrics.SetAttestationCommitteeSubnet(
            new ValidatorIndex(_validatorId).ComputeSubnetId(_consensusConfig.AttestationCommitteeCount));

        LoadKnownValidatorPublicKeysFromGenesisConfig();

        for (var i = 0; i < indices.Count; i++)
        {
            var validatorId = indices[i];

            // Try attestation-specific key paths first, then fall back to legacy paths
            var attestPublicKeyPath = i < _validatorDutyConfig.AllAttestationPublicKeyPaths.Count
                ? _validatorDutyConfig.AllAttestationPublicKeyPaths[i]
                : null;
            var attestSecretKeyPath = i < _validatorDutyConfig.AllAttestationSecretKeyPaths.Count
                ? _validatorDutyConfig.AllAttestationSecretKeyPaths[i]
                : null;
            var proposalPublicKeyPath = i < _validatorDutyConfig.AllProposalPublicKeyPaths.Count
                ? _validatorDutyConfig.AllProposalPublicKeyPaths[i]
                : null;
            var proposalSecretKeyPath = i < _validatorDutyConfig.AllProposalSecretKeyPaths.Count
                ? _validatorDutyConfig.AllProposalSecretKeyPaths[i]
                : null;

            // Try attestation-specific hex config first, then fall back to legacy hex
            var attestPublicHex = (i == 0 ? ParseHex(_validatorDutyConfig.AttestationPublicKeyHex) : null)
                ?? (i == 0 ? ParseHex(_validatorDutyConfig.PublicKeyHex) : null);
            var attestSecretHex = (i == 0 ? ParseHex(_validatorDutyConfig.AttestationSecretKeyHex) : null)
                ?? (i == 0 ? ParseHex(_validatorDutyConfig.SecretKeyHex) : null);
            var proposalPublicHex = (i == 0 ? ParseHex(_validatorDutyConfig.ProposalPublicKeyHex) : null);
            var proposalSecretHex = (i == 0 ? ParseHex(_validatorDutyConfig.ProposalSecretKeyHex) : null);

            // Resolve attestation public key
            var configuredAttestPublic = attestPublicHex ?? ReadKeyFile(attestPublicKeyPath, "attestation public");
            var derivedAttestPublic = configuredAttestPublic;
            if (derivedAttestPublic is null)
            {
                lock (_dutyStateLock)
                {
                    if (_validatorPublicKeys.TryGetValue(validatorId, out var knownPublic) && knownPublic.Length > 0)
                    {
                        derivedAttestPublic = knownPublic.ToArray();
                    }
                }
            }

            // Resolve attestation secret key
            var configuredAttestSecret = attestSecretHex ?? ReadKeyFile(attestSecretKeyPath, "attestation secret");

            // Resolve proposal public key
            var configuredProposalPublic = proposalPublicHex ?? ReadKeyFile(proposalPublicKeyPath, "proposal public");

            // Resolve proposal secret key
            var configuredProposalSecret = proposalSecretHex ?? ReadKeyFile(proposalSecretKeyPath, "proposal secret");

            if (configuredAttestSecret is not null)
            {
                // We have at least attestation keys from config/files.
                // Use same keys for proposal if proposal keys not separately configured.
                var attestPub = derivedAttestPublic ?? Array.Empty<byte>();
                var proposalPub = configuredProposalPublic ?? attestPub;
                var proposalSec = configuredProposalSecret ?? configuredAttestSecret;

                _localValidators[validatorId] = new ValidatorKeyMaterial(
                    attestPub, configuredAttestSecret, proposalPub, proposalSec);

                if (attestPub.Length == 0)
                {
                    _logger.LogWarning(
                        "Validator secret key configured without public key. Aggregate publishing is disabled for validator {ValidatorId}.",
                        validatorId);
                }
            }
            else
            {
                // Generate fresh key pairs for both attestation and proposal
                var attestKeyPair = _leanSig.GenerateKeyPair(
                    _validatorDutyConfig.ActivationEpoch,
                    _validatorDutyConfig.NumActiveEpochs);
                var proposalKeyPair = _leanSig.GenerateKeyPair(
                    _validatorDutyConfig.ActivationEpoch,
                    _validatorDutyConfig.NumActiveEpochs);
                _localValidators[validatorId] = new ValidatorKeyMaterial(
                    attestKeyPair.PublicKey, attestKeyPair.SecretKey,
                    proposalKeyPair.PublicKey, proposalKeyPair.SecretKey);
            }

            if (i == 0)
            {
                _validatorPublicKey = _localValidators[validatorId].AttestationPublicKey;
                _validatorSecretKey = _localValidators[validatorId].AttestationSecretKey;
            }

            lock (_dutyStateLock)
            {
                var publicKey = _localValidators[validatorId].AttestationPublicKey;
                if (publicKey.Length > 0)
                {
                    _validatorPublicKeys[validatorId] = publicKey.ToArray();
                }
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
        var keyPath = _validatorDutyConfig.PublicKeyPath;
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            // Try attestation-specific path
            keyPath = _validatorDutyConfig.AttestationPublicKeyPath;
        }

        if (string.IsNullOrWhiteSpace(keyPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(keyPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        // Load attestation public keys from new naming convention first
        foreach (var filePath in Directory.EnumerateFiles(directory, "validator_*_attest_pk.ssz"))
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (!TryParseValidatorIndexFromAttestPublicKeyFileName(fileName, out var validatorId))
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

        // Fall back to legacy naming convention for validators not already loaded
        foreach (var filePath in Directory.EnumerateFiles(directory, "validator_*_pk.ssz"))
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (!TryParseValidatorIndexFromPublicKeyFileName(fileName, out var validatorId))
            {
                continue;
            }

            lock (_dutyStateLock)
            {
                if (_validatorPublicKeys.ContainsKey(validatorId))
                {
                    continue;
                }
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
        if (_validatorDutyConfig.GenesisValidatorKeys.Count == 0)
        {
            return;
        }

        lock (_dutyStateLock)
        {
            for (var i = 0; i < _validatorDutyConfig.GenesisValidatorKeys.Count; i++)
            {
                var (attestKeyHex, _) = _validatorDutyConfig.GenesisValidatorKeys[i];
                var parsed = ParseHex(attestKeyHex);
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

    private static bool TryParseValidatorIndexFromAttestPublicKeyFileName(string fileName, out ulong validatorId)
    {
        validatorId = 0;
        const string prefix = "validator_";
        const string suffix = "_attest_pk";
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
        SignedBlock signedBlock)
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
                $"attestation_count={signedBlock.Block.Body.Attestations.Count}",
                $"proof_count={signedBlock.Signature.AttestationSignatures.Count}",
                $"proposer_signature_length={signedBlock.Signature.ProposerSignature.Bytes.Length}",
                $"proposer_signature_hash={Convert.ToHexString(SHA256.HashData(signedBlock.Signature.ProposerSignature.Bytes))}"
            };

            var attestations = signedBlock.Block.Body.Attestations;
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
                var block = decodeResult.SignedBlock.Block;
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
