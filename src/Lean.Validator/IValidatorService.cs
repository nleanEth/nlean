using Lean.Consensus.Chain;

namespace Lean.Validator;

public interface IValidatorService : IIntervalDutyTarget
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
