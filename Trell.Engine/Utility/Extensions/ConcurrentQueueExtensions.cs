using System.Collections.Concurrent;

namespace Trell.Engine.Utility.Extensions;

static class ConcurrentQueueExtensions {
    internal static void EnqueueRange<T>(this ConcurrentQueue<T> @this, IEnumerable<T> range) {
        foreach (var item in range) {
            @this.Enqueue(item);
        }
    }
}
