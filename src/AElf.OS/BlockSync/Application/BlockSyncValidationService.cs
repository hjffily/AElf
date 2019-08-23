using System.Collections.Generic;
using System.Threading.Tasks;
using AElf.Kernel;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.Blockchain.Domain;
using AElf.Kernel.SmartContractExecution.Application;
using AElf.OS.BlockSync.Infrastructure;
using AElf.OS.Network;
using AElf.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AElf.OS.BlockSync.Application
{
    public class BlockSyncValidationService : IBlockSyncValidationService
    {
        private readonly IAnnouncementCacheProvider _announcementCacheProvider;
        private readonly IBlockValidationService _blockValidationService;
        private readonly ITransactionManager _transactionManager;

        public ILogger<BlockSyncValidationService> Logger { get; set; }

        private readonly IEnumerable<ITransactionValidationProvider> _transactionValidationProviders;

        public BlockSyncValidationService(IAnnouncementCacheProvider announcementCacheProvider,
            IBlockValidationService blockValidationService, ITransactionManager transactionManager,
            IEnumerable<ITransactionValidationProvider> transactionValidationProviders)
        {
            Logger = NullLogger<BlockSyncValidationService>.Instance;

            _announcementCacheProvider = announcementCacheProvider;
            _blockValidationService = blockValidationService;
            _transactionManager = transactionManager;
            _transactionValidationProviders = transactionValidationProviders;
        }

        public async Task<bool> ValidateAnnouncementAsync(Chain chain, BlockAnnouncement blockAnnouncement, string senderPubKey)
        {
            if (!TryCacheNewAnnouncement(blockAnnouncement.BlockHash, blockAnnouncement.BlockHeight, senderPubKey))
            {
                return false;
            }

            if (blockAnnouncement.BlockHeight <= chain.LastIrreversibleBlockHeight)
            {
                Logger.LogWarning(
                    $"Receive lower header {{ hash: {blockAnnouncement.BlockHash}, height: {blockAnnouncement.BlockHeight} }} ignore.");
                return false;
            }

            return true;
        }

        public async Task<bool> ValidateBlockAsync(Chain chain, BlockWithTransactions blockWithTransactions, string senderPubKey)
        {
            if (blockWithTransactions.Height <= chain.LastIrreversibleBlockHeight)
            {
                Logger.LogWarning($"Receive lower block {blockWithTransactions} ignore.");
                return false;
            }

            return true;
        }
        
        private bool TryCacheNewAnnouncement(Hash blockHash, long blockHeight, string senderPubkey)
        {
            return _announcementCacheProvider.TryAddOrUpdateAnnouncementCache(blockHash, blockHeight, senderPubkey);
        }

        public async Task<bool> ValidateTransactionAsync(IEnumerable<Transaction> transactions)
        {
            foreach (var transaction in transactions)
            {
                foreach (var validationProvider in _transactionValidationProviders)
                {
                    // No need to validate again if this tx already in local database.
                    var tx = await _transactionManager.GetTransactionAsync(transaction.GetHash());
                    if (tx != null)
                        continue;

                    if (!await validationProvider.ValidateTransactionAsync(transaction))
                        return false;
                }
            }

            return true;
        }

        public async Task<bool> ValidateBlockBeforeAttachAsync(BlockWithTransactions blockWithTransactions)
        {
            return await _blockValidationService.ValidateBlockBeforeAttachAsync(blockWithTransactions);
        }
    }
}