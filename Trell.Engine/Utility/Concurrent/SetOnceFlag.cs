namespace Trell.Engine.Utility.Concurrent;

class SetOnceFlag {
    int isFlagSet;

    internal SetOnceFlag(bool isFlagSet = false) {
        this.isFlagSet = isFlagSet ? 1 : 0;
    }

    internal bool IsFlagSet => this.isFlagSet == 1;

    internal bool TrySet() {
        return 0 == Interlocked.CompareExchange(ref this.isFlagSet, 1, 0);
    }
}
