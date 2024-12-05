using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Trell.Engine.Utility;

static class Assert {
    [DoesNotReturn]
    static internal void False() => throw new AssertionFailed("False.");

    static internal void IsTrue(bool condition, [CallerArgumentExpression(nameof(condition))] string conditionExpression = "") {
        if (!condition) {
            throw new AssertionFailed($"The expression \"{conditionExpression}\" is false.");
        }
    }

    static internal void NotNull([NotNull] object? o, [CallerArgumentExpression(nameof(o))] string conditionExpression = "") {
        if (o is null) {
            throw new AssertionFailed($"The expression \"{conditionExpression}\" is false.");
        }
    }

    static internal void Equals<T>(T left, T right,
        [CallerArgumentExpression(nameof(left))] string leftExpr = "",
        [CallerArgumentExpression(nameof(left))] string rightExpr = ""
    ) where T : class {
        if (left != right) {
            throw new AssertionFailed($"The expressions \"{leftExpr}\" and \"{rightExpr}\" are not equal.");
        }
    }

    internal class AssertionFailed : Exception {
        internal AssertionFailed(string message) : base(message) { }
    }
}
