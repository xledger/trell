using System;
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
    public async Task TestPoolPreInitializesObjectsInPendingCollection() {
        const int MAX = 3;
        const int PENDING = 4;
        int currIdCtr = 600;
        int initializationCtr = 0;
        using var pool = new BoundedObjectPool<int, int>(
            () => {
                Interlocked.Increment(ref initializationCtr);
                return ++currIdCtr;
            },
            MAX,
            PENDING
        );

        // All pending behavior is asynchronous so we have to ask threads to sleep
        // for a bit, otherwise we might fail a test because of a race.
        const int TIMEOUT = 10000;  // 10 seconds
        const int SHORT_WAIT = 100; // 0.1 seconds

        // Makes sure pending pre-initializes objects before we ever call TryGet, but
        // does not pre-initialize more objects than the max.
        var taskRace = await Task.WhenAny(
            Task.Run(() => {
                while (initializationCtr < MAX) {
                    Thread.Sleep(SHORT_WAIT);
                }
                return true;
            }),
            Task.Run(() => {
                Thread.Sleep(TIMEOUT);
                return false;
            })
        );
        if (!taskRace.Result) {
            throw new Exception("Test timed out");
        }
        Assert.True(pool.Pending <= MAX);

        // Testing that pre-initialization does not happen again until we return something to the pool
        for (int i = 0; i < MAX; i++) {
            pool.TryGet(i, out _);
        }
        taskRace = await Task.WhenAny(
            Task.Run(() => {
                Thread.Sleep(TIMEOUT);
                return true;
            }),
            Task.Run(() => {
                while (initializationCtr == MAX) {
                    Thread.Sleep(SHORT_WAIT);
                }
                return false;
            })
        );
        if (!taskRace.Result) {
            throw new Exception("Pre-initialization happened when it should not have");
        }
        Assert.Equal(MAX, initializationCtr);

        pool.TryRemove(0, out _);
        taskRace = await Task.WhenAny(
            Task.Run(() => {
                while (initializationCtr == MAX) {
                    Thread.Sleep(SHORT_WAIT);
                }
                return true;
            }),
            Task.Run(() => {
                Thread.Sleep(TIMEOUT);
                return false;
            })
        );
        if (!taskRace.Result) {
            throw new Exception("Test timed out");
        }
        Assert.Equal(MAX + 1, initializationCtr);

        initializationCtr = 0;
        const int MAX_2 = 4;
        const int PENDING_2 = 2;
        using var pool2 = new BoundedObjectPool<int, int>(
            () => {
                Interlocked.Increment(ref initializationCtr);
                return ++currIdCtr;
            },
            MAX_2,
            PENDING_2
        );

        // If our pending count is less than our max, the number of pre-initialized
        // objects should not exceed the original pending count
        taskRace = await Task.WhenAny(
            Task.Run(() => {
                while (initializationCtr < PENDING_2) {
                    Thread.Sleep(SHORT_WAIT);
                }
                return true;
            }),
            Task.Run(() => {
                Thread.Sleep(TIMEOUT);
                return false;
            })
        );
        if (!taskRace.Result) {
            throw new Exception("Test timed out");
        }
        Assert.Equal(PENDING_2, initializationCtr);
    }
}
