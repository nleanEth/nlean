using System.Diagnostics.Metrics;
using Lean.Consensus;
using Lean.Consensus.Types;
using Lean.Crypto;
using Lean.Network;
using Microsoft.Extensions.Logging;

namespace Lean.Validator;

public sealed class ValidatorService : IValidatorService
{
    private static readonly Meter ValidatorMeter = new("Lean.Validator");
    private static readonly Counter<long> DutyRunsTotal = ValidatorMeter.CreateCounter<long>(
        "lean_validator_duty_runs_total",
        description: "Total number of validator duty loop ticks executed.");

    private readonly ILogger<ValidatorService> _logger;
    private readonly IConsensusService _consensusService;
    private readonly INetworkService _networkService;
    private readonly ConsensusConfig _consensusConfig;
    private readonly ValidatorDutyConfig _validatorDutyConfig;
    private readonly ILeanSig _leanSig;
    private readonly ILeanMultiSig _leanMultiSig;
    private readonly Dictionary<ulong, List<ValidatorSignature>> _slotSignatures = new();
    private readonly object _lifecycleLock = new();
    private readonly object _dutyStateLock = new();
    private CancellationTokenSource? _dutyLoopCts;
    private Task? _dutyLoopTask;
    private int _started;
    private byte[] _validatorPublicKey = Array.Empty<byte>();
    private byte[] _validatorSecretKey = Array.Empty<byte>();
    private ulong _validatorId;
    private bool _aggregateEnabled;

    public ValidatorService(
        ILogger<ValidatorService> logger,
        IConsensusService consensusService,
        INetworkService networkService,
        ConsensusConfig consensusConfig,
        ValidatorDutyConfig validatorDutyConfig,
        ILeanSig leanSig,
        ILeanMultiSig leanMultiSig)
    {
        _logger = logger;
        _consensusService = consensusService;
        _networkService = networkService;
        _consensusConfig = consensusConfig;
        _validatorDutyConfig = validatorDutyConfig;
        _leanSig = leanSig;
        _leanMultiSig = leanMultiSig;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        try
        {
            // Initialize native proving/verifying contexts once per active lifecycle.
            _leanMultiSig.SetupProver();
            _leanMultiSig.SetupVerifier();
            InitializeValidatorKeyMaterial();

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
            "Validator service started. SecondsPerSlot: {SecondsPerSlot}",
            Math.Max(1, _consensusConfig.SecondsPerSlot));
        return Task.CompletedTask;
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
        var secondsPerSlot = Math.Max(1, _consensusConfig.SecondsPerSlot);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(secondsPerSlot));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await ExecuteDutyAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Validator duty execution failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path.
        }
    }

    private async Task ExecuteDutyAsync(CancellationToken cancellationToken)
    {
        var slot = _consensusService.CurrentSlot;
        var headSlot = _consensusService.HeadSlot;
        var headRoot = NormalizeRoot(_consensusService.HeadRoot);
        var sourceSlot = Math.Min(slot, headSlot);
        var epoch = ToEpoch(slot);
        var attestationData = new AttestationData(
            new Slot(slot),
            new Checkpoint(headRoot, new Slot(headSlot)),
            new Checkpoint(headRoot, new Slot(headSlot)),
            new Checkpoint(headRoot, new Slot(sourceSlot)));

        var messageRoot = attestationData.HashTreeRoot();
        var signatureBytes = _leanSig.Sign(_validatorSecretKey, epoch, messageRoot);
        var signature = XmssSignature.FromBytes(signatureBytes);
        var signedAttestation = new SignedAttestation(_validatorId, attestationData, signature);
        var attestationPayload = SszEncoding.Encode(signedAttestation);
        await _networkService.PublishAsync(GossipTopics.Attestations, attestationPayload, cancellationToken);
        TrackSignature(slot, messageRoot, epoch, signatureBytes);

        if (_aggregateEnabled)
        {
            await PublishAggregateAsync(slot, messageRoot, epoch, cancellationToken);
        }

        PruneOldSlots(slot);
        DutyRunsTotal.Add(1);
        _logger.LogDebug(
            "Executed validator duty for slot {Slot}. HeadSlot: {HeadSlot}, ValidatorId: {ValidatorId}",
            slot,
            headSlot,
            _validatorId);
    }

    private void InitializeValidatorKeyMaterial()
    {
        var configuredPublic = ParseHex(_validatorDutyConfig.PublicKeyHex);
        var configuredSecret = ParseHex(_validatorDutyConfig.SecretKeyHex);
        if (configuredPublic is not null && configuredSecret is not null)
        {
            _validatorPublicKey = configuredPublic;
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

        _validatorId = _validatorDutyConfig.ValidatorIndex;
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

        var aggregate = _leanMultiSig.AggregateSignatures(publicKeys, signatureBytes, messageRoot, epoch);
        var isValid = _leanMultiSig.VerifyAggregate(publicKeys, messageRoot, aggregate, epoch);
        if (!isValid)
        {
            _logger.LogWarning("Skipping aggregate publish due to local verification failure. Slot: {Slot}", slot);
            return;
        }

        await _networkService.PublishAsync(GossipTopics.Aggregates, aggregate, cancellationToken);
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
        }
    }

    private uint ToEpoch(ulong slot)
    {
        var slotsPerEpoch = Math.Max(1UL, _consensusConfig.SlotsPerEpoch);
        return checked((uint)(slot / slotsPerEpoch));
    }

    private static Bytes32 NormalizeRoot(byte[] maybeRoot)
    {
        if (maybeRoot.Length == SszEncoding.Bytes32Length)
        {
            return new Bytes32(maybeRoot);
        }

        return Bytes32.Zero();
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

    private sealed record ValidatorSignature(
        byte[] PublicKey,
        byte[] Signature,
        byte[] MessageRoot,
        uint Epoch);
}
