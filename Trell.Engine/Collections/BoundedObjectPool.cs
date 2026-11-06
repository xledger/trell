using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Trell.Engine.Collections;

/// <summary>
/// A bounded, concurrent object pool that preinitializes some number of objects and binds
/// each one to a key.
/// </summary>
/// <remarks>
/// When [pending] > 0, gets the Lazy.Value of each unallocated object in a
/// Task so that any long construction time happens before the object is needed.
///
/// A caller that acquires a key holds a lease on the key's object until it disposes the
/// lease. When every slot is bound and a new key arrives, the pool unbinds the key that has
/// gone longest without a lease, provided it has been idle for at least [idleTimeout], and
/// binds its object to the new key. A negative [idleTimeout] means the pool never unbinds
/// a key by itself.
/// </remarks>
sealed class BoundedObjectPool<K, V>
    : IDisposable
    where K : notnull
{
    readonly int max, pending;
    readonly TimeSpan idleTimeout;
    readonly TimeProvider time;

    readonly BlockingCollection<int> requested = new();
    readonly BlockingCollection<Lazy<V>> unallocated;

    // Guarded by the lock on itself.
    readonly Dictionary<K, Binding> allocated = new();

    readonly CancellationTokenSource cts = new();

    internal sealed class Binding(Lazy<V> value) {
        public readonly Lazy<V> Value = value;
        public int Leases;
        public long IdleSince;
    }

    /// <summary>
    /// One caller's use of a key's object. Dispose it when the use ends.
    /// </summary>
    public sealed class Lease : IDisposable {
        readonly BoundedObjectPool<K, V> pool;
        readonly Binding binding;
        int disposed;

        internal Lease(BoundedObjectPool<K, V> pool, Binding binding, V value) {
            this.pool = pool;
            this.binding = binding;
            Value = value;
        }

        public V Value { get; }

        public void Dispose() {
            if (Interlocked.Exchange(ref disposed, 1) == 0) {
                pool.EndLease(binding);
            }
        }
    }

    public BoundedObjectPool(
        Func<V> ctor,
        int max = 1,
        int pending = 0,
        TimeSpan? idleTimeout = null,
        TimeProvider? time = null
    ) {
        if (max < 1) {
            throw new ArgumentOutOfRangeException(nameof(max), max, "Must be > 0");
        }

        this.max = max;
        this.pending = pending;
        this.idleTimeout = idleTimeout ?? Timeout.InfiniteTimeSpan;
        this.time = time ?? TimeProvider.System;
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

    public int Count {
        get {
            lock (allocated) {
                return allocated.Count;
            }
        }
    }

    public int Pending => unallocated.Count;

    /// <summary>
    /// The objects currently bound to a key, whether or not anyone holds a lease on them.
    /// </summary>
    public IEnumerable<V> Values {
        get {
            lock (allocated) {
                return allocated.Values
                    .Where(b => b.Value.IsValueCreated)
                    .Select(b => b.Value.Value)
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Binds key to an object, unless every slot is bound to a key that is in use or has not
    /// been idle long enough. Returns false in that case.
    /// </summary>
    public bool TryAcquire(K key, [NotNullWhen(true)] out Lease? lease) {
        Binding binding;
        lock (allocated) {
            if (!allocated.TryGetValue(key, out binding!)) {
                if (allocated.Count < max) {
                    binding = new Binding(unallocated.Take(cts.Token));
                } else if (TryUnbindIdle() is Binding idle) {
                    binding = new Binding(idle.Value);
                } else {
                    lease = null;
                    return false;
                }
                allocated.Add(key, binding);
            }
            binding.Leases++;
        }

        // Create the object outside the lock so that a slow constructor does not hold up
        // callers that want other keys.
        V value;
        try {
            value = binding.Value.Value;
        } catch {
            lock (allocated) {
                binding.Leases--;
                if (allocated.TryGetValue(key, out var current) && current == binding) {
                    allocated.Remove(key);
                    requested.Add(1);
                }
            }
            throw;
        }
        lease = new Lease(this, binding, value);
        return true;
    }

    /// <summary>
    /// Binds key to an object without holding a lease on it.
    /// </summary>
    public bool TryGet(K key, out V val) {
        if (TryAcquire(key, out var lease)) {
            val = lease.Value;
            lease.Dispose();
            return true;
        }
#pragma warning disable CS8601 // Possible null reference assignment.
        val = default;
#pragma warning restore CS8601 // Possible null reference assignment.
        return false;
    }

    /// <summary>
    /// Unbinds key and discards its object. The pool creates a new object to fill the slot.
    /// </summary>
    public bool TryRemove(K key, out V val) {
        lock (allocated) {
            if (allocated.Remove(key, out var binding)) {
                requested.Add(1);
                val = binding.Value.Value;
                return true;
            }
        }
#pragma warning disable CS8601 // Possible null reference assignment.
        val = default;
#pragma warning restore CS8601 // Possible null reference assignment.
        return false;
    }

    void EndLease(Binding binding) {
        lock (allocated) {
            binding.Leases--;
            if (binding.Leases == 0) {
                binding.IdleSince = time.GetTimestamp();
            }
        }
    }

    // Caller holds the lock on allocated.
    Binding? TryUnbindIdle() {
        if (idleTimeout < TimeSpan.Zero) {
            return null;
        }

        K? oldestKey = default;
        Binding? oldest = null;
        foreach (var (key, binding) in allocated) {
            if (binding.Leases == 0
                && binding.Value.IsValueCreated
                && time.GetElapsedTime(binding.IdleSince) >= idleTimeout
                && (oldest is null || binding.IdleSince < oldest.IdleSince)
            ) {
                oldestKey = key;
                oldest = binding;
            }
        }

        if (oldest is not null) {
            allocated.Remove(oldestKey!);
        }
        return oldest;
    }

    public void Dispose() {
        cts.Cancel();
        requested.CompleteAdding();

        List<Binding> bindings;
        lock (allocated) {
            bindings = allocated.Values.ToList();
        }
        foreach (var binding in bindings) {
            if (binding.Value.IsValueCreated && binding.Value.Value is IDisposable disposable) {
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
