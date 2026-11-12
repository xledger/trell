using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Trell.Engine.Collections;

namespace Trell.Test.Collections;

public class BoundedObjectPoolTest {
    sealed class TestBox(int x) {
        public int X = x;
    };

    [Fact]
    public void TestPoolLetsDataBeRetrieved() {
        const int MAX = 4;
        int currIdCtr = 66;
        using var pool = new BoundedObjectPool<int, int>(() => currIdCtr++, MAX);

        var retrieved = new int[MAX];
        for (int i = 0; i < MAX; i++) {
            Assert.Equal(i, pool.Count);
            Assert.True(pool.TryGet(i, out retrieved[i]));
        }

        // Checking to make sure entries aren't overwritten or recreated when accessed again
        for (int i = 0; i < MAX; i++) {
            Assert.True(pool.TryGet(i, out int x));
            Assert.True(x == retrieved[i]);
        }
    }

    [Fact]
    public void TestPoolLimitsNumberOfEntries() {
        const int MAX = 6;
        using var pool = new BoundedObjectPool<int, int>(() => 5, MAX);

        for (int i = 0; i < MAX; i++) {
            Assert.True(pool.TryGet(i, out _));
        }
        Assert.Equal(MAX, pool.Count);
        Assert.False(pool.TryGet(MAX + 1, out _));
    }

    [Fact]
    public void TestPoolLetsDataBeRemoved() {
        const int MAX = 3;
        int currIdCtr = 27;
        using var pool = new BoundedObjectPool<int, int>(() => ++currIdCtr, MAX);

        var storedValues = new Dictionary<int, int>();
        for (int i = 0; i < MAX; i++) {
            pool.TryGet(i, out int x);
            storedValues[i] = x;
        }
        Assert.Equal(MAX, pool.Count);

        for (int i = 0; i < MAX; i++) {
            Assert.True(pool.TryRemove(i, out int x));
            Assert.Equal(storedValues[i], x);
        }
        Assert.Equal(0, pool.Count);

        // Checking to make sure we actually overwrite previous entries
        Assert.True(pool.TryGet(0, out int y));
        Assert.Equal(currIdCtr, y);
    }

    [Fact]
    public void TestPoolPreInitializesObjectsInPendingCollection() {
        // All pending behavior is asynchronous so we have to use this BlockingCollection to block
        // the thread until we get expected results.
        // It also acts as a timeout so the test nevers hangs indefinitely -- if the collection waits too
        // long on an expected result, its given CancellationToken will stop the test with an exception.
        var blocker = new BlockingCollection<int>();

        const int MAX = 3;
        const int PENDING = 4;
        int currIdCtr = 600;
        int initializationCtr = 0;
        using var pool = new BoundedObjectPool<int, int>(
            () => {
                Interlocked.Increment(ref initializationCtr);
                var id = ++currIdCtr;
                blocker.Add(id);
                return id;
            },
            MAX,
            PENDING
        );

        // Makes sure pending pre-initializes objects before we ever call TryGet, but
        // does not pre-initialize more objects than the max.
        for (int i = 0; i < MAX; i++) {
            blocker.Take(new CancellationTokenSource(5000).Token);
        }
        Assert.Equal(MAX, initializationCtr);
        Assert.True(pool.Pending <= MAX);

        // Testing that pre-initialization does not happen again until we return something to the pool
        for (int i = 0; i < MAX; i++) {
            pool.TryGet(i, out _);
        }
        Assert.Throws<OperationCanceledException>(() =>
            blocker.Take(new CancellationTokenSource(2000).Token)
        );
        Assert.Equal(MAX, initializationCtr);

        pool.TryRemove(0, out _);
        blocker.Take(new CancellationTokenSource(5000).Token);
        Assert.Equal(MAX + 1, initializationCtr);

        blocker = [];
        initializationCtr = 0;
        const int MAX_2 = 4;
        const int PENDING_2 = 2;
        using var pool2 = new BoundedObjectPool<int, int>(
            () => {
                Interlocked.Increment(ref initializationCtr);
                var id = ++currIdCtr;
                blocker.Add(id);
                return id;
            },
            MAX_2,
            PENDING_2
        );

        // If our pending count is less than our max, the number of pre-initialized
        // objects should not exceed the original pending count
        for (int i = 0; i < PENDING_2; i++) {
            blocker.Take(new CancellationTokenSource(5000).Token);
        }
        Assert.Throws<OperationCanceledException>(() =>
            blocker.Take(new CancellationTokenSource(2000).Token)
        );
        Assert.Equal(PENDING_2, initializationCtr);
    }

    sealed class ManualTime : TimeProvider {
        long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => ticks;

        public void Advance(TimeSpan by) => ticks += by.Ticks;
    }

    [Fact]
    public void TestPoolGivesIdleObjectToNewKey() {
        var created = 0;
        var time = new ManualTime();
        using var pool = new BoundedObjectPool<int, int>(
            () => Interlocked.Increment(ref created),
            max: 1,
            idleTimeout: TimeSpan.FromSeconds(10),
            time: time
        );

        Assert.True(pool.TryAcquire(0, out var first));
        Assert.Equal(1, first.Value);

        // Key 0 holds the only slot while its lease is open.
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(pool.TryAcquire(1, out _));

        // Key 0 must stay idle for the whole timeout before key 1 can take its object.
        first.Dispose();
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(pool.TryAcquire(1, out _));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(pool.TryAcquire(1, out var second));
        Assert.Equal(1, second.Value);
        Assert.Equal(1, pool.Count);
        Assert.Equal(1, created);

        // Key 0 lost its slot, and key 1 is in use, so key 0 is refused.
        Assert.False(pool.TryAcquire(0, out _));
        second.Dispose();
    }

    [Fact]
    public void TestPoolKeepsKeyWhileAnyLeaseIsOpen() {
        var time = new ManualTime();
        using var pool = new BoundedObjectPool<int, int>(
            () => 7,
            max: 1,
            idleTimeout: TimeSpan.Zero,
            time: time
        );

        Assert.True(pool.TryAcquire(0, out var a));
        Assert.True(pool.TryAcquire(0, out var b));
        a.Dispose();
        // Disposing a lease twice must not end another caller's lease.
        a.Dispose();
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(pool.TryAcquire(1, out _));

        b.Dispose();
        Assert.True(pool.TryAcquire(1, out var c));
        Assert.Equal(7, c.Value);
        c.Dispose();
    }

    [Fact]
    public void TestPoolUnbindsTheKeyIdleLongest() {
        var created = 0;
        var time = new ManualTime();
        using var pool = new BoundedObjectPool<int, int>(
            () => Interlocked.Increment(ref created),
            max: 2,
            idleTimeout: TimeSpan.FromSeconds(10),
            time: time
        );

        Assert.True(pool.TryAcquire(0, out var zero));
        Assert.True(pool.TryAcquire(1, out var one));
        one.Dispose();
        time.Advance(TimeSpan.FromSeconds(5));
        zero.Dispose();
        time.Advance(TimeSpan.FromSeconds(20));

        Assert.True(pool.TryAcquire(2, out var two));
        Assert.Equal(one.Value, two.Value);
        Assert.Equal(2, created);

        // Key 0 still has its own object.
        Assert.True(pool.TryGet(0, out var zeroAgain));
        Assert.Equal(zero.Value, zeroAgain);
        Assert.Equal(2, created);
        two.Dispose();
    }

    [Fact]
    public void TestDiscardReplacesTheObject() {
        var created = 0;
        using var pool =
            new BoundedObjectPool<int, int>(() => Interlocked.Increment(ref created), max: 1);

        Assert.True(pool.TryAcquire(0, out var first));
        Assert.True(first.Discard());
        Assert.Equal(0, pool.Count);

        // The freed slot goes to a new object, for the same key or another.
        Assert.True(pool.TryAcquire(1, out var second));
        Assert.Equal(2, second.Value);

        // The first lease's binding is gone, so discarding it again must not unbind key 1.
        Assert.False(first.Discard());
        Assert.Equal(1, pool.Count);
        second.Dispose();
    }

    [Fact]
    public void TestDiscardLeavesKeyThatTookTheObject() {
        var time = new ManualTime();
        using var pool = new BoundedObjectPool<int, int>(
            () => 1,
            max: 1,
            idleTimeout: TimeSpan.Zero,
            time: time
        );

        Assert.True(pool.TryAcquire(0, out var stale));
        stale.Dispose();
        Assert.True(pool.TryAcquire(1, out var current));

        // Key 1 now holds the object. A discard through key 0's old lease must not unbind it.
        Assert.False(stale.Discard());
        Assert.Equal(1, pool.Count);
        Assert.False(pool.TryAcquire(2, out _));
        current.Dispose();
    }

    [Fact]
    public void TestPoolNeverUnbindsWithoutIdleTimeout() {
        var time = new ManualTime();
        using var pool = new BoundedObjectPool<int, int>(() => 1, max: 1, time: time);

        Assert.True(pool.TryAcquire(0, out var lease));
        lease.Dispose();
        time.Advance(TimeSpan.FromDays(1));
        Assert.False(pool.TryAcquire(1, out _));
    }
}
