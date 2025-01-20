using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Trell.IPC.Server;

namespace Trell.Collections;

sealed class InfiniteSingletonObjectPool<K, V> : IObjectPool<K, V> where K : notnull {
    readonly V singleton;

    public InfiniteSingletonObjectPool(V singleton) {
        this.singleton = singleton;
    }

    public IEnumerable<V> Values => [this.singleton];

    public bool TryGet(K key, out V val) {
        val = this.singleton;
        return true;
    }

    public void Dispose() {
        if (this.singleton is IDisposable disposable) {
            disposable.Dispose();
        }
    }
}
