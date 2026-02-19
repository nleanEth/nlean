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
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

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
                ApplyLibp2pIdentityDefaults(options, validatorNodeConfig);
                ApplyLibp2pBootstrapDefaults(options, validatorConfig, validatorNodeConfig);
                var validatorDutyConfig = BuildValidatorDutyConfig(options, validatorNodeConfig, chainConfig);
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
                services.AddSingleton<IForkChoiceStateTransition, ForkChoiceStateTransition>();
                services.AddSingleton<ForkChoiceStore>();
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

    private static void ApplyLibp2pBootstrapDefaults(
        NodeOptions options,
        ValidatorConfig? validatorConfig,
        ValidatorNodeConfig? validatorNodeConfig)
    {
        if (options.Libp2p.BootstrapPeers.Count > 0)
        {
            return;
        }

        if (validatorConfig?.Validators is null || validatorConfig.Validators.Count == 0)
        {
            return;
        }

        var nodeName = validatorNodeConfig?.Name ?? options.NodeName;
        var localClientPrefix = GetClientPrefix(nodeName);
        var bootstrapNodeNameFilter = BuildBootstrapNodeNameFilter(options.Libp2p.BootstrapNodeNames);
        var peers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in validatorConfig.Validators)
        {
            if (string.Equals(candidate.Name, nodeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (bootstrapNodeNameFilter is not null && !bootstrapNodeNameFilter.Contains(candidate.Name))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(localClientPrefix)
                && !string.Equals(GetClientPrefix(candidate.Name), localClientPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ip = candidate.EnrFields?.Ip?.Trim();
            var quicPort = candidate.EnrFields?.Quic;
            if (string.IsNullOrWhiteSpace(ip) || !quicPort.HasValue || quicPort.Value <= 0)
            {
                continue;
            }

            if (!IPAddress.TryParse(ip, out var parsedIp) || parsedIp.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            if (!TryDerivePeerId(candidate.Privkey, out var peerId))
            {
                continue;
            }

            peers.Add($"/ip4/{ip}/udp/{quicPort.Value}/quic-v1/p2p/{peerId}");
        }

        if (peers.Count > 0)
        {
            options.Libp2p.BootstrapPeers = peers.ToList();
        }
    }

    private static bool TryDerivePeerId(string? privateKeyHex, [NotNullWhen(true)] out string? peerId)
    {
        peerId = null;
        if (string.IsNullOrWhiteSpace(privateKeyHex))
        {
            return false;
        }

        var normalizedHex = privateKeyHex.Trim();
        if (normalizedHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalizedHex = normalizedHex[2..];
        }

        if (normalizedHex.Length != 64)
        {
            return false;
        }

        byte[] privateKeyBytes;
        try
        {
            privateKeyBytes = Convert.FromHexString(normalizedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        if (privateKeyBytes.Length != 32)
        {
            return false;
        }

        var identityPrivateKey = ToLibp2pSecpPrivateKey(privateKeyBytes);
        var identity = new Identity(identityPrivateKey, KeyType.Secp256K1);
        var derivedPeerId = identity.PeerId.ToString();
        if (string.IsNullOrWhiteSpace(derivedPeerId))
        {
            return false;
        }

        peerId = derivedPeerId;
        return true;
    }

    private static byte[] ToLibp2pSecpPrivateKey(byte[] privateKeyBytes)
    {
        if (privateKeyBytes.Length != 32)
        {
            return privateKeyBytes;
        }

        if ((privateKeyBytes[0] & 0x80) == 0)
        {
            return privateKeyBytes;
        }

        var encoded = new byte[33];
        Buffer.BlockCopy(privateKeyBytes, 0, encoded, 1, 32);
        return encoded;
    }

    private static string? GetClientPrefix(string? nodeName)
    {
        if (string.IsNullOrWhiteSpace(nodeName))
        {
            return null;
        }

        var trimmed = nodeName.Trim();
        var separator = trimmed.IndexOf('_');
        return separator > 0 ? trimmed[..separator] : trimmed;
    }

    private static HashSet<string>? BuildBootstrapNodeNameFilter(IReadOnlyList<string>? configuredNodeNames)
    {
        if (configuredNodeNames is null || configuredNodeNames.Count == 0)
        {
            return null;
        }

        var filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configuredNodeName in configuredNodeNames)
        {
            if (string.IsNullOrWhiteSpace(configuredNodeName))
            {
                continue;
            }

            filter.Add(configuredNodeName.Trim());
        }

        return filter.Count == 0 ? null : filter;
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
