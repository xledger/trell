namespace Trell.Engine.Utility.Concurrent;

public interface IAtomRead<T> where T : class
{
    T? Value { get; }
}
