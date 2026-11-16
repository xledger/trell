namespace Trell.Engine.Utility.Concurrent;

class SetOnceFlag {
    int isFlagSet;

    internal SetOnceFlag(bool isFlagSet = false) {
        this.isFlagSet = isFlagSet ? 1 : 0;
    }

    internal bool IsFlagSet => isFlagSet == 1;

    internal bool TrySet() {
        return 0 == Interlocked.CompareExchange(ref isFlagSet, 1, 0);
    }
}
