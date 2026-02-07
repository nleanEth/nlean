using Lean.Crypto;
using Microsoft.Extensions.Logging;

namespace Lean.Validator;

public sealed class ValidatorService : IValidatorService
{
    private readonly ILogger<ValidatorService> _logger;
    private readonly ILeanSig _leanSig;
    private readonly ILeanMultiSig _leanMultiSig;

    public ValidatorService(ILogger<ValidatorService> logger, ILeanSig leanSig, ILeanMultiSig leanMultiSig)
    {
        _logger = logger;
        _leanSig = leanSig;
        _leanMultiSig = leanMultiSig;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Validator service started - stub. TODO: implement duty scheduling and signing.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Validator service stopped.");
        return Task.CompletedTask;
    }
}
