using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AElf.Kernel.Blockchain.Application;
using AElf.Kernel.SmartContract.Infrastructure;
using AElf.Kernel.SmartContract.Sdk;
using AElf.Kernel.SmartContractExecution.Events;
using AElf.Types;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.EventBus.Local;

namespace AElf.Kernel.SmartContract.Application
{
    public class TransactionExecutingService : ITransactionExecutingService
    {
        private readonly ISmartContractExecutiveService _smartContractExecutiveService;
        private readonly List<IPreExecutionPlugin> _prePlugins;
        private readonly List<IPostExecutionPlugin> _postPlugins;
        private readonly ITransactionResultService _transactionResultService;
        public ILogger<TransactionExecutingService> Logger { get; set; }
        
        public ILocalEventBus LocalEventBus { get; set; }

        public TransactionExecutingService(ITransactionResultService transactionResultService,
            ISmartContractExecutiveService smartContractExecutiveService, IEnumerable<IPostExecutionPlugin> postPlugins, IEnumerable<IPreExecutionPlugin> prePlugins
            )
        {
            _transactionResultService = transactionResultService;
            _smartContractExecutiveService = smartContractExecutiveService;
            _prePlugins = GetUniquePrePlugins(prePlugins);
            _postPlugins = GetUniquePostPlugins(postPlugins);
            Logger = NullLogger<TransactionExecutingService>.Instance;
            LocalEventBus = NullLocalEventBus.Instance;
        }

        public async Task<List<ExecutionReturnSet>> ExecuteAsync(TransactionExecutingDto transactionExecutingDto,
            CancellationToken cancellationToken, bool throwException)
        {
            var groupStateCache = transactionExecutingDto.PartialBlockStateSet == null
                ? new TieredStateCache()
                : new TieredStateCache(
                    new StateCacheFromPartialBlockStateSet(transactionExecutingDto.PartialBlockStateSet));
            var groupChainContext = new ChainContextWithTieredStateCache(
                transactionExecutingDto.BlockHeader.PreviousBlockHash,
                transactionExecutingDto.BlockHeader.Height - 1, groupStateCache);

            var returnSets = new List<ExecutionReturnSet>();
            foreach (var transaction in transactionExecutingDto.Transactions)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var trace = await ExecuteOneAsync(0, groupChainContext, transaction,
                    transactionExecutingDto.BlockHeader.Time,
                    cancellationToken);

                // Will be useful when debugging MerkleTreeRootOfWorldState is different from each miner.
                Logger.LogTrace(transaction.MethodName);
                Logger.LogTrace(trace.StateSet.Writes.Values.Select(v => v.ToBase64().ComputeHash().ToHex())
                    .JoinAsString("\n"));

                if (!trace.IsSuccessful())
                {
                    if (throwException)
                    {
                        Logger.LogError(trace.Error);
                    }

                    // Do not package this transaction if any of his inline transactions canceled.
                    if (IsTransactionCanceled(trace))
                    {
                        break;
                    }

                    trace.SurfaceUpError();
                }
                else
                {
                    groupStateCache.Update(trace.GetStateSets());
                }

                if (trace.Error != string.Empty)
                {
                    Logger.LogError(trace.Error);
                }

                var result = GetTransactionResult(trace, transactionExecutingDto.BlockHeader.Height);

                if (result != null)
                {
                    result.TransactionFee = trace.TransactionFee;
                    await _transactionResultService.AddTransactionResultAsync(result,
                        transactionExecutingDto.BlockHeader);
                }

                var returnSet = GetReturnSet(trace, result);
                returnSets.Add(returnSet);
            }

            return returnSets;
        }

        private static bool IsTransactionCanceled(TransactionTrace trace)
        {
            return trace.ExecutionStatus == ExecutionStatus.Canceled ||
                   trace.InlineTraces.ToList().Any(IsTransactionCanceled);
        }

        private async Task<TransactionTrace> ExecuteOneAsync(int depth, IChainContext chainContext,
            Transaction transaction, Timestamp currentBlockTime, CancellationToken cancellationToken,
            Address origin = null, bool isCancellable = true)
        {
            if (isCancellable && cancellationToken.IsCancellationRequested)
            {
                return new TransactionTrace
                {
                    TransactionId = transaction.GetHash(),
                    ExecutionStatus = ExecutionStatus.Canceled,
                    Error = "Execution cancelled"
                };
            }

            if (transaction.To == null || transaction.From == null)
            {
                throw new Exception($"error tx: {transaction}");
            }

            var trace = new TransactionTrace
            {
                TransactionId = transaction.GetHash()
            };

            var txContext = new TransactionContext
            {
                PreviousBlockHash = chainContext.BlockHash,
                CurrentBlockTime = currentBlockTime,
                Transaction = transaction,
                BlockHeight = chainContext.BlockHeight + 1,
                Trace = trace,
                CallDepth = depth,
                StateCache = chainContext.StateCache,
                Origin = origin != null ? origin : transaction.From
            };

            var internalStateCache = new TieredStateCache(chainContext.StateCache);
            var internalChainContext = new ChainContextWithTieredStateCache(chainContext, internalStateCache);

            IExecutive executive;
            try
            {
                executive = await _smartContractExecutiveService.GetExecutiveAsync(
                    internalChainContext,
                    transaction.To);
            }
            catch (SmartContractFindRegistrationException e)
            {
                txContext.Trace.ExecutionStatus = ExecutionStatus.ContractError;
                txContext.Trace.Error += "Invalid contract address.\n";
                return trace;
            }
            
            try
            {
                #region PreTransaction

                if (depth == 0)
                {
                    if (!await ExecutePluginOnPreTransactionStageAsync(executive, txContext, currentBlockTime,
                        internalChainContext, internalStateCache, cancellationToken))
                    {
                        return trace;
                    }
                }

                #endregion

                await executive.ApplyAsync(txContext);

                await ExecuteInlineTransactions(depth, currentBlockTime, txContext, internalStateCache,
                    internalChainContext, cancellationToken);

                #region PostTransaction

                if (depth == 0)
                {
                    if (!await ExecutePluginOnPostTransactionStageAsync(executive, txContext, currentBlockTime,
                        internalChainContext, internalStateCache, cancellationToken))
                    {
                        return trace;
                    }
                }

                #endregion
            }
            catch (Exception ex)
            {
                Logger.LogError($"Tx execution failed: {txContext}");
                txContext.Trace.ExecutionStatus = ExecutionStatus.ContractError;
                txContext.Trace.Error += ex + "\n";
                throw;
            }
            finally
            {
                await _smartContractExecutiveService.PutExecutiveAsync(transaction.To, executive);
                await LocalEventBus.PublishAsync(new TransactionExecutedEventData
                {
                    TransactionTrace = trace
                });
            }

            return trace;
        }

        private async Task ExecuteInlineTransactions(int depth, Timestamp currentBlockTime,
            ITransactionContext txContext, TieredStateCache internalStateCache,
            IChainContext internalChainContext, CancellationToken cancellationToken)
        {
            var trace = txContext.Trace;
            if (txContext.Trace.IsSuccessful())
            {
                internalStateCache.Update(txContext.Trace.GetStateSets());
                foreach (var inlineTx in txContext.Trace.InlineTransactions)
                {
                    var inlineTrace = await ExecuteOneAsync(depth + 1, internalChainContext, inlineTx,
                        currentBlockTime, cancellationToken, txContext.Origin);
                    trace.InlineTraces.Add(inlineTrace);
                    if (!inlineTrace.IsSuccessful())
                    {
                        Logger.LogError($"Method name: {inlineTx.MethodName}, {inlineTrace.Error}");
                        // Already failed, no need to execute remaining inline transactions
                        break;
                    }

                    internalStateCache.Update(inlineTrace.GetStateSets());
                }
            }
        }

        private async Task<bool> ExecutePluginOnPreTransactionStageAsync(IExecutive executive,
            ITransactionContext txContext,
            Timestamp currentBlockTime,
            IChainContext internalChainContext,
            TieredStateCache internalStateCache,
            CancellationToken cancellationToken)
        {
            var trace = txContext.Trace;
            foreach (var plugin in _prePlugins)
            {
                var transactions = await plugin.GetPreTransactionsAsync(executive.Descriptors, txContext);
                foreach (var preTx in transactions)
                {
                    var preTrace = await ExecuteOneAsync(0, internalChainContext, preTx, currentBlockTime,
                        cancellationToken);
                    trace.PreTransactions.Add(preTx);
                    trace.PreTraces.Add(preTrace);
                    if (preTx.MethodName == "ChargeTransactionFees")
                    {
                        var txFee = new TransactionFee();
                        txFee.MergeFrom(preTrace.ReturnValue);
                        trace.TransactionFee = txFee;
                    }
                    if (!preTrace.IsSuccessful())
                    {
                        trace.ExecutionStatus = ExecutionStatus.Prefailed;
                        preTrace.SurfaceUpError();
                        trace.Error += preTrace.Error;
                        return false;
                    }

                    var stateSets = preTrace.GetStateSets().ToList();
                    internalStateCache.Update(stateSets);
                    var parentStateCache = txContext.StateCache as TieredStateCache;
                    parentStateCache?.Update(stateSets);
                }
            }

            return true;
        }

        private async Task<bool> ExecutePluginOnPostTransactionStageAsync(IExecutive executive,
            ITransactionContext txContext,
            Timestamp currentBlockTime,
            IChainContext internalChainContext,
            TieredStateCache internalStateCache,
            CancellationToken cancellationToken)
        {
            var trace = txContext.Trace;
            foreach (var plugin in _postPlugins)
            {
                var transactions = await plugin.GetPostTransactionsAsync(executive.Descriptors, txContext);
                foreach (var postTx in transactions)
                {
                    var postTrace = await ExecuteOneAsync(0, internalChainContext, postTx, currentBlockTime,
                        cancellationToken, isCancellable: false);
                    trace.PostTransactions.Add(postTx);
                    trace.PostTraces.Add(postTrace);
                    if (!postTrace.IsSuccessful())
                    {
                        trace.ExecutionStatus = ExecutionStatus.Postfailed;
                        postTrace.SurfaceUpError();
                        trace.Error += postTrace.Error;
                        return false;
                    }

                    internalStateCache.Update(postTrace.GetStateSets());
                }
            }

            return true;
        }

        private TransactionResult GetTransactionResult(TransactionTrace trace, long blockHeight)
        {
            if (trace.ExecutionStatus == ExecutionStatus.Undefined)
            {
                return new TransactionResult
                {
                    TransactionId = trace.TransactionId,
                    Status = TransactionResultStatus.Unexecutable,
                    Error = ExecutionStatus.Undefined.ToString()
                };
            }

            if (trace.ExecutionStatus == ExecutionStatus.Prefailed)
            {
                return new TransactionResult
                {
                    TransactionId = trace.TransactionId,
                    Status = TransactionResultStatus.Unexecutable,
                    Error = trace.Error
                };
            }

            if (trace.IsSuccessful())
            {
                var txRes = new TransactionResult
                {
                    TransactionId = trace.TransactionId,
                    Status = TransactionResultStatus.Mined,
                    ReturnValue = trace.ReturnValue,
                    ReadableReturnValue = trace.ReadableReturnValue,
                    BlockNumber = blockHeight,
                    //StateHash = trace.GetSummarizedStateHash(),
                    Logs = {trace.FlattenedLogs}
                };

                txRes.UpdateBloom();

                return txRes;
            }

            return new TransactionResult
            {
                TransactionId = trace.TransactionId,
                Status = TransactionResultStatus.Failed,
                Error = trace.Error
            };
        }

        private ExecutionReturnSet GetReturnSet(TransactionTrace trace, TransactionResult result)
        {
            var returnSet = new ExecutionReturnSet
            {
                TransactionId = result.TransactionId,
                Status = result.Status,
                Bloom = result.Bloom
            };

            if (trace.IsSuccessful())
            {
                var transactionExecutingStateSets = trace.GetStateSets();
                foreach (var transactionExecutingStateSet in transactionExecutingStateSets)
                {
                    foreach (var write in transactionExecutingStateSet.Writes)
                    {
                        returnSet.StateChanges[write.Key] = write.Value;
                        returnSet.StateDeletes.Remove(write.Key);
                    }
                    foreach (var delete in transactionExecutingStateSet.Deletes)
                    {
                        returnSet.StateDeletes[delete.Key] = delete.Value;
                        returnSet.StateChanges.Remove(delete.Key);
                    }
                }

                returnSet.ReturnValue = trace.ReturnValue;
            }

            var reads = trace.GetFlattenedReads();
            foreach (var read in reads)
            {
                returnSet.StateAccesses[read.Key] = read.Value;
            }

            return returnSet;
        }

        private static List<IPreExecutionPlugin> GetUniquePrePlugins(IEnumerable<IPreExecutionPlugin> plugins)
        {
            // One instance per type
            return plugins.ToLookup(p => p.GetType()).Select(coll => coll.First()).ToList();
        }

        private static List<IPostExecutionPlugin> GetUniquePostPlugins(IEnumerable<IPostExecutionPlugin> plugins)
        {
            // One instance per type
            return plugins.ToLookup(p => p.GetType()).Select(coll => coll.First()).ToList();
        }
    }
}