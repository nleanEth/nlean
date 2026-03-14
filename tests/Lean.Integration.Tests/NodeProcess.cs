using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Lean.Integration.Tests;

public sealed class NodeProcess : IDisposable
{
    private Process? _process;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();

    private readonly string _binaryPath;
    private readonly string _validatorConfigPath;
    private readonly string _nodeName;
    private readonly string _dataDir;
    private readonly string _network;
    private readonly string _nodeKeyPath;
    private readonly int _socketPort;
    private readonly int _metricsPort;
    private readonly bool _isAggregator;
    private readonly string _hashSigKeyDir;
    private readonly string? _checkpointSyncUrl;

    public string NodeName => _nodeName;
    public string DataDir => _dataDir;
    public int ApiPort { get; }
    public bool IsRunning => _process is { HasExited: false };

    public NodeProcess(
        string binaryPath,
        string validatorConfigPath,
        string nodeName,
        string dataDir,
        string network,
        string nodeKeyPath,
        int socketPort,
        int apiPort,
        int metricsPort,
        bool isAggregator,
        string hashSigKeyDir,
        string? checkpointSyncUrl = null)
    {
        _binaryPath = binaryPath;
        _validatorConfigPath = validatorConfigPath;
        _nodeName = nodeName;
        _dataDir = dataDir;
        _network = network;
        _nodeKeyPath = nodeKeyPath;
        _socketPort = socketPort;
        ApiPort = apiPort;
        _metricsPort = metricsPort;
        _isAggregator = isAggregator;
        _hashSigKeyDir = hashSigKeyDir;
        _checkpointSyncUrl = checkpointSyncUrl;
    }

    public void Start()
    {
        var configPath = Path.Combine(_dataDir, "node-config.json");
        var args = new StringBuilder();
        args.Append($"--config \"{configPath}\"");
        args.Append($" --validator-config \"{_validatorConfigPath}\"");
        args.Append($" --node {_nodeName}");
        args.Append($" --data-dir \"{_dataDir}\"");
        args.Append($" --network {_network}");
        args.Append($" --node-key \"{_nodeKeyPath}\"");
        args.Append($" --socket-port {_socketPort}");
        args.Append($" --api-port {ApiPort}");
        args.Append($" --metrics-port {_metricsPort}");
        args.Append($" --hash-sig-key-dir \"{_hashSigKeyDir}\"");

        if (_isAggregator)
        {
            args.Append(" --is-aggregator");
        }

        if (!string.IsNullOrWhiteSpace(_checkpointSyncUrl))
        {
            args.Append($" --checkpoint-sync-url {_checkpointSyncUrl}");
        }

        args.Append(" --log Information");

        var psi = new ProcessStartInfo
        {
            FileName = _binaryPath,
            Arguments = args.ToString(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            ConfigureMacOsDyld(psi);
        }

        _stdout.Clear();
        _stderr.Clear();

        _process = new Process { StartInfo = psi };
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) _stdout.AppendLine(e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) _stderr.AppendLine(e.Data);
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public void Kill()
    {
        if (_process is null or { HasExited: true })
            return;

        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch
        {
            // Best-effort.
        }
    }

    public void CleanConsensusData()
    {
        var consensusDir = Path.Combine(_dataDir, "consensus");
        if (Directory.Exists(consensusDir))
        {
            Directory.Delete(consensusDir, recursive: true);
        }
    }

    public string GetStdout() => _stdout.ToString();
    public string GetStderr() => _stderr.ToString();

    public void Dispose()
    {
        Kill();
        _process?.Dispose();
    }

    private static void ConfigureMacOsDyld(ProcessStartInfo psi)
    {
        var dirs = new[]
        {
            "/opt/homebrew/lib",
            "/opt/homebrew/opt/libmsquic/lib",
            "/usr/local/lib",
            "/usr/local/opt/libmsquic/lib"
        };

        var existing = Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH") ?? "";
        var validDirs = dirs.Where(Directory.Exists).ToList();
        if (!string.IsNullOrEmpty(existing))
        {
            validDirs.Add(existing);
        }

        if (validDirs.Count > 0)
        {
            psi.Environment["DYLD_LIBRARY_PATH"] = string.Join(':', validDirs);
        }
    }
}
