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
            //if (disposing) {
            //    // TODO: dispose managed state (managed objects)
            //}
            this.runtime.Dispose();

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
        }
    }

    ~RuntimeWrapper() {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: false);
    }

    public void Dispose() {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
