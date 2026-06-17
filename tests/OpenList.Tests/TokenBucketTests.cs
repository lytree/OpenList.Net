using OpenList.Drivers.Local;

namespace OpenList.Tests;

public class TokenBucketTests
{
    [Test]
    public async Task StaticTokenBucket_ShouldLimitConcurrency()
    {
        var bucket = new StaticTokenBucket(2);
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var lockObj = new object();

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            bucket.DoAsync(CancellationToken.None, async () =>
            {
                lock (lockObj)
                {
                    concurrentCount++;
                    maxConcurrent = Math.Max(maxConcurrent, concurrentCount);
                }
                await Task.Delay(50);
                lock (lockObj) { concurrentCount--; }
            })).ToArray();

        await Task.WhenAll(tasks);
        await Assert.That(maxConcurrent).IsLessThanOrEqualTo(2);
    }

    [Test]
    public async Task StaticTokenBucket_Do_ShouldAcquireAndRelease()
    {
        var bucket = new StaticTokenBucket(1);
        var executed = false;

        await bucket.DoAsync(CancellationToken.None, async () =>
        {
            await Task.Delay(10);
            executed = true;
        });

        await Assert.That(executed).IsTrue();

        // Should be able to execute again (token was released)
        var executed2 = false;
        await bucket.DoAsync(CancellationToken.None, () =>
        {
            executed2 = true;
            return Task.CompletedTask;
        });
        await Assert.That(executed2).IsTrue();
    }

    [Test]
    public async Task NopTokenBucket_Do_ShouldExecuteImmediately()
    {
        var bucket = new NopTokenBucket();
        var executed = false;

        await bucket.DoAsync(CancellationToken.None, () =>
        {
            executed = true;
            return Task.CompletedTask;
        });

        await Assert.That(executed).IsTrue();
    }

    [Test]
    public async Task StaticTokenBucket_Do_Cancellation_ShouldThrow()
    {
        var bucket = new StaticTokenBucket(1);
        var cts = new CancellationTokenSource();

        // Occupy the single token
        var occupyTask = bucket.DoAsync(CancellationToken.None, async () =>
        {
            await Task.Delay(5000);
        });

        // Cancel immediately
        cts.Cancel();

        try
        {
            await bucket.DoAsync(cts.Token, () => Task.CompletedTask);
            Assert.Fail("Expected OperationCanceledException");
        }
        catch (OperationCanceledException) { /* expected, includes TaskCanceledException */ }
    }

    [Test]
    public async Task NopTokenBucket_Do_NoConcurrencyLimit()
    {
        var bucket = new NopTokenBucket();
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var lockObj = new object();

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            bucket.DoAsync(CancellationToken.None, async () =>
            {
                lock (lockObj)
                {
                    concurrentCount++;
                    maxConcurrent = Math.Max(maxConcurrent, concurrentCount);
                }
                await Task.Delay(50);
                lock (lockObj) { concurrentCount--; }
            })).ToArray();

        await Task.WhenAll(tasks);
        // NopTokenBucket has no limit, so all 10 should run concurrently
        await Assert.That(maxConcurrent).IsGreaterThan(2);
    }
}
