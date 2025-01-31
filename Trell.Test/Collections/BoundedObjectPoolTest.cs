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

        for (int i = 0; i < MAX; i++) {
            pool.TryGet(i, out _);
        }
        Assert.Equal(MAX, pool.Count);

        for (int i = 0; i < MAX; i++) {
            Assert.True(pool.TryRemove(i, out int x));
            Assert.Equal((currIdCtr + i + 1) - MAX, x);
        }
        Assert.Equal(0, pool.Count);

        // Checking to make sure we actually overwrite previous entries
        Assert.True(pool.TryGet(0, out int y));
        Assert.Equal(currIdCtr, y);
    }
}
