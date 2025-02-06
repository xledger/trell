using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Trell.Engine.Collections;

namespace Trell.Test.Collections;

public class BoundedObjectPoolTest {
    sealed class TestBox(int x) { public int X = x; };

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
        Assert.Throws<OperationCanceledException>(() => blocker.Take(new CancellationTokenSource(2000).Token));
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
        Assert.Throws<OperationCanceledException>(() => blocker.Take(new CancellationTokenSource(2000).Token));
        Assert.Equal(PENDING_2, initializationCtr);
    }
}
