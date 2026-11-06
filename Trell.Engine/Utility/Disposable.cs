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
        if (0 == Interlocked.CompareExchange(ref isDisposed, 0, 1)) {
            action?.Invoke();
            action = null;
        }
    }

    public ValueTask DisposeAsync() {
        if (0 == Interlocked.CompareExchange(ref isDisposed, 0, 1)) {
            action?.Invoke();
            action = null;
        }
        return ValueTask.CompletedTask;
    }
}
