using Microsoft.Extensions.Options;

namespace Audorix.ObjectStorage.Api;

internal sealed class ObjectStorageApiOperationAdmission
{
    private readonly SemaphoreSlim _permits;
    private readonly int _maxQueued;
    private int _queued;

    public ObjectStorageApiOperationAdmission(IOptions<ObjectStorageApiOptions> options)
    {
        _permits = new SemaphoreSlim(options.Value.MaxConcurrentOperations, options.Value.MaxConcurrentOperations);
        _maxQueued = options.Value.MaxQueuedOperations;
    }

    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        if (_permits.Wait(0)) return new Lease(_permits);
        if (_maxQueued == 0 || Interlocked.Increment(ref _queued) > _maxQueued)
        {
            if (_maxQueued != 0) Interlocked.Decrement(ref _queued);
            throw new ObjectStorageApiOverloadedException();
        }

        try
        {
            await _permits.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(_permits);
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
        }
    }

    private sealed class Lease(SemaphoreSlim permits) : IDisposable
    {
        private SemaphoreSlim? _permits = permits;
        public void Dispose() => Interlocked.Exchange(ref _permits, null)?.Release();
    }
}

internal sealed class ObjectStorageApiOverloadedException : Exception;
