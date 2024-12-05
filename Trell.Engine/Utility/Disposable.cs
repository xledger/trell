namespace Trell.Engine.Utility;

class Disposable : IDisposable, IAsyncDisposable {
    int isDisposed;
    Action? action;

    internal static Disposable FromAction(Action action) {
        return new Disposable {
            action = action
        };
    }

    public void Dispose() {
        if (0 == Interlocked.CompareExchange(ref this.isDisposed, 0, 1)) {
            this.action?.Invoke();
            this.action = null;
        }
    }

    public ValueTask DisposeAsync() {
        if (0 == Interlocked.CompareExchange(ref this.isDisposed, 0, 1)) {
            this.action?.Invoke();
            this.action = null;
        }
        return ValueTask.CompletedTask;
    }
}
