using System.Collections.Concurrent;

namespace Trell.Engine.Collections;
/// <summary>
/// A bounded, concurrent object pool that preinitializes some number of objects.
/// </summary>
/// <remarks>
/// When [pending] > 0, gets the Lazy.Value of each unallocated object in a
/// Task so that any long construction time happens before the object is needed.
/// </remarks>
sealed class BoundedObjectPool<K, V> : IDisposable
where K : notnull {
    readonly int max, pending;

    readonly BlockingCollection<int> requested = new();
    readonly BlockingCollection<Lazy<V>> unallocated;
    readonly ConcurrentDictionary<K, V> allocated = new();

    readonly CancellationTokenSource cts = new();
    readonly object serializedCreation = new();

    public BoundedObjectPool(Func<V> ctor, int max = 1, int pending = 0) {
        if (max < 1) {
            throw new ArgumentOutOfRangeException(nameof(max), max, "Must be > 0");
        }

        this.max = max;
        this.pending = pending;
        unallocated = new BlockingCollection<Lazy<V>>(Math.Max(pending, 1));
        _ = Task.Run(() => {
            while (true) {
                var n = requested.Take(cts.Token);
                for (var i = 0; i < n; ++i) {
                    var next = new Lazy<V>(ctor);
                    unallocated.Add(next);
                    if (pending > i) {
                        // After adding, get value to start creation.
                        _ = Task.Run(() => next.Value);
                    }
                }
            }
        });
        requested.Add(max);
    }

    public int Count => allocated.Count;
    public int Pending => unallocated.Count;

    public IEnumerable<V> Values => allocated.Values;

    public bool TryGet(K key, out V val) {
#pragma warning disable CS8601 // Possible null reference assignment.
        if (allocated.TryGetValue(key, out val)) {
            return true;
        }

        if (allocated.Count < max) {
            lock (serializedCreation) {
                if (allocated.TryGetValue(key, out val)) {
                    return true;
                }

                if (allocated.Count < max) {
                    val = unallocated.Take(cts.Token).Value;
                    var added = allocated.TryAdd(key, val);
                    if (!added) {
                        throw new InvalidOperationException("Could not add key to allocated dictionary.");
                    }
                    return true;
                }
            }
        }

        val = default;
        return false;
#pragma warning restore CS8601 // Possible null reference assignment.
    }

    public bool TryRemove(K key, out V val) {
#pragma warning disable CS8601 // Possible null reference assignment.
        if (allocated.TryRemove(key, out val)) {
            requested.Add(1);
            return true;
        }
        return false;
#pragma warning restore CS8601 // Possible null reference assignment.
    }

    public void Dispose() {
        cts.Cancel();
        requested.CompleteAdding();

        foreach (var handle in allocated.Values) {
            if (handle is IDisposable disposable) {
                disposable.Dispose();
            }
        }

        foreach (var lazyHandle in unallocated) {
            if (lazyHandle.IsValueCreated && lazyHandle.Value is IDisposable disposable) {
                disposable.Dispose();
            }
        }

        unallocated.Dispose();
        cts.Dispose();
    }
}
