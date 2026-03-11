using Lean.Consensus;
using Lean.Consensus.Chain;
using Lean.Consensus.ForkChoice;
using Lean.Consensus.Sync;
using Lean.Crypto;
using Lean.Metrics;
using Lean.Network;
using Lean.Node.Configuration;
using Lean.Storage;
using Lean.Validator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using System.Net;
using System.Text;
using ConsensusService = Lean.Consensus.ConsensusServiceV2;

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
                var (validatorConfig, validatorNodeConfig, chainConfig) = TryLoadValidatorNodeConfig(options);
                ApplyBootstrapPeersFromNodesYaml(options);
                ApplyLibp2pIdentityDefaults(options, validatorNodeConfig);
                var validatorDutyConfig = BuildValidatorDutyConfig(options, validatorNodeConfig, chainConfig);
                options.Consensus.IsAggregator = validatorDutyConfig.PublishAggregates;
                options.Consensus.LocalValidatorId = validatorDutyConfig.ValidatorIndex;
                services.AddSingleton(options);
                services.AddSingleton(options.Libp2p);
                services.AddSingleton(options.Consensus);
                services.AddSingleton(options.Metrics);
                services.AddSingleton(options.Storage);
                services.AddSingleton<IGossipTopicProvider>(_ => new GossipTopicProvider(options.Network));
                services.AddSingleton(validatorDutyConfig);
                services.AddSingleton<IKeyValueStore>(_ => new RocksDbKeyValueStore(options.Storage, "consensus"));
                services.AddSingleton<IConsensusStateStore, ConsensusStateStore>();
                services.AddSingleton<IBlockByRootStore, BlockByRootStore>();
                services.AddSingleton<IBlocksByRootRpcRouter, BlocksByRootRpcRouter>();
                services.AddSingleton<IStatusRpcRouter, StatusRpcRouter>();
                services.AddSingleton<SignedBlockWithAttestationGossipDecoder>();
                services.AddSingleton<SignedAttestationGossipDecoder>();
                services.AddSingleton<SignedAggregatedAttestationGossipDecoder>();
                services.AddSingleton(sp => new ProtoArrayForkChoiceStore(
                    sp.GetRequiredService<ConsensusConfig>(),
                    sp.GetRequiredService<IConsensusStateStore>(),
                    sp.GetService<ILogger<ProtoArrayForkChoiceStore>>()));
                services.AddSingleton<ITimeSource, SystemTimeSource>();
                services.AddSingleton(sp =>
                {
                    var consensus = sp.GetRequiredService<ConsensusConfig>();
                    return new SlotClock(
                        consensus.GenesisTimeUnix,
                        Math.Max(1, consensus.SecondsPerSlot),
                        ProtoArrayForkChoiceStore.IntervalsPerSlot,
                        sp.GetRequiredService<ITimeSource>());
                });
                services.AddSingleton<ChainStateCache>();
                services.AddSingleton<IBlockProcessor, ProtoArrayBlockProcessor>();
                services.AddSingleton<IAttestationSink>(sp =>
                {
                    var store = sp.GetRequiredService<ProtoArrayForkChoiceStore>();
                    var consensus = sp.GetRequiredService<ConsensusConfig>();
                    return new ProtoArrayAttestationSink(store, consensus.IsAggregator);
                });
                services.AddSingleton<SyncPeerManager>();
                services.AddSingleton(sp =>
                {
                    var consensus = sp.GetRequiredService<ConsensusConfig>();
                    return new NewBlockCache(Math.Max(1, consensus.MaxOrphanBlocks));
                });
                services.AddSingleton<INetworkRequester, Libp2pNetworkRequester>();
                services.AddSingleton<ISyncService, SyncService>();
                services.AddSingleton<LeanBlocksByRootProtocol>();
                services.AddSingleton<LeanStatusProtocol>();

                services.AddLibp2p(libp2pBuilder =>
                {
                    var blocksByRootProtocol = libp2pBuilder.ServiceProvider.GetRequiredService<LeanBlocksByRootProtocol>();
                    var statusProtocol = libp2pBuilder.ServiceProvider.GetRequiredService<LeanStatusProtocol>();

                    libp2pBuilder.AddProtocol(blocksByRootProtocol, isExposed: true);
                    libp2pBuilder.AddProtocol(statusProtocol, isExposed: true);

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
                services.AddSingleton(new IdentifyProtocolSettings
                {
                    ProtocolVersion = "",
                    AgentVersion = "nlean",
                    PeerRecordsVerificationPolicy = PeerRecordsVerificationPolicy.DoesNotRequire
                });
                services.AddSingleton(BuildPubsubSettings());

                services.AddSingleton<INetworkService, Libp2pNetworkService>();
                services.AddSingleton<IConsensusService, ConsensusService>();
                services.AddSingleton<IMetricsService, PrometheusMetricsService>();
                services.AddSingleton<IValidatorService, ValidatorService>();
                services.AddSingleton<ILeanSig, RustLeanSig>();
                services.AddSingleton<ILeanMultiSig, RustLeanMultiSig>();

                services.AddHostedService<NodeService>();
            });

        return builder.Build();
    }

    private static void ApplyBootstrapPeersFromNodesYaml(NodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Libp2p.BootstrapPeers.Count > 0 || string.IsNullOrWhiteSpace(options.ValidatorConfigPath))
        {
            return;
        }

        var validatorConfigDirectory = Path.GetDirectoryName(options.ValidatorConfigPath);
        if (string.IsNullOrWhiteSpace(validatorConfigDirectory))
        {
            return;
        }

        var nodesYamlPath = Path.Combine(validatorConfigDirectory, "nodes.yaml");
        if (!File.Exists(nodesYamlPath))
        {
            return;
        }

        List<string> enrs;
        try
        {
            var yaml = File.ReadAllText(nodesYamlPath);
            var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build();
            enrs = deserializer.Deserialize<List<string>>(yaml) ?? new List<string>();
        }
        catch
        {
            return;
        }

        var bootstrapPeers = options.Libp2p.BootstrapPeers;
        var dedup = new HashSet<string>(bootstrapPeers, StringComparer.Ordinal);
        foreach (var rawEnr in enrs)
        {
            if (!TryBuildBootstrapPeerFromEnr(rawEnr, out var address))
            {
                continue;
            }

            if (dedup.Add(address))
            {
                bootstrapPeers.Add(address);
            }
        }
    }

    private static bool TryBuildBootstrapPeerFromEnr(string? rawEnr, out string address)
    {
        address = string.Empty;
        if (string.IsNullOrWhiteSpace(rawEnr))
        {
            return false;
        }

        var enr = rawEnr.Trim();
        if (enr.StartsWith("enr:", StringComparison.OrdinalIgnoreCase))
        {
            enr = enr[4..];
        }

        var enrBase64 = enr.Replace('-', '+').Replace('_', '/');
        var remainder = enrBase64.Length % 4;
        if (remainder != 0)
        {
            enrBase64 = enrBase64 + new string('=', 4 - remainder);
        }

        byte[] enrRlp;
        try
        {
            enrRlp = Convert.FromBase64String(enrBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!TryDecodeFlatRlpList(enrRlp, out var enrFields) || enrFields.Count < 4)
        {
            return false;
        }

        var enrMap = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var i = 2; i + 1 < enrFields.Count; i += 2)
        {
            var key = Encoding.ASCII.GetString(enrFields[i]);
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            enrMap[key] = enrFields[i + 1];
        }

        if (!enrMap.TryGetValue("secp256k1", out var secpPublicKey) || secpPublicKey.Length == 0)
        {
            return false;
        }

        if (!enrMap.TryGetValue("quic", out var quicRaw) || !TryDecodeBigEndianPort(quicRaw, out var quicPort))
        {
            return false;
        }

        if (!TryGetIpAddress(enrMap, out var ipFamily, out var ipAddress))
        {
            return false;
        }

        string peerId;
        try
        {
            var publicKey = new PublicKey
            {
                Type = KeyType.Secp256K1,
                Data = ByteString.CopyFrom(secpPublicKey)
            };

            peerId = new PeerId(publicKey).ToString();
        }
        catch
        {
            return false;
        }

        address = $"/{ipFamily}/{ipAddress}/udp/{quicPort}/quic-v1/p2p/{peerId}";
        return true;
    }

    private static bool TryGetIpAddress(
        IReadOnlyDictionary<string, byte[]> enrMap,
        out string ipFamily,
        out string ipAddress)
    {
        ipFamily = string.Empty;
        ipAddress = string.Empty;

        if (enrMap.TryGetValue("ip", out var ipRaw))
        {
            if (!TryDecodeIp(ipRaw, out ipFamily, out ipAddress))
            {
                return false;
            }

            return true;
        }

        if (enrMap.TryGetValue("ip6", out var ip6Raw))
        {
            return TryDecodeIp(ip6Raw, out ipFamily, out ipAddress);
        }

        return false;
    }

    private static bool TryDecodeIp(byte[] raw, out string ipFamily, out string ipAddress)
    {
        ipFamily = string.Empty;
        ipAddress = string.Empty;

        if (raw.Length is not (4 or 16))
        {
            return false;
        }

        try
        {
            ipAddress = new IPAddress(raw).ToString();
            ipFamily = raw.Length == 4 ? "ip4" : "ip6";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDecodeBigEndianPort(byte[] raw, out int port)
    {
        port = 0;
        if (raw.Length == 0 || raw.Length > 2)
        {
            return false;
        }

        var value = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            value = (value << 8) | raw[i];
        }

        if (value is <= 0 or > 65535)
        {
            return false;
        }

        port = value;
        return true;
    }

    private static bool TryDecodeFlatRlpList(byte[] encoded, out List<byte[]> items)
    {
        items = new List<byte[]>();
        if (!TryReadRlpItem(encoded, 0, out var rootIsList, out var rootPayloadOffset, out var rootPayloadLength, out var rootConsumed))
        {
            return false;
        }

        if (!rootIsList || rootConsumed != encoded.Length)
        {
            return false;
        }

        var offset = rootPayloadOffset;
        var end = rootPayloadOffset + rootPayloadLength;
        while (offset < end)
        {
            if (!TryReadRlpItem(encoded, offset, out var itemIsList, out var itemPayloadOffset, out var itemPayloadLength, out var consumed))
            {
                return false;
            }

            if (itemIsList)
            {
                return false;
            }

            items.Add(encoded.AsSpan(itemPayloadOffset, itemPayloadLength).ToArray());
            offset += consumed;
        }

        return offset == end;
    }

    private static bool TryReadRlpItem(
        byte[] source,
        int offset,
        out bool isList,
        out int payloadOffset,
        out int payloadLength,
        out int consumed)
    {
        isList = false;
        payloadOffset = 0;
        payloadLength = 0;
        consumed = 0;

        if ((uint)offset >= (uint)source.Length)
        {
            return false;
        }

        var prefix = source[offset];
        if (prefix <= 0x7f)
        {
            isList = false;
            payloadOffset = offset;
            payloadLength = 1;
            consumed = 1;
            return true;
        }

        if (prefix <= 0xb7)
        {
            var len = prefix - 0x80;
            var dataOffset = offset + 1;
            if (!HasAvailableBytes(source, dataOffset, len))
            {
                return false;
            }

            isList = false;
            payloadOffset = dataOffset;
            payloadLength = len;
            consumed = 1 + len;
            return true;
        }

        if (prefix <= 0xbf)
        {
            var lenOfLen = prefix - 0xb7;
            var lengthOffset = offset + 1;
            if (!HasAvailableBytes(source, lengthOffset, lenOfLen))
            {
                return false;
            }

            if (!TryReadBigEndianLength(source.AsSpan(lengthOffset, lenOfLen), out var len))
            {
                return false;
            }

            var dataOffset = lengthOffset + lenOfLen;
            if (!HasAvailableBytes(source, dataOffset, len))
            {
                return false;
            }

            isList = false;
            payloadOffset = dataOffset;
            payloadLength = len;
            consumed = 1 + lenOfLen + len;
            return true;
        }

        if (prefix <= 0xf7)
        {
            var len = prefix - 0xc0;
            var dataOffset = offset + 1;
            if (!HasAvailableBytes(source, dataOffset, len))
            {
                return false;
            }

            isList = true;
            payloadOffset = dataOffset;
            payloadLength = len;
            consumed = 1 + len;
            return true;
        }

        {
            var lenOfLen = prefix - 0xf7;
            var lengthOffset = offset + 1;
            if (!HasAvailableBytes(source, lengthOffset, lenOfLen))
            {
                return false;
            }

            if (!TryReadBigEndianLength(source.AsSpan(lengthOffset, lenOfLen), out var len))
            {
                return false;
            }

            var dataOffset = lengthOffset + lenOfLen;
            if (!HasAvailableBytes(source, dataOffset, len))
            {
                return false;
            }

            isList = true;
            payloadOffset = dataOffset;
            payloadLength = len;
            consumed = 1 + lenOfLen + len;
            return true;
        }
    }

    private static bool TryReadBigEndianLength(ReadOnlySpan<byte> bytes, out int length)
    {
        length = 0;
        if (bytes.Length == 0 || bytes.Length > sizeof(int))
        {
            return false;
        }

        var value = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            value = (value << 8) | bytes[i];
        }

        if (value < 0)
        {
            return false;
        }

        length = value;
        return true;
    }

    private static bool HasAvailableBytes(byte[] source, int offset, int length)
    {
        if (offset < 0 || length < 0)
        {
            return false;
        }

        return offset <= source.Length && length <= source.Length - offset;
    }

    private static void ApplyLibp2pIdentityDefaults(NodeOptions options, ValidatorNodeConfig? validatorNodeConfig)
    {
        if (!string.IsNullOrWhiteSpace(options.Libp2p.PrivateKeyHex)
            || !string.IsNullOrWhiteSpace(options.Libp2p.PrivateKeyPath))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(validatorNodeConfig?.Privkey))
        {
            options.Libp2p.PrivateKeyHex = validatorNodeConfig.Privkey;
        }
    }

    private static (ValidatorConfig? Config, ValidatorNodeConfig? Node, LeanChainConfig? ChainConfig) TryLoadValidatorNodeConfig(NodeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ValidatorConfigPath))
        {
            return (null, null, null);
        }

        try
        {
            var validatorConfig = ValidatorConfig.Load(options.ValidatorConfigPath);
            var chainConfig = ApplyChainTimingConfig(options);
            ApplyInitialValidatorCount(options, validatorConfig, chainConfig);
            return (validatorConfig, validatorConfig.FindNode(options.NodeName), chainConfig);
        }
        catch
        {
            // Runtime startup will surface config loading errors in NodeService logs.
            return (null, null, null);
        }
    }

    private static LeanChainConfig? ApplyChainTimingConfig(NodeOptions options)
    {
        var validatorConfigPath = options.ValidatorConfigPath;
        LeanChainConfig? chainConfig;
        try
        {
            chainConfig = LeanChainConfig.TryLoad(validatorConfigPath!);
        }
        catch
        {
            return null;
        }

        if (chainConfig is null)
        {
            return null;
        }

        if (chainConfig.SecondsPerSlot is int configuredSecondsPerSlot && configuredSecondsPerSlot > 0)
        {
            options.Consensus.SecondsPerSlot = configuredSecondsPerSlot;
        }

        if (chainConfig.GenesisTime > 0)
        {
            options.Consensus.GenesisTimeUnix = chainConfig.GenesisTime;
        }

        return chainConfig;
    }

    private static void ApplyInitialValidatorCount(
        NodeOptions options,
        ValidatorConfig validatorConfig,
        LeanChainConfig? chainConfig)
    {
        if (chainConfig?.GenesisValidators is { Count: > 0 } genesisValidators)
        {
            options.Consensus.GenesisValidatorPublicKeys = genesisValidators;
        }

        var configuredValidatorCount = chainConfig?.ValidatorCount
            ?? (chainConfig?.GenesisValidators is { Count: > 0 } validators
                ? (ulong)validators.Count
                : 0UL);
        if (configuredValidatorCount > 0)
        {
            options.Consensus.InitialValidatorCount = configuredValidatorCount;
            return;
        }

        ulong totalValidators = 0;
        foreach (var validator in validatorConfig.Validators)
        {
            totalValidators += (ulong)Math.Max(0, validator.Count);
        }

        if (totalValidators > 0)
        {
            options.Consensus.InitialValidatorCount = totalValidators;
        }
    }

    private static ValidatorDutyConfig BuildValidatorDutyConfig(
        NodeOptions options,
        ValidatorNodeConfig? validatorNodeConfig,
        LeanChainConfig? chainConfig)
    {
        var secretKeyHex = options.Validator.SecretKeyHex;
        if (string.IsNullOrWhiteSpace(secretKeyHex) && string.IsNullOrWhiteSpace(options.Validator.SecretKeyPath))
        {
            if (!string.IsNullOrWhiteSpace(validatorNodeConfig?.Privkey))
            {
                secretKeyHex = validatorNodeConfig.Privkey;
            }
        }

        return new ValidatorDutyConfig
        {
            PublicKeyHex = options.Validator.PublicKeyHex,
            SecretKeyHex = secretKeyHex,
            PublicKeyPath = options.Validator.PublicKeyPath,
            SecretKeyPath = options.Validator.SecretKeyPath,
            ValidatorIndex = options.Validator.ValidatorIndex,
            ActivationEpoch = options.Validator.ActivationEpoch,
            NumActiveEpochs = options.Validator.NumActiveEpochs,
            GenesisValidatorPublicKeys = chainConfig?.GenesisValidators ?? (IReadOnlyList<string>)Array.Empty<string>(),
            PublishAggregates = options.Validator.PublishAggregates
        };
    }

    private static PubsubSettings BuildPubsubSettings()
    {
        // Keep pubsub signature policy aligned with current Ream/Zeam images.
        // Lean gossip currently uses unsigned envelopes.
        var defaults = PubsubSettings.Default;
        return new PubsubSettings
        {
            ReconnectionAttempts = defaults.ReconnectionAttempts,
            ReconnectionPeriod = defaults.ReconnectionPeriod,
            Degree = defaults.Degree,
            LowestDegree = defaults.LowestDegree,
            HighestDegree = defaults.HighestDegree,
            LazyDegree = defaults.LazyDegree,
            MaxConnections = defaults.MaxConnections,
            HeartbeatInterval = defaults.HeartbeatInterval,
            FanoutTtl = defaults.FanoutTtl,
            mcache_len = defaults.mcache_len,
            mcache_gossip = defaults.mcache_gossip,
            MessageCacheTtl = defaults.MessageCacheTtl,
            DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
            MaxIdontwantMessages = defaults.MaxIdontwantMessages,
            GetMessageId = LeanPubsubMessageId.Compute
        };
    }
}
