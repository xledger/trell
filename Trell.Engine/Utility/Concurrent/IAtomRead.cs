namespace Trell.Engine.Utility.Concurrent;

public interface IAtomRead<T> where T : class {
    public T? Value { get; }
}
