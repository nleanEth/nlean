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
    private readonly string _configPath;
    private readonly string _validatorConfigPath;
    private readonly string _nodeName;
    private readonly string _dataDir;
    private readonly bool _metrics;
    private readonly string? _checkpointSyncUrl;

    public string NodeName => _nodeName;
    public string DataDir => _dataDir;
    public int ApiPort { get; }
    public bool IsRunning => _process is { HasExited: false };

    public NodeProcess(
        string binaryPath,
        string configPath,
        string validatorConfigPath,
        string nodeName,
        string dataDir,
        int apiPort,
        bool metrics,
        string? checkpointSyncUrl = null)
    {
        _binaryPath = binaryPath;
        _configPath = configPath;
        _validatorConfigPath = validatorConfigPath;
        _nodeName = nodeName;
        _dataDir = dataDir;
        ApiPort = apiPort;
        _metrics = metrics;
        _checkpointSyncUrl = checkpointSyncUrl;
    }

    public void Start()
    {
        var args = new StringBuilder();
        args.Append($"--config \"{_configPath}\"");
        args.Append($" --validator-config \"{_validatorConfigPath}\"");
        args.Append($" --node {_nodeName}");
        args.Append($" --data-dir \"{_dataDir}\"");
        args.Append($" --metrics {_metrics.ToString().ToLowerInvariant()}");

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
