using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lean.Crypto;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;

namespace Lean.Integration.Tests;

public sealed class DevnetFixture : IDisposable
{
    public string RootDir { get; }
    public string ConfigDir { get; }
    public int NodeCount { get; }
    public string[] DataDirs { get; }
    public string[] NodeNames { get; }
    public int[] QuicPorts { get; }
    public int[] ApiPorts { get; }
    public int[] MetricsPorts { get; }
    public ulong GenesisTime { get; }
    public string BinaryPath { get; }
    public string[] PeerIds { get; }

    public string HashSigKeyDir { get; }
    public int ValidatorsPerNode { get; }
    public int AttestationCommitteeCount { get; }

    private const uint ActiveEpochExponent = 18;
    private const uint NumActiveEpochs = 1 << (int)ActiveEpochExponent;

    public DevnetFixture(int nodeCount = 4, int basePort = 19100, int validatorsPerNode = 1, int attestationCommitteeCount = 1)
    {
        NodeCount = nodeCount;
        ValidatorsPerNode = validatorsPerNode;
        AttestationCommitteeCount = attestationCommitteeCount;
        RootDir = Path.Combine(Path.GetTempPath(), $"nlean-integ-{Guid.NewGuid():N}");
        ConfigDir = Path.Combine(RootDir, "config");
        Directory.CreateDirectory(ConfigDir);

        var keyDir = Path.Combine(ConfigDir, "hash-sig-keys");
        Directory.CreateDirectory(keyDir);
        HashSigKeyDir = keyDir;

        DataDirs = new string[nodeCount];
        NodeNames = new string[nodeCount];
        QuicPorts = new int[nodeCount];
        ApiPorts = new int[nodeCount];
        MetricsPorts = new int[nodeCount];
        PeerIds = new string[nodeCount];

        for (int i = 0; i < nodeCount; i++)
        {
            NodeNames[i] = $"nlean_{i}";
            QuicPorts[i] = basePort + i;
            MetricsPorts[i] = basePort + 100 + i;
            ApiPorts[i] = basePort + 200 + i;
            DataDirs[i] = Path.Combine(RootDir, $"data_{i}");
            Directory.CreateDirectory(DataDirs[i]);
        }

        BinaryPath = ResolveBinaryPath();

        var keyList = GenerateKeys(keyDir);
        GenerateLibp2pKeys();
        GenesisTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30;
        WriteConfigYaml(keyList);
        WriteValidatorConfigYaml();
        WriteBootstrapConfig();
    }

    public NodeProcess CreateNodeProcess(int index, string? checkpointSyncUrl = null)
    {
        var validatorConfigPath = Path.Combine(ConfigDir, "validator-config.yaml");
        var nodeKeyPath = Path.Combine(ConfigDir, $"nlean_{index}.key");
        var isAggregator = index == 0;

        int? committeeCount = AttestationCommitteeCount > 1 ? AttestationCommitteeCount : null;
        int[]? aggregateSubnetIds = null;
        if (isAggregator && AttestationCommitteeCount > 1)
        {
            aggregateSubnetIds = Enumerable.Range(0, AttestationCommitteeCount).ToArray();
        }

        return new NodeProcess(
            BinaryPath,
            validatorConfigPath,
            NodeNames[index],
            DataDirs[index],
            network: "integ-test",
            nodeKeyPath,
            QuicPorts[index],
            ApiPorts[index],
            MetricsPorts[index],
            isAggregator,
            HashSigKeyDir,
            checkpointSyncUrl: checkpointSyncUrl,
            attestationCommitteeCount: committeeCount,
            aggregateSubnetIds: aggregateSubnetIds);
    }

    private List<(string AttestHex, string ProposeHex)> GenerateKeys(string keyDir)
    {
        var totalValidators = NodeCount * ValidatorsPerNode;
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "nlean-integ-keys");
        Directory.CreateDirectory(cacheDir);

        var keyList = new (string AttestHex, string ProposeHex)[totalValidators];

        bool allCached = Enumerable.Range(0, totalValidators).All(i =>
            File.Exists(Path.Combine(cacheDir, $"validator_{i}_attester_key_pk.ssz")) &&
            File.Exists(Path.Combine(cacheDir, $"validator_{i}_attester_key_sk.ssz")) &&
            File.Exists(Path.Combine(cacheDir, $"validator_{i}_proposer_key_pk.ssz")) &&
            File.Exists(Path.Combine(cacheDir, $"validator_{i}_proposer_key_sk.ssz")));

        if (allCached)
        {
            for (int i = 0; i < totalValidators; i++)
            {
                foreach (var suffix in new[] { "attester_key_pk", "attester_key_sk", "proposer_key_pk", "proposer_key_sk" })
                {
                    var fileName = $"validator_{i}_{suffix}.ssz";
                    File.Copy(Path.Combine(cacheDir, fileName), Path.Combine(keyDir, fileName), overwrite: true);
                }

                var attestPk = File.ReadAllBytes(Path.Combine(keyDir, $"validator_{i}_attester_key_pk.ssz"));
                var proposePk = File.ReadAllBytes(Path.Combine(keyDir, $"validator_{i}_proposer_key_pk.ssz"));
                keyList[i] = (Convert.ToHexString(attestPk).ToLowerInvariant(),
                              Convert.ToHexString(proposePk).ToLowerInvariant());
            }
        }
        else
        {
            Parallel.For(0, totalValidators, i =>
            {
                var sig = new RustLeanSig();
                var attestKp = sig.GenerateKeyPair(0, NumActiveEpochs);
                var proposeKp = sig.GenerateKeyPair(0, NumActiveEpochs);

                foreach (var dir in new[] { keyDir, cacheDir })
                {
                    File.WriteAllBytes(Path.Combine(dir, $"validator_{i}_attester_key_pk.ssz"), attestKp.PublicKey);
                    File.WriteAllBytes(Path.Combine(dir, $"validator_{i}_attester_key_sk.ssz"), attestKp.SecretKey);
                    File.WriteAllBytes(Path.Combine(dir, $"validator_{i}_proposer_key_pk.ssz"), proposeKp.PublicKey);
                    File.WriteAllBytes(Path.Combine(dir, $"validator_{i}_proposer_key_sk.ssz"), proposeKp.SecretKey);
                }

                keyList[i] = (Convert.ToHexString(attestKp.PublicKey).ToLowerInvariant(),
                              Convert.ToHexString(proposeKp.PublicKey).ToLowerInvariant());
            });
        }

        return keyList.ToList();
    }

    private void GenerateLibp2pKeys()
    {
        for (int i = 0; i < NodeCount; i++)
        {
            var keyBytes = RandomNumberGenerator.GetBytes(32);
            var hex = Convert.ToHexString(keyBytes).ToLowerInvariant();
            File.WriteAllText(Path.Combine(ConfigDir, $"nlean_{i}.key"), hex);

            // Derive peer ID from private key (mirrors Libp2pIdentityFactory.EncodeSecpPrivateKeyForLibp2p)
            byte[] encoded;
            if ((keyBytes[0] & 0x80) != 0)
            {
                encoded = new byte[33];
                Buffer.BlockCopy(keyBytes, 0, encoded, 1, 32);
            }
            else
            {
                encoded = keyBytes;
            }
            var identity = new Identity(encoded, KeyType.Secp256K1);
            PeerIds[i] = identity.PeerId.ToString();
        }
    }

    private void WriteConfigYaml(List<(string AttestHex, string ProposeHex)> keyList)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Genesis Settings");
        sb.AppendLine($"GENESIS_TIME: {GenesisTime}");
        sb.AppendLine();
        sb.AppendLine("# Timing");
        var secondsPerSlot = ResolveSecondsPerSlot();
        sb.AppendLine($"SECONDS_PER_SLOT: {secondsPerSlot}");
        sb.AppendLine();
        sb.AppendLine("# Key Settings");
        sb.AppendLine($"ACTIVE_EPOCH: {ActiveEpochExponent}");
        sb.AppendLine();
        sb.AppendLine("# Validator Settings");
        sb.AppendLine($"VALIDATOR_COUNT: {NodeCount * ValidatorsPerNode}");
        sb.AppendLine($"ATTESTATION_COMMITTEE_COUNT: {AttestationCommitteeCount}");
        sb.AppendLine();
        sb.AppendLine("# Genesis Validator Pubkeys");
        sb.AppendLine("GENESIS_VALIDATORS:");
        foreach (var (attestHex, proposeHex) in keyList)
        {
            sb.AppendLine($"    - attestation_pubkey: \"{attestHex}\"");
            sb.AppendLine($"      proposal_pubkey: \"{proposeHex}\"");
        }

        File.WriteAllText(Path.Combine(ConfigDir, "config.yaml"), sb.ToString());
    }

    private void WriteValidatorConfigYaml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("shuffle: roundrobin");
        sb.AppendLine("deployment_mode: local");
        sb.AppendLine("config:");
        sb.AppendLine($"  activeEpoch: {ActiveEpochExponent}");
        sb.AppendLine("  keyType: \"hash-sig\"");
        sb.AppendLine("validators:");

        for (int i = 0; i < NodeCount; i++)
        {
            var privkey = File.ReadAllText(Path.Combine(ConfigDir, $"nlean_{i}.key")).Trim();
            sb.AppendLine($"  - name: \"nlean_{i}\"");
            sb.AppendLine($"    privkey: \"{privkey}\"");
            sb.AppendLine("    enrFields:");
            sb.AppendLine("      ip: \"127.0.0.1\"");
            sb.AppendLine($"      quic: {QuicPorts[i]}");
            sb.AppendLine($"    metricsPort: {MetricsPorts[i]}");
            sb.AppendLine($"    count: {ValidatorsPerNode}");
            sb.AppendLine($"    isAggregator: {(i == 0 ? "true" : "false")}");
        }

        File.WriteAllText(Path.Combine(ConfigDir, "validator-config.yaml"), sb.ToString());
    }

    private void WriteBootstrapConfig()
    {
        var bootstrapPeers = new List<string>();
        for (int i = 0; i < NodeCount; i++)
        {
            bootstrapPeers.Add($"/ip4/127.0.0.1/udp/{QuicPorts[i]}/quic-v1/p2p/{PeerIds[i]}");
        }

        for (int i = 0; i < NodeCount; i++)
        {
            var config = new
            {
                libp2p = new
                {
                    bootstrapPeers,
                    enableMdns = false,
                    enablePubsub = true,
                    enableQuic = true
                }
            };

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(Path.Combine(DataDirs[i], "node-config.json"), json);
        }
    }

    private static int ResolveSecondsPerSlot()
    {
        var overrideValue = Environment.GetEnvironmentVariable("NLEAN_INTEG_SECONDS_PER_SLOT");
        if (int.TryParse(overrideValue, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        return Environment.GetEnvironmentVariable("CI") == "true" ? 2 : 1;
    }

    private static string ResolveBinaryPath()
    {
        var envPath = Environment.GetEnvironmentVariable("NLEAN_BINARY_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "artifacts", "lean-client", "Lean.Client");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }

        var sln = FindSolutionRoot();
        if (sln is not null)
        {
            var candidate = Path.Combine(sln, "artifacts", "lean-client", "Lean.Client");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Cannot find Lean.Client binary. Run: dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -o artifacts/lean-client --self-contained false\n" +
            "Or set NLEAN_BINARY_PATH environment variable.");
    }

    private static string? FindSolutionRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Lean.sln")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootDir))
            {
                Directory.Delete(RootDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
