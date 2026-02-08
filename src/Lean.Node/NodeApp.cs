using Lean.Consensus;
using Lean.Crypto;
using Lean.Metrics;
using Lean.Network;
using Lean.Node.Configuration;
using Lean.Storage;
using Lean.Validator;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Protocols;

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
                services.AddSingleton(options.Consensus);
                services.AddSingleton(options.Metrics);
                services.AddSingleton(options.Storage);
                services.AddSingleton(new ValidatorDutyConfig
                {
                    PublicKeyHex = options.Validator.PublicKeyHex,
                    SecretKeyHex = options.Validator.SecretKeyHex,
                    ValidatorIndex = options.Validator.ValidatorIndex,
                    ActivationEpoch = options.Validator.ActivationEpoch,
                    NumActiveEpochs = options.Validator.NumActiveEpochs,
                    PublishAggregates = options.Validator.PublishAggregates
                });
                services.AddSingleton<IKeyValueStore>(_ => new RocksDbKeyValueStore(options.Storage, "consensus"));
                services.AddSingleton<IConsensusStateStore, ConsensusStateStore>();
                services.AddSingleton<IBlockByRootStore, BlockByRootStore>();
                services.AddSingleton<IBlocksByRootRpcRouter, BlocksByRootRpcRouter>();
                services.AddSingleton<SignedBlockWithAttestationGossipDecoder>();
                services.AddSingleton<SignedAttestationGossipDecoder>();
                services.AddSingleton<IForkChoiceStateTransition, Devnet2ForkChoiceStateTransition>();
                services.AddSingleton<ForkChoiceStore>();

                services.AddLibp2p(libp2pBuilder =>
                {
                    var blocksByRootRouter = libp2pBuilder.ServiceProvider.GetRequiredService<IBlocksByRootRpcRouter>();
                    libp2pBuilder.AddRequestResponseProtocol<Google.Protobuf.WellKnownTypes.BytesValue, Google.Protobuf.WellKnownTypes.BytesValue>(
                        RpcProtocols.BlocksByRoot,
                        async (request, _) =>
                        {
                            var payload = await blocksByRootRouter.ResolveAsync(request.Value.ToByteArray(), CancellationToken.None);
                            return new Google.Protobuf.WellKnownTypes.BytesValue
                            {
                                Value = payload is null ? ByteString.Empty : ByteString.CopyFrom(payload)
                            };
                        },
                        isExposed: true);

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
