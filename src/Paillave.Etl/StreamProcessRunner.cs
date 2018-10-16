﻿using Paillave.Etl.Core;
using Paillave.Etl.Core.Streams;
using Paillave.Etl.Extensions;
using Paillave.Etl.Reactive.Core;
using Paillave.Etl.Reactive.Disposables;
using Paillave.Etl.Reactive.Operators;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Paillave.Etl
{
    public class StreamProcessRunner
    {
        public static StreamProcessRunner<TConfig> Create<TConfig>(Action<ISingleStream<TConfig>> jobDefinition, string jobName = "NoName") => new StreamProcessRunner<TConfig>(jobDefinition, jobName);
    }

    public class StreamProcessRunner<TConfig>
    {
        private Action<ISingleStream<TConfig>> _jobDefinition;
        private string _jobName;
        public StreamProcessRunner(Action<ISingleStream<TConfig>> jobDefinition, string jobName = "NoName")
        {
            _jobDefinition = jobDefinition ?? (_jobDefinition => { });
            _jobName = jobName;
        }
        public Task<ExecutionStatus> ExecuteAsync(TConfig config, ITraceStreamProcessDefinition traceStreamProcessDefinition = null)
        {
            Guid executionId = Guid.NewGuid();
            EventWaitHandle startSynchronizer = new EventWaitHandle(false, EventResetMode.ManualReset);
            IPushSubject<TraceEvent> traceSubject = new PushSubject<TraceEvent>();
            IPushSubject<TConfig> startupSubject = new PushSubject<TConfig>();
            IExecutionContext traceExecutionContext = new TraceExecutionContext(startSynchronizer, executionId);
            var traceStream = new Stream<TraceEvent>(null, traceExecutionContext, null, traceSubject);
            JobExecutionContext jobExecutionContext = new JobExecutionContext(_jobName, executionId, startSynchronizer, traceSubject);
            var startupStream = new SingleStream<TConfig>(new Tracer(jobExecutionContext, new CurrentExecutionNodeContext(_jobName)), jobExecutionContext, _jobName, startupSubject.First());

            traceStreamProcessDefinition?.DefineProcess(traceStream);
            _jobDefinition(startupStream);

            Task<StreamStatistics> jobExecutionStatus = traceStream.GetStreamStatisticsAsync();

            startSynchronizer.Set();
            startupSubject.PushValue(config);
            startupSubject.Complete();

            return Task.WhenAll(
                jobExecutionContext
                    .GetCompletionTask()
                    .ContinueWith(_ => traceSubject.Complete()),
                traceExecutionContext.GetCompletionTask())
                .ContinueWith(t => new ExecutionStatus(jobExecutionContext.GetDefinitionStructure(), jobExecutionStatus.Result));
        }
        public JobDefinitionStructure GetDefinitionStructure()
        {
            GetDefinitionExecutionContext jobExecutionContext = new GetDefinitionExecutionContext(_jobName);
            //JobExecutionContext jobExecutionContext = new JobExecutionContext(jobDefinition.Name, Guid.Empty, null, null);
            var startupStream = new SingleStream<TConfig>(new Tracer(jobExecutionContext, new CurrentExecutionNodeContext(_jobName)), jobExecutionContext, _jobName, PushObservable.Empty<TConfig>());
            _jobDefinition(startupStream);
            return jobExecutionContext.GetDefinitionStructure();
        }
        private class CurrentExecutionNodeContext : INodeContext
        {
            public CurrentExecutionNodeContext(string jobName)
            {
                this.NodeName = jobName;
            }
            public string NodeName { get; }

            public string TypeName => "ExecutionContext";
        }

        private class GetDefinitionExecutionContext : IExecutionContext
        {
            private List<StreamToNodeLink> _streamToNodeLinks = new List<StreamToNodeLink>();
            private List<string> _nodeNamesToWait = new List<string>();
            public JobDefinitionStructure GetDefinitionStructure()
            {
                return new JobDefinitionStructure(_streamToNodeLinks, _nodeNamesToWait, this.JobName);
            }
            public GetDefinitionExecutionContext(string jobName)
            {
                this.JobName = jobName;
            }
            public void AddStreamToNodeLink(StreamToNodeLink link) => _streamToNodeLinks.Add(link);
            public Guid ExecutionId => throw new NotImplementedException();
            public WaitHandle StartSynchronizer => throw new NotImplementedException();
            public string JobName { get; }
            public IPushObservable<TraceEvent> TraceEvents => PushObservable.Empty<TraceEvent>();
            public bool IsTracingContext => false;
            public void AddDisposable(IDisposable disposable) => throw new NotImplementedException();
            public void AddToWaitForCompletion<T>(string sourceNodeName, IPushObservable<T> stream) => _nodeNamesToWait.Add(sourceNodeName);
            public Task GetCompletionTask() => throw new NotImplementedException();
            public void Trace(TraceEvent traceEvent) => throw new NotImplementedException();
        }
        private class JobExecutionContext : IExecutionContext
        {
            private readonly IPushSubject<TraceEvent> _traceSubject;
            private List<StreamToNodeLink> _streamToNodeLinks = new List<StreamToNodeLink>();
            private List<string> _nodeNamesToWait = new List<string>();
            public JobDefinitionStructure GetDefinitionStructure()
            {
                return new JobDefinitionStructure(_streamToNodeLinks, _nodeNamesToWait, this.JobName);
            }
            private readonly List<Task> _tasksToWait = new List<Task>();
            private readonly CollectionDisposableManager _disposables = new CollectionDisposableManager();

            public JobExecutionContext(string jobName, Guid executionId, WaitHandle startSynchronizer, IPushSubject<TraceEvent> traceSubject)
            {
                this.ExecutionId = executionId;
                this.JobName = jobName;
                this._traceSubject = traceSubject;
                this.StartSynchronizer = startSynchronizer;
            }
            public Guid ExecutionId { get; }
            public string JobName { get; }
            public IPushObservable<TraceEvent> TraceEvents => _traceSubject;
            public bool IsTracingContext => false;
            public WaitHandle StartSynchronizer { get; }
            public void Trace(TraceEvent traceEvent) => _traceSubject.PushValue(traceEvent);
            public void AddToWaitForCompletion<T>(string sourceNodeName, IPushObservable<T> stream)
            {
                _nodeNamesToWait.Add(sourceNodeName);
                _tasksToWait.Add(stream.ToTaskAsync());
            }
            public Task GetCompletionTask() => Task.WhenAll(_tasksToWait.ToArray()).ContinueWith(_ => _disposables.Dispose());
            public void AddDisposable(IDisposable disposable) => _disposables.Set(disposable);
            public void AddStreamToNodeLink(StreamToNodeLink link) => _streamToNodeLinks.Add(link);
        }
        private class TraceExecutionContext : IExecutionContext
        {
            private readonly IPushObservable<TraceEvent> _traceSubject;
            private readonly List<Task> _tasksToWait = new List<Task>();
            private readonly CollectionDisposableManager _disposables = new CollectionDisposableManager();
            public TraceExecutionContext(WaitHandle startSynchronizer, Guid executionId)
            {
                this.ExecutionId = executionId;
                this.JobName = null;
                this._traceSubject = PushObservable.Empty<TraceEvent>(this.StartSynchronizer);
                this.StartSynchronizer = startSynchronizer;
            }
            public Guid ExecutionId { get; }
            public string JobName { get; }
            public IPushObservable<TraceEvent> TraceEvents => _traceSubject;
            public bool IsTracingContext => true;
            public WaitHandle StartSynchronizer { get; }
            public void Trace(TraceEvent traveEvent) { }
            public void AddToWaitForCompletion<T>(string sourceNodeName, IPushObservable<T> stream) => _tasksToWait.Add(stream.ToTaskAsync());
            public Task GetCompletionTask() => Task.WhenAll(_tasksToWait.ToArray()).ContinueWith(_ => _disposables.Dispose());
            public void AddDisposable(IDisposable disposable) => _disposables.Set(disposable);
            public void AddStreamToNodeLink(StreamToNodeLink link) { }
        }
    }
}
