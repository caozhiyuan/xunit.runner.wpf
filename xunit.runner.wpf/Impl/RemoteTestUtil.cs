﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using xunit.runner.data;
using xunit.runner.wpf.ViewModel;

namespace xunit.runner.wpf.Impl
{
    internal sealed partial class RemoteTestUtil : ITestUtil
    {
        private readonly Dispatcher _dispatcher;

        internal RemoteTestUtil(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        private static Connection StartWorkerProcess(string action, string argument)
        {
            var pipeName = $"xunit.runner.wpf.pipe.{Guid.NewGuid()}";
            var processStartInfo = new ProcessStartInfo();
            processStartInfo.FileName = typeof(xunit.runner.worker.Program).Assembly.Location;
            processStartInfo.Arguments = $"{pipeName} {action} {argument}";
            processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;

            var process = Process.Start(processStartInfo);
            try
            {
                var stream = new NamedPipeClientStream(pipeName);
                stream.Connect();
                return new Connection(stream, process);
            }
            catch
            {
                process.Kill();
                throw;
            }
        }

        private Task Discover(string assemblyPath, Action<TestCaseData> callback, CancellationToken cancellationToken)
        {
            var connection = StartWorkerProcess(Constants.ActionDiscover, assemblyPath);
            var queue = new ConcurrentQueue<TestCaseData>();
            var backgroundReader = new BackgroundReader<TestCaseData>(queue, new ClientReader(connection.Stream), r => r.ReadTestCaseData(), cancellationToken);
            backgroundReader.ReadAsync();

            var backgroundProducer = new BackgroundProducer<TestCaseData>(connection, _dispatcher, queue, callback);
            return backgroundProducer.Task;
        }

        private Task RunCore(string actionName, string assemblyPath, ImmutableArray<string> testCaseDisplayNames, Action<TestResultData> callback, CancellationToken cancellationToken)
        {
            var connection = StartWorkerProcess(actionName, assemblyPath);
            var queue = CreateRunQueue(connection, testCaseDisplayNames, cancellationToken);
            var backgroundProducer = new BackgroundProducer<TestResultData>(connection, _dispatcher, queue, callback);
            return backgroundProducer.Task;
        }

        /// <summary>
        /// Create the <see cref="ConcurrentQueue{T}"/> which will be populated with the <see cref="TestResultData"/>
        /// as it arrives from the worker. 
        /// </summary>
        private static ConcurrentQueue<TestResultData> CreateRunQueue(Connection connection, ImmutableArray<string> testCaseDisplayNames, CancellationToken cancellationToken)
        {
            var queue = new ConcurrentQueue<TestResultData>();
            var unused = CreateRunQueueCore(queue, connection, testCaseDisplayNames, cancellationToken);
            return queue;
        }

        private static async Task CreateRunQueueCore(ConcurrentQueue<TestResultData> queue, Connection connection, ImmutableArray<string> testCaseDisplayNames, CancellationToken cancellationToken)
        {
            try
            {
                if (!testCaseDisplayNames.IsDefaultOrEmpty)
                {
                    var backgroundWriter = new BackgroundWriter<string>(new ClientWriter(connection.Stream), testCaseDisplayNames, (w, s) => w.Write(s), cancellationToken);
                    await backgroundWriter.WriteAsync();
                }

                var backgroundReader = new BackgroundReader<TestResultData>(queue, new ClientReader(connection.Stream), r => r.ReadTestResultData(), cancellationToken);
                await backgroundReader.ReadAsync();
            }
            catch (Exception ex)
            {
                Debug.Fail(ex.Message);

                // Signal data completed
                queue.Enqueue(null);
            }
        }

        #region ITestUtil

        Task ITestUtil.Discover(string assemblyPath, Action<TestCaseData> callback, CancellationToken cancellationToken)
        {
            return Discover(assemblyPath, callback, cancellationToken);
        }

        Task ITestUtil.RunAll(string assemblyPath, Action<TestResultData> callback, CancellationToken cancellationToken)
        {
            return RunCore(Constants.ActionRunAll, assemblyPath, ImmutableArray<string>.Empty, callback, cancellationToken);
        }

        Task ITestUtil.RunSpecific(string assemblyPath, ImmutableArray<string> testCaseDisplayNames, Action<TestResultData> callback, CancellationToken cancellationToken)
        {
            return RunCore(Constants.ActionRunSpecific, assemblyPath, testCaseDisplayNames, callback, cancellationToken);
        }

        #endregion
    }
}
