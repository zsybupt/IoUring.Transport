using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IoUring.Transport.Internals.Inbound;
using Microsoft.Extensions.Options;

namespace IoUring.Transport.Internals
{
    internal sealed class IoUringTransport : IAsyncDisposable
    {
        private const int Disposed = -1;

        private object _lock = new object();
        private AcceptThread _acceptThread;
        private int _refCount;
        private readonly IoUringOptions _options;

        public IoUringTransport(IOptions<IoUringOptions> options)
        {
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

            Limits.SetToMax(Resource.RLIMIT_NOFILE);

            List<int> cpus = null;
            if (_options.SetThreadAffinity)
            {
                cpus = CpuInfo.GetPreferredCpuIds(_options.ThreadCount);
            }

            var threads = new TransportThread[_options.ThreadCount];
            int cpuIdx = 0;
            for (int i = 0; i < threads.Length; i++)
            {
                var cpuId = cpus == null ? TransportThread.NoCpuAffinity : cpus[cpuIdx++ % cpus.Count];
                var thread = new TransportThread(_options, cpuId);
                thread.Run();
                threads[i] = thread;
            }

            TransportThreads = threads;
        }

        public TransportThread[] TransportThreads { get; private set; }

        public AcceptThread AcceptThread => LazyInitializer.EnsureInitialized(ref _acceptThread, ref _lock, () => CreateAcceptThread());

        private AcceptThread CreateAcceptThread()
        {
            if (_refCount == Disposed) ThrowHelper.ThrowNewObjectDisposedException(ThrowHelper.ExceptionArgument.IoUringTransport);

            var thread = new AcceptThread(_options, TransportThreads);
            thread.Run();
            return thread;
        }

        public void IncrementThreadRefCount()
        {
            lock (_lock)
            {
                if (_refCount == Disposed) ThrowHelper.ThrowNewObjectDisposedException(ThrowHelper.ExceptionArgument.IoUringTransport);
                _refCount++;
            }
        }

        public void DecrementThreadRefCount()
        {
            lock (_lock)
            {
                if (_refCount == Disposed) ThrowHelper.ThrowNewObjectDisposedException(ThrowHelper.ExceptionArgument.IoUringTransport);
                _refCount--;
            }
        }

        public async ValueTask DisposeAsync()
        {
            TransportThread[] transportThreads;
            AcceptThread acceptThread;

            lock (_lock)
            {
                if (_refCount == Disposed) return;
                if ( _refCount != 0) ThrowHelper.ThrowNewInvalidOperationException();
                _refCount = Disposed;
                transportThreads = TransportThreads;
                TransportThreads = null;
                acceptThread = _acceptThread;
                _acceptThread = null;
            }

            if (transportThreads != null)
            {
                foreach (var transportThread in transportThreads)
                {
                    await transportThread.DisposeAsync();
                }
            }

            if (acceptThread != null)
                await acceptThread.DisposeAsync();
        }
    }
}