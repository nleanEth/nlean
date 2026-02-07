using Lean.Crypto;
using Microsoft.Extensions.Logging;

namespace Lean.Validator;

public sealed class ValidatorService : IValidatorService
{
    private readonly ILogger<ValidatorService> _logger;
    private readonly ILeanSig _leanSig;
    private readonly ILeanMultiSig _leanMultiSig;
    private bool _started;

    public ValidatorService(ILogger<ValidatorService> logger, ILeanSig leanSig, ILeanMultiSig leanMultiSig)
    {
        _logger = logger;
        _leanSig = leanSig;
        _leanMultiSig = leanMultiSig;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_started)
        {
            return Task.CompletedTask;
        }

        // Initialize native proving/verifying contexts once at service start.
        _leanMultiSig.SetupProver();
        _leanMultiSig.SetupVerifier();

        _started = true;
        _logger.LogInformation("Validator service started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _started = false;
        _logger.LogInformation("Validator service stopped.");
        return Task.CompletedTask;
    }
}
