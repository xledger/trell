using System.Diagnostics.CodeAnalysis;
using Microsoft.ClearScript;

namespace Trell.Engine.ClearScriptHelpers;

static class ScriptingInterop {
    internal static bool IsNullLike([NotNullWhen(false)] object o) {
        return o is null || o == Undefined.Value;
    }
}
