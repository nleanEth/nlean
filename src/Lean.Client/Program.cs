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

        var overrides = new NodeOptionsOverrides(
            cliOptions.ConfigPath,
            cliOptions.DataDir,
            cliOptions.Network,
            cliOptions.Metrics,
            cliOptions.Libp2pConfig,
            cliOptions.LogLevel,
            cliOptions.ValidatorConfig,
            cliOptions.NodeName);

        var nodeOptions = NodeOptions.Load(overrides);

        try
        {
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
        Console.WriteLine(@"Lean Client

Usage:
  lean-client [options]

Options:
  --config PATH             Path to node-config.json
  --data-dir PATH           Data directory
  --network NAME            Network name (e.g. devnet2)
  --metrics                 Enable metrics
  --libp2p PATH             Optional libp2p config path
  --log LEVEL               Log level (Trace, Debug, Information, Warning, Error)
  --validator-config PATH   Path to validator-config.yaml
  --node NAME               Node name inside validator-config.yaml
  --version, -v             Print version
  --help, -h                Show help
");
    }
}
