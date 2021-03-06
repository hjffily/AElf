using System.Collections.Generic;
using AElf.Kernel.Account.Application;
using AElf.Kernel.Consensus.AEDPoS.Application;
using AElf.Kernel.Consensus.Application;
using AElf.Kernel.Consensus.Scheduler.RxNet;
using AElf.Kernel.TransactionPool.Application;
using AElf.Modularity;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;
using Volo.Abp.Threading;
using AElf.Kernel.SmartContract.Application;
using AElf.Kernel.SmartContract.ExecutionPluginForAcs1.FreeFeeTransactions;
using AElf.Kernel.SmartContractExecution.Application;
using InlineTransferFromValidationProvider = AElf.Kernel.Consensus.AEDPoS.Application.InlineTransferFromValidationProvider;
using AElf.Kernel.Txn.Application;

namespace AElf.Kernel.Consensus.AEDPoS
{
    [DependsOn(
        typeof(RxNetSchedulerAElfModule),
        typeof(ConsensusAElfModule)
    )]
    // ReSharper disable once InconsistentNaming
    public class AEDPoSAElfModule : AElfModule
    {
        public override void ConfigureServices(ServiceConfigurationContext context)
        {
            // ITriggerInformationProvider is for generating some necessary information
            // to trigger consensus hints from consensus contract.
            context.Services.AddSingleton<ITriggerInformationProvider, AEDPoSTriggerInformationProvider>();

            // IConsensusExtraDataExtractor is for extracting consensus data from extra data in Block Header.
            context.Services.AddTransient<IConsensusExtraDataExtractor, AEDPoSExtraDataExtractor>();

            // IBroadcastPrivilegedPubkeyListProvider is just a helper for network module
            // to broadcast blocks to nodes of higher priority.
            context.Services
                .AddSingleton<IBroadcastPrivilegedPubkeyListProvider, AEDPoSBroadcastPrivilegedPubkeyListProvider>();

            // IConstrainedTransactionValidationProvider is to limit the number of
            // consensus transactions packaged to one single block.
            context.Services
                .AddSingleton<IConstrainedTransactionValidationProvider,
                    ConstrainedAEDPoSTransactionValidationProvider>();

            context.Services.AddSingleton(typeof(ContractEventDiscoveryService<>));
            context.Services.AddSingleton<IBestChainFoundLogEventHandler, IrreversibleBlockFoundLogEventHandler>();
            context.Services
                .AddSingleton<IBestChainFoundLogEventHandler, IrreversibleBlockHeightUnacceptableLogEventHandler>();
            context.Services.AddSingleton<IBestChainFoundLogEventHandler, SecretSharingInformationLogEventHandler>();

            context.Services.AddSingleton<IChargeFeeStrategy, ConsensusContractChargeFeeStrategy>();

            context.Services.AddSingleton<ITransactionValidationProvider, NotAllowEnterTxHubValidationProvider>();

            //context.Services.AddSingleton<IInlineTransactionValidationProvider, InlineTransferFromValidationProvider>();

            // Our purpose is that other modules won't sense which consensus protocol are using, 
            // thus we read the configuration of ConsensusOption here.
            // (ConsensusOption itself can support all kinds of consensus protocol via adding more properties.)
            var configuration = context.Services.GetConfiguration();
            Configure<ConsensusOptions>(option =>
            {
                var consensusOptions = configuration.GetSection("Consensus");
                consensusOptions.Bind(option);

                var startTimeStamp = consensusOptions["StartTimestamp"];
                option.StartTimestamp = new Timestamp
                {
                    Seconds = string.IsNullOrEmpty(startTimeStamp) ? 0 : long.Parse(startTimeStamp)
                };

                if (option.InitialMinerList == null || option.InitialMinerList.Count == 0 ||
                    string.IsNullOrWhiteSpace(option.InitialMinerList[0]))
                {
                    // If InitialMinerList isn't configured yet, then read AccountService and config current user as single initial miner.
                    AsyncHelper.RunSync(async () =>
                    {
                        var accountService = context.Services.GetRequiredServiceLazy<IAccountService>().Value;
                        var publicKey = (await accountService.GetPublicKeyAsync()).ToHex();
                        option.InitialMinerList = new List<string> {publicKey};
                    });
                }
            });
        }
    }
}