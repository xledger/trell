using System.Collections.Concurrent;
using Trell.IPC.Server;

namespace Trell.Collections;
/// <summary>
/// A bounded, concurrent object pool that preinitializes some number of objects.
/// </summary>
/// <remarks>
/// When [pending] > 0, gets the Lazy.Value of each unallocated object in a
/// Task so that any long construction time happens before the object is needed.
/// </remarks>
sealed class BoundedObjectPool<K, V> : IDisposable, IObjectPool<K, V>
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
        this.unallocated = new BlockingCollection<Lazy<V>>(Math.Max(pending, 1));
        _ = Task.Run(() => {
            while (true) {
                var n = this.requested.Take(this.cts.Token);
                for (var i = 0; i < n; ++i) {
                    var next = new Lazy<V>(ctor);
                    this.unallocated.Add(next);
                    if (pending > i) {
                        // After adding, get value to start creation.
                        _ = Task.Run(() => next.Value);
                    }
                }
            }
        });
        this.requested.Add(max);
    }

    public int Count => this.allocated.Count;
    public int Pending => this.unallocated.Count;

    public IEnumerable<V> Values => this.allocated.Values;

    public bool TryGet(K key, out V val) {
#pragma warning disable CS8601 // Possible null reference assignment.
        if (this.allocated.TryGetValue(key, out val)) {
            return true;
        }

        if (this.allocated.Count < this.max) {
            lock (this.serializedCreation) {
                if (this.allocated.TryGetValue(key, out val)) {
                    return true;
                }

                if (this.allocated.Count < this.max) {
                    val = this.unallocated.Take(this.cts.Token).Value;
                    var added = this.allocated.TryAdd(key, val);
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
        if (this.allocated.TryRemove(key, out val)) {
            this.requested.Add(1);
            return true;
        }
        return false;
#pragma warning restore CS8601 // Possible null reference assignment.
    }

    public void Dispose() {
        this.cts.Cancel();
        this.requested.CompleteAdding();

        foreach (var handle in this.allocated.Values) {
            if (handle is IDisposable disposable) {
                disposable.Dispose();
            }
        }

        foreach (var lazyHandle in this.unallocated) {
            if (lazyHandle.IsValueCreated && lazyHandle.Value is IDisposable disposable) {
                disposable.Dispose();
            }
        }

        this.unallocated.Dispose();
        this.cts.Dispose();
    }
}
