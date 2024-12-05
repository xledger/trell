using Trell.Engine.ClearScriptWrappers;
using Trell.Engine.Utility.Concurrent;

namespace Trell.Engine.Extensibility.Interfaces;

public interface IPlugin {
    PluginDotNetObject DotNetObject { get; }
    string JsScript { get; }
    IReadOnlyList<string> TopLevelJsNamesExposed { get; }
}

public record PluginDotNetObject(Func<IAtomRead<TrellExecutionContext>, EngineWrapper, object> Constructor, string AddToEngineWithName);
