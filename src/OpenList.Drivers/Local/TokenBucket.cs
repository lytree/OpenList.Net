namespace OpenList.Drivers.Local;

/// <summary>
/// Concurrency control for thumbnail generation. Mirrors Go's TokenBucket interface.
/// </summary>
public interface ITokenBucket
{
    Task DoAsync(CancellationToken ct, Func<Task> action);
}

/// <summary>
/// Fixed-size token bucket. Limits concurrent operations to <see cref="_size"/> tokens.
/// Mirrors Go's StaticTokenBucket using SemaphoreSlim.
/// </summary>
public class StaticTokenBucket : ITokenBucket
{
    private readonly SemaphoreSlim _semaphore;

    public StaticTokenBucket(int size)
    {
        _semaphore = new SemaphoreSlim(size, size);
    }

    public async Task DoAsync(CancellationToken ct, Func<Task> action)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            _semaphore.Release();
        }
    }
}

/// <summary>
/// No-op token bucket — all operations execute immediately with no concurrency limit.
/// Mirrors Go's NopTokenBucket.
/// </summary>
public class NopTokenBucket : ITokenBucket
{
    public Task DoAsync(CancellationToken ct, Func<Task> action) => action();
}
