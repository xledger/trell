namespace Trell.Engine.Utility.Extensions;

static class ObjectExtensions {
    /// <summary>
    /// Given an object that may be null:
    ///   - if null, return null
    ///   - if non-null, return the result of f applied to it
    /// Similar to Option.Map in F#, except no wrapper object is needed.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="U"></typeparam>
    /// <param name="this"></param>
    /// <param name="f"></param>
    /// <returns></returns>
    internal static U? Maybe<T, U>(
        this T? @this,
        Func<T, U> f
    ) where T : class {
        if (@this is null) {
            return default;
        }
        return f(@this);
    }

    /// <summary>
    /// Given an object that may be null:
    ///   - if null, return null
    ///   - if non-null, return the result of f applied to it
    /// Similar to Option.Map in F#, except no wrapper object is needed.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="U"></typeparam>
    /// <param name="this"></param>
    /// <param name="f"></param>
    /// <returns></returns>
    internal static U? Maybe<T, U>(
        this T? @this,
        Func<T, U> f
    ) where T : struct {
        if (@this is null) {
            return default;
        }
        return f(@this.Value);
    }
}
