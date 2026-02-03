using Lean.Consensus;
using Lean.Crypto;
using Lean.Metrics;
using Lean.Network;
using Lean.Node.Configuration;
using Lean.Storage;
using Lean.Validator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p;

namespace Lean.Node;

public static class NodeApp
{
    public static IHost Build(NodeOptions options)
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                if (Enum.TryParse<LogLevel>(options.Logging.Level, true, out var level))
                {
                    logging.SetMinimumLevel(level);
                }
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(options);
                services.AddSingleton(options.Libp2p);
                services.AddSingleton(options.Metrics);
                services.AddSingleton(options.Storage);

                services.AddLibp2p(libp2pBuilder =>
                {
                    if (options.Libp2p.EnablePubsub)
                    {
                        libp2pBuilder.WithPubsub();
                    }
                    if (options.Libp2p.EnableQuic)
                    {
                        libp2pBuilder.WithQuic();
                    }

                    return libp2pBuilder;
                });

                services.AddSingleton<IConsensusService, ConsensusService>();
                services.AddSingleton<INetworkService, Libp2pNetworkService>();
                services.AddSingleton<IMetricsService, PrometheusMetricsService>();
                services.AddSingleton<IValidatorService, ValidatorService>();
                services.AddSingleton<ILeanSig, RustLeanSig>();
                services.AddSingleton<ILeanMultiSig, RustLeanMultiSig>();

                services.AddHostedService<NodeService>();
            });

        return builder.Build();
    }
}
