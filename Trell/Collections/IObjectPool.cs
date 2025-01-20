using Trell.IPC.Server;

namespace Trell.Collections;

interface IObjectPool<K, V> : IDisposable where K : notnull {
    IEnumerable<V> Values { get; }

    bool TryGet(K key, out V val);
}
