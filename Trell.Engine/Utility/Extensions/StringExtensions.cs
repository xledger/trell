namespace Trell.Engine.Utility.Extensions;

static class StringExtensions {
    public static string? NullIfBlank(this string? @this) {
        return string.IsNullOrWhiteSpace(@this)
            ? null
            : @this;
    }
}
