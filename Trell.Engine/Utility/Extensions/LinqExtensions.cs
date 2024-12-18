namespace Trell.Engine.Utility.Extensions;

static class LinqExtensions {
    internal static void Deconstruct<K, V>(this IGrouping<K, V> @this, out K key, out IEnumerable<V> value) {
        key = @this.Key;
        value = @this;
    }

    internal static IEnumerable<(int, T)> Indexed<T>(this IEnumerable<T> @this) {
        var i = 0;
        foreach (var item in @this) {
            yield return (i, item);
            ++i;
        }
    }
}
