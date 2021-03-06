using System.Collections.Generic;
using System.Threading.Tasks;
using AElf.Contracts.MultiToken;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.SmartContract.Application;
using AElf.Kernel.TransactionPool.Infrastructure;
using Volo.Abp.DependencyInjection;

namespace AElf.Kernel.TransactionPool.Application
{
    #region concrete strategys

    //TODO: should not implement here
    public abstract class CalculateCostStrategyBase
    {
        protected ICalculateAlgorithmService CalculateAlgorithmService { get; set; }

        public async Task<long> GetCostAsync(IChainContext chainContext, int cost)
        {
            if (chainContext != null)
                CalculateAlgorithmService.CalculateAlgorithmContext.BlockIndex = new BlockIndex
                {
                    BlockHash = chainContext.BlockHash,
                    BlockHeight = chainContext.BlockHeight
                };
            return await CalculateAlgorithmService.CalculateAsync(cost);
        }

        public void AddAlgorithm(BlockIndex blockIndex, IList<ICalculateWay> allWay)
        {
            CalculateAlgorithmService.CalculateAlgorithmContext.BlockIndex = blockIndex;
            CalculateAlgorithmService.AddAlgorithmByBlock(blockIndex, allWay);
        }

        public void RemoveForkCache(List<BlockIndex> blockIndexes)
        {
            CalculateAlgorithmService.RemoveForkCache(blockIndexes);
        }

        public void SetIrreversedCache(List<BlockIndex> blockIndexes)
        {
            CalculateAlgorithmService.SetIrreversedCache(blockIndexes);
        }
    }

    internal class ReadCalculateCostStrategy : CalculateCostStrategyBase, ICalculateReadCostStrategy, ISingletonDependency
    {
        public ReadCalculateCostStrategy(ITokenContractReaderFactory tokenStTokenContractReaderFactory,
            IBlockchainService blockchainService,
            IChainBlockLinkService chainBlockLinkService,
            ICalculateFunctionCacheProvider functionCacheProvider)
        {
            CalculateAlgorithmService =
                new CalculateAlgorithmService(tokenStTokenContractReaderFactory, blockchainService,
                    chainBlockLinkService, functionCacheProvider);
                   
            CalculateAlgorithmService.CalculateAlgorithmContext.CalculateFeeTypeEnum = (int) FeeTypeEnum.Read;
        }
    }

    internal class StorageCalculateCostStrategy : CalculateCostStrategyBase,  ICalculateStorageCostStrategy, ISingletonDependency
    {
        public StorageCalculateCostStrategy(ITokenContractReaderFactory tokenStTokenContractReaderFactory,
            IBlockchainService blockchainService,
            IChainBlockLinkService chainBlockLinkService,
            ICalculateFunctionCacheProvider functionCacheProvider)
        {
            CalculateAlgorithmService =
                new CalculateAlgorithmService(tokenStTokenContractReaderFactory, blockchainService,
                    chainBlockLinkService, functionCacheProvider);
            
            CalculateAlgorithmService.CalculateAlgorithmContext.CalculateFeeTypeEnum = (int) FeeTypeEnum.Storage;
        }
    }

    internal class WriteCalculateCostStrategy : CalculateCostStrategyBase, ICalculateWriteCostStrategy, ISingletonDependency
    {
        public WriteCalculateCostStrategy(ITokenContractReaderFactory tokenStTokenContractReaderFactory,
            IBlockchainService blockchainService,
            IChainBlockLinkService chainBlockLinkService,
            ICalculateFunctionCacheProvider functionCacheProvider)
        {
            CalculateAlgorithmService =
                new CalculateAlgorithmService(tokenStTokenContractReaderFactory, blockchainService,
                    chainBlockLinkService, functionCacheProvider);
            CalculateAlgorithmService.CalculateAlgorithmContext.CalculateFeeTypeEnum = (int) FeeTypeEnum.Write;
        }
    }

    internal class TrafficCalculateCostStrategy : CalculateCostStrategyBase, ICalculateTrafficCostStrategy, ISingletonDependency
    {
        public TrafficCalculateCostStrategy(ITokenContractReaderFactory tokenStTokenContractReaderFactory,
            IBlockchainService blockchainService,
            IChainBlockLinkService chainBlockLinkService,
            ICalculateFunctionCacheProvider functionCacheProvider)
        {
            CalculateAlgorithmService =
                new CalculateAlgorithmService(tokenStTokenContractReaderFactory, blockchainService,
                    chainBlockLinkService, functionCacheProvider);
                    
            CalculateAlgorithmService.CalculateAlgorithmContext.CalculateFeeTypeEnum = (int) FeeTypeEnum.Traffic;
        }
    }

    internal class TxCalculateCostStrategy : CalculateCostStrategyBase, ICalculateTxCostStrategy, ISingletonDependency
    {
        public TxCalculateCostStrategy(ITokenContractReaderFactory tokenStTokenContractReaderFactory,
            IBlockchainService blockchainService,
            IChainBlockLinkService chainBlockLinkService,
            ICalculateFunctionCacheProvider functionCacheProvider)
        {
            CalculateAlgorithmService =
                new CalculateAlgorithmService(tokenStTokenContractReaderFactory, blockchainService,
                        chainBlockLinkService, functionCacheProvider);
            CalculateAlgorithmService.CalculateAlgorithmContext.CalculateFeeTypeEnum = (int) FeeTypeEnum.Tx;
        }
    }

    #endregion
}