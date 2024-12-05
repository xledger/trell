using Microsoft.ClearScript;
using System.Diagnostics.CodeAnalysis;

namespace Trell.Engine.ClearScriptHelpers;

static class ScriptingInterop {
    internal static bool IsNullLike([NotNullWhen(false)] object o) {
        return o is null || o == Undefined.Value;
    }
}
