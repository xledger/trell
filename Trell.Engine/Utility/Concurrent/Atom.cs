namespace Trell.Engine.Utility.Concurrent;

/// <summary>
/// Like the Atom in Clojure.
/// </summary>
/// <typeparam name="T"></typeparam>
class Atom<T> : IAtomRead<T> where T : class {
    volatile T? value;

    internal Atom(T? value = default) {
        this.value = value;
    }

    internal void Reset(T? value) {
        Interlocked.Exchange(ref this.value, value);
    }

    internal void Update(Func<T?, T?> update) {
        var spinner = new SpinWait();
        while (true) {
            var current = this.value;
            var updated = update(current);
            var prev = Interlocked.CompareExchange(ref this.value, updated, current);
            if (Equals(current, prev)) {
                break;
            }
            spinner.SpinOnce();
        }
    }

    public T? Value => this.value;
}
