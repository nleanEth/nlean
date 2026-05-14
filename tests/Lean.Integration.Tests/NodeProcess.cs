using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// ReSharper disable InconsistentNaming

namespace Lean.Integration.Tests;

public sealed class NodeProcess : IDisposable
{
    private Process? _process;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private StreamWriter? _stdoutLog;
    private StreamWriter? _stderrLog;
    private string? _stdoutLogPath;
    private string? _stderrLogPath;

    public string? StdoutLogPath => _stdoutLogPath;
    public string? StderrLogPath => _stderrLogPath;

    private readonly string _binaryPath;
    private readonly string _customNetworkConfigDir;
    private readonly string _nodeName;
    private readonly string _dataDir;
    private readonly string _nodeKeyPath;
    private readonly int _socketPort;
    private readonly int _metricsPort;
    private readonly bool _isAggregator;
    private readonly string? _checkpointSyncUrl;
    private readonly int? _attestationCommitteeCount;
    private readonly int[]? _aggregateSubnetIds;

    public string NodeName => _nodeName;
    public string DataDir => _dataDir;
    public int ApiPort { get; }
    public bool IsRunning => _process is { HasExited: false };
    public int? ExitCode => _process is { HasExited: true } p ? p.ExitCode : null;
    public DateTime? ExitTime => _process is { HasExited: true } p ? p.ExitTime : null;

    public NodeProcess(
        string binaryPath,
        string customNetworkConfigDir,
        string nodeName,
        string dataDir,
        string nodeKeyPath,
        int socketPort,
        int apiPort,
        int metricsPort,
        bool isAggregator,
        string? checkpointSyncUrl = null,
        int? attestationCommitteeCount = null,
        int[]? aggregateSubnetIds = null)
    {
        _binaryPath = binaryPath;
        _customNetworkConfigDir = customNetworkConfigDir;
        _nodeName = nodeName;
        _dataDir = dataDir;
        _nodeKeyPath = nodeKeyPath;
        _socketPort = socketPort;
        ApiPort = apiPort;
        _metricsPort = metricsPort;
        _isAggregator = isAggregator;
        _checkpointSyncUrl = checkpointSyncUrl;
        _attestationCommitteeCount = attestationCommitteeCount;
        _aggregateSubnetIds = aggregateSubnetIds;
    }

    public void Start()
    {
        var configPath = Path.Combine(_dataDir, "node-config.json");
        var args = new StringBuilder();
        args.Append($"--config \"{configPath}\"");
        args.Append($" --custom-network-config-dir \"{_customNetworkConfigDir}\"");
        args.Append($" --node {_nodeName}");
        args.Append($" --data-dir \"{_dataDir}\"");
        args.Append($" --node-key \"{_nodeKeyPath}\"");
        args.Append($" --socket-port {_socketPort}");
        args.Append($" --api-port {ApiPort}");
        args.Append($" --metrics-port {_metricsPort}");

        if (_isAggregator)
        {
            args.Append(" --is-aggregator");
        }

        if (!string.IsNullOrWhiteSpace(_checkpointSyncUrl))
        {
            args.Append($" --checkpoint-sync-url {_checkpointSyncUrl}");
        }

        if (_attestationCommitteeCount.HasValue)
        {
            args.Append($" --attestation-committee-count {_attestationCommitteeCount.Value}");
        }

        if (_aggregateSubnetIds is { Length: > 0 })
        {
            args.Append($" --aggregate-subnet-ids {string.Join(",", _aggregateSubnetIds)}");
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

        // Capture native crash dumps so we can debug post-mortem when a child node SIGSEGVs.
        // Type=4 is "Full" (heap+threads); name uses %p for PID. Path overridable via env.
        var dumpDir = Environment.GetEnvironmentVariable("NLEAN_INTEG_DUMP_DIR")
            ?? Path.Combine(Path.GetTempPath(), "nlean-integ-dumps");
        Directory.CreateDirectory(dumpDir);
        psi.Environment["DOTNET_DbgEnableMiniDump"] = "1";
        psi.Environment["DOTNET_DbgMiniDumpType"] = "4";
        psi.Environment["DOTNET_DbgMiniDumpName"] = Path.Combine(dumpDir, $"{_nodeName}.%p.dmp");
        psi.Environment["DOTNET_CreateDumpDiagnostics"] = "1";

        _stdout.Clear();
        _stderr.Clear();

        // When NLEAN_INTEG_LOG_DIR is set, tee each node's stdout/stderr to its
        // own file. The in-memory StringBuilder only keeps the tail used for
        // inline diagnostics; the full log is the durable source — critical when
        // createdump output drowns the tail buffer on SIGSEGV.
        var logDir = Environment.GetEnvironmentVariable("NLEAN_INTEG_LOG_DIR");
        if (!string.IsNullOrWhiteSpace(logDir))
        {
            Directory.CreateDirectory(logDir);
            _stdoutLogPath = Path.Combine(logDir, $"{_nodeName}.stdout.log");
            _stderrLogPath = Path.Combine(logDir, $"{_nodeName}.stderr.log");
            _stdoutLog = new StreamWriter(_stdoutLogPath, append: false) { AutoFlush = true };
            _stderrLog = new StreamWriter(_stderrLogPath, append: false) { AutoFlush = true };
        }

        _process = new Process { StartInfo = psi };
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _stdout.AppendLine(e.Data);
            _stdoutLog?.WriteLine(e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            _stderr.AppendLine(e.Data);
            _stderrLog?.WriteLine(e.Data);
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
            // Send SIGTERM first to allow graceful shutdown (RocksDB flush).
            // Process.Kill() sends SIGKILL which skips cleanup.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    kill(_process.Id, SIGTERM);
                    if (_process.WaitForExit(5000))
                        return;
                }
                catch
                {
                    // Fall through to SIGKILL.
                }
            }

            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch
        {
            // Best-effort.
        }
    }

    private const int SIGTERM = 15;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

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
        try { _stdoutLog?.Dispose(); } catch { /* best-effort */ }
        try { _stderrLog?.Dispose(); } catch { /* best-effort */ }
        _stdoutLog = null;
        _stderrLog = null;
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
