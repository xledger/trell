using Microsoft.ClearScript.V8;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;
using Trell.Engine.Utility.Concurrent;

namespace Trell.Engine.ClearScriptWrappers;

public class RuntimeWrapper : IDisposable {
    public record Config {
        public RuntimeLimits Limits { get; init; } = new();
    }

    readonly V8Runtime runtime;
    readonly TrellExtensionContainer extensionContainer;
    readonly SetOnceFlag isDisposed = new();
    readonly Config config;

    public RuntimeWrapper(TrellExtensionContainer extensionContainer, Config config) {
        this.runtime = new V8Runtime();
        this.extensionContainer = extensionContainer;
        this.config = config;
    }

    public EngineWrapper CreateScriptEngine(IReadOnlyList<IPlugin> extraPlugins) {
        var raw = this.runtime.CreateScriptEngine(
            V8ScriptEngineFlags.EnableTaskPromiseConversion
            | V8ScriptEngineFlags.EnableValueTaskPromiseConversion
            | V8ScriptEngineFlags.EnableDateTimeConversion
            | V8ScriptEngineFlags.EnableDynamicModuleImports);
        raw.EnableRuntimeInterruptPropagation = true;
        return new EngineWrapper(raw, this.extensionContainer, extraPlugins, this.config.Limits);
    }

    protected virtual void Dispose(bool disposing) {
        if (this.isDisposed.TrySet()) {
            this.runtime.Dispose();
        }
    }

    ~RuntimeWrapper() {
        Dispose(disposing: false);
    }

    public void Dispose() {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
