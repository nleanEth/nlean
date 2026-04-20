using Lean.Node;
using Lean.Node.Configuration;
using Microsoft.Extensions.Hosting;

namespace Lean.Client;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            try
            {
                Console.Error.WriteLine($"[fatal] AppDomain unhandled exception. IsTerminating={eventArgs.IsTerminating}");
                if (eventArgs.ExceptionObject is Exception ex)
                {
                    Console.Error.WriteLine(ex);
                }
                else if (eventArgs.ExceptionObject is not null)
                {
                    Console.Error.WriteLine(eventArgs.ExceptionObject);
                }
            }
            catch
            {
                // Best-effort fatal logging only.
            }
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            try
            {
                Console.Error.WriteLine("[fatal] Unobserved task exception.");
                Console.Error.WriteLine(eventArgs.Exception);
            }
            catch
            {
                // Best-effort fatal logging only.
            }
        };

        var cliOptions = CliOptions.Parse(args);

        if (cliOptions.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (cliOptions.ShowVersion)
        {
            Console.WriteLine(BuildInfo.VersionString);
            return 0;
        }

        if (cliOptions.AggregateSubnetIds is not null && !cliOptions.IsAggregator)
        {
            Console.Error.WriteLine("Error: --aggregate-subnet-ids requires --is-aggregator to be set.");
            return 1;
        }

        var overrides = new NodeOptionsOverrides(
            cliOptions.ConfigPath,
            cliOptions.DataDir,
            cliOptions.ForkDigest,
            cliOptions.Metrics,
            cliOptions.LogLevel,
            cliOptions.ValidatorConfig,
            cliOptions.NodeName,
            cliOptions.CheckpointSyncUrl,
            cliOptions.NodeKeyPath,
            cliOptions.SocketPort,
            cliOptions.MetricsPort,
            cliOptions.MetricsAddress,
            cliOptions.IsAggregator,
            cliOptions.AttestationCommitteeCount,
            cliOptions.ApiPort,
            cliOptions.HashSigKeyDir,
            cliOptions.AggregateSubnetIds,
            cliOptions.AnnotatedValidatorsPath);

        var nodeOptions = NodeOptions.Load(overrides);

        try
        {
            NodeApp.LoadChainConfig(nodeOptions);
            await NodeApp.TryRunCheckpointSyncAsync(nodeOptions, CancellationToken.None);
            using var host = NodeApp.Build(nodeOptions);
            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[fatal] Host terminated with exception.");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Lean Client\n");
        Console.WriteLine("Usage:");
        Console.WriteLine("  lean-client [options]\n");
        Console.WriteLine("Options:");
        Console.WriteLine("  --config PATH             Path to node-config.json");
        Console.WriteLine("  --data-dir PATH           Data directory");
        Console.WriteLine("  --network DIGEST          Fork digest for gossip topics (default: 12345678)");
        Console.WriteLine("  --metrics [true|false]     Enable/disable metrics");
        Console.WriteLine("  --metrics-port PORT       Metrics endpoint port");
        Console.WriteLine("  --metrics-address HOST    Metrics listen host");
        Console.WriteLine("  --log LEVEL               Log level (Trace, Debug, Information, Warning, Error)");
        Console.WriteLine("  --validator-config PATH   Path to validator-config.yaml");
        Console.WriteLine("  --node, --node-id NAME    Node name inside validator-config.yaml");
        Console.WriteLine("  --node-key PATH           Path to libp2p private key file");
        Console.WriteLine("  --socket-port PORT        QUIC transport port");
        Console.WriteLine("  --api-port PORT           HTTP API port");
        Console.WriteLine("  --is-aggregator           Enable aggregate publishing");
        Console.WriteLine("  --aggregate-subnet-ids IDs");
        Console.WriteLine("                            Comma-separated extra subnet IDs for aggregators (requires --is-aggregator)");
        Console.WriteLine("  --attestation-committee-count N");
        Console.WriteLine("                            Committee count override");
        Console.WriteLine("  --hash-sig-key-dir DIR    Hash-sig key directory (auto-resolves by validator index)");
        Console.WriteLine("  --checkpoint-sync-url URL Bootstrap from a remote finalized state");
        Console.WriteLine("  --version, -v             Print version");
        Console.WriteLine("  --help, -h                Show help");
    }
}
