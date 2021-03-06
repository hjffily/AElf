﻿using System;
using System.Reactive.Linq;
using AElf.Kernel.Consensus.Application;
using AElf.Sdk.CSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EventBus.Local;

namespace AElf.Kernel.Consensus.Scheduler.RxNet
{
    public class RxNetScheduler : IConsensusScheduler, IObserver<ConsensusRequestMiningEventData>, ISingletonDependency
    {
        private IDisposable _observables;

        public ILocalEventBus LocalEventBus { get; set; }

        public ILogger<RxNetScheduler> Logger { get; set; }

        public RxNetScheduler()
        {
            LocalEventBus = NullLocalEventBus.Instance;

            Logger = NullLogger<RxNetScheduler>.Instance;
        }

        public void NewEvent(long countingMilliseconds, ConsensusRequestMiningEventData consensusRequestMiningEventData)
        {
            _observables = Subscribe(countingMilliseconds, consensusRequestMiningEventData);
        }

        public void CancelCurrentEvent()
        {
            Logger.LogInformation("Disposed previous consensus event.");
            _observables?.Dispose();
        }

        public IDisposable Subscribe(long countingMilliseconds, ConsensusRequestMiningEventData consensusRequestMiningEventData)
        {
            Logger.LogInformation($"Will produce block after {countingMilliseconds} ms - " +
                            $"{TimestampHelper.GetUtcNow().AddMilliseconds(countingMilliseconds).ToDateTime():yyyy-MM-dd HH.mm.ss,fff}");

            return Observable.Timer(TimeSpan.FromMilliseconds(countingMilliseconds))
                .Select(_ => consensusRequestMiningEventData).Subscribe(this);
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        // This is the callback.
        public void OnNext(ConsensusRequestMiningEventData value)
        {
            Logger.LogInformation($"Published block mining event. Current block height: {value.PreviousBlockHeight}");
            LocalEventBus.PublishAsync(value);
        }
    }
}