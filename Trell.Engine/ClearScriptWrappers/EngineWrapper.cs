using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;
using Serilog;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;
using Trell.Engine.RuntimeApis;
using Trell.Engine.Utility.Concurrent;
using Trell.Engine.Utility.IO;

namespace Trell.Engine.ClearScriptWrappers;

public class EngineWrapper : IDisposable {
    public sealed record Work(
        RuntimeLimits Limits,
        string JsonEnv,
        AbsolutePath SourceDirectory,
        string Name
    ) {
        public abstract record ArgType {
            public sealed record Json(string JsonString) : ArgType;
            public sealed record Raw(object Object) : ArgType;
            public sealed record None : ArgType;

            public static readonly ArgType NONE = new None();

            protected ArgType() { }
        }

        public ArgType Arg { get; init; } = ArgType.NONE;

        public TrellPath WorkerJs { get; init; } = TrellPath.WorkerJs;
    }

    readonly V8ScriptEngine engine;
    readonly Atom<TrellExecutionContext> currentContext = new();
    readonly SetOnceFlag isDisposed = new();
    readonly ITrellLogger log;
    readonly RuntimeLimits limits;

    internal EngineWrapper(V8ScriptEngine engine, TrellExtensionContainer extensionContainer, IReadOnlyList<IPlugin> extraPlugins, RuntimeLimits limits) {
        this.engine = engine;

        this.log = extensionContainer.Logger;
        this.limits = limits;
        Log.Debug("Engine runtime limits: {Limits}", limits);

        static void AddPlugin(IAtomRead<TrellExecutionContext> ctx, EngineWrapper engine, IPlugin plugin) {
            var instance = plugin.DotNetObject.Constructor(ctx, engine);
            var name = plugin.DotNetObject.AddToEngineWithName;
            engine.engine.AddHostObject(name, instance);
            engine.engine.Execute(plugin.JsScript);
        }

        AddPlugin(this.currentContext, this, new BrowserApi(extensionContainer.Logger));

        foreach (var plugin in extensionContainer.Plugins.Concat(extraPlugins)) {
            AddPlugin(this.currentContext, this, plugin);
        }
    }

    internal EngineWrapper(V8ScriptEngine engine, TrellExtensionContainer extensionContainer, RuntimeLimits limits)
        : this(engine, extensionContainer, [], limits) { }

    void EnableSourceLoading(AbsolutePath path) {
        Log.Debug("Source loading enabled from {Path}", path);

        this.engine.DocumentSettings.AccessFlags |= DocumentAccessFlags.EnableFileLoading;
        this.engine.DocumentSettings.SearchPath = path;
        this.engine.DocumentSettings.FileNameExtensions = ".js";
        this.engine.DocumentSettings.Loader = new TrellDocumentLoader();
    }

    public class TrellDocumentLoader : DocumentLoader {
        /// <summary>
        /// Concatenates `dir` and `file` and ensures that absolute path exists underneath `root`.
        /// </summary>
        public static bool TryGetRootedPath(string root, string dir, string file, out string path) {
            if (!Path.EndsInDirectorySeparator(root)) {
                root += Path.DirectorySeparatorChar;
            }
            root = Path.GetFullPath(root);

            if (!Path.EndsInDirectorySeparator(dir)) {
                dir += Path.DirectorySeparatorChar;
            }
            dir = Path.GetFullPath(dir);

            path = Path.GetFullPath(Path.Combine(dir, file));

            return path.StartsWith(root);
        }

        public override async Task<Document> LoadDocumentAsync(
            DocumentSettings settings,
            DocumentInfo? sourceInfo,
            string specifier,
            DocumentCategory category,
            DocumentContextCallback contextCallback
        ) {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNullOrEmpty(specifier);
            if (!settings.AccessFlags.HasFlag(DocumentAccessFlags.EnableFileLoading)) {
                throw new UnauthorizedAccessException("File loading must be enabled on the script engine.");
            }

            var root = settings.SearchPath;

            var dir = settings.SearchPath;
            if (sourceInfo is DocumentInfo docInfo && docInfo.Uri is Uri docUri) {
                dir = Path.GetDirectoryName(docUri.AbsolutePath) ?? "";
                // Relocate the parent's including directory underneath the
                // root directory search path (the worker's src).
                dir = Path.Join(root, dir);
            }

            var extensions = settings.FileNameExtensions.Split(";");

            if (TryGetRootedPath(root, dir, specifier, out var path)
                && File.Exists(path) && extensions.Contains(Path.GetExtension(path))) {
                // The paths we give to ClearScript appear to be at / so that
                // we can hide the actual path from the user.
                var pathUnderRoot = path.Substring(root.Length);
                var uri = new Uri("file://" + pathUnderRoot, UriKind.Absolute);
                var info = new DocumentInfo(uri) {
                    Category = category,
                    ContextCallback = contextCallback,
                };
                if (settings.LoadCallback is not null) {
                    settings.LoadCallback(ref info);
                }
                Log.Verbose("Loading script {Path}", path);
                var contents = await File.ReadAllTextAsync(path);
                var doc = new StringDocument(info, contents);
                return doc;
            }

            throw new FileNotFoundException(null, specifier);
        }
    }

    static async Task<object?> ResolveAsync(object? result) {
        if (result is Task<object> resultTask) {
            result = await resultTask;
        } else if (result is Task voidTask) {
            await voidTask;
            result = null;
        }
        return result;
    }

    // There's something weird with ClearScript.  We should be able to pass
    // an IPropertyBag into the engine and have it be treated as a JS object.
    // But when we pass it in via IJavaScriptObject::InvokeAsFunction(), we get
    // `null` in place of the object within the engine.  So this ugly solution
    // is temporary, until we can figure out why IPropertyBags are getting mapped
    // to `null`.  Oddly, the objects map correctly if inside something else.  For
    // example the ContextWrapper has an IPropertyBag that's (as should be) accessible
    // from within the engine.
    IScriptObject CreateScriptObject(IDictionary<string, object> dict) {
        var obj = (IScriptObject)this.engine.Evaluate("({})");
        foreach (var item in dict) {
            obj.SetProperty(item.Key, item.Value);
        }
        return obj;
    }

    public IArrayBuffer CreateJsBuffer(byte[] contents) {
        var buf = (IArrayBuffer)((ScriptObject)this.engine.Evaluate("ArrayBuffer")).Invoke(true, [contents.Length]);
        if (contents.Length > 0) {
            buf.WriteBytes(contents, 0, (ulong)contents.Length, 0);
        }
        return buf;
    }

    public ScriptObject CreateJsFile(string filename, string type, byte[] contents) {
        return (ScriptObject)((ScriptObject)this.engine.Evaluate("File")).Invoke(
            true,
            [
                new object[] { CreateJsBuffer(contents) },
                filename,
                new { type }
            ]
        );
    }

    public async Task<object?> RunWorkAsync(TrellExecutionContext ctx, Work work) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
        ctx = ctx.WithCancellationToken(linked.Token);
        this.currentContext.Reset(ctx);
        try {
            EnableSourceLoading(work.SourceDirectory);
            var limits = this.limits.RestrictBy(work.Limits);

            var docInfo = new DocumentInfo {
                Category = ModuleCategory.Standard
            };
            object module;

            using (var t = Cancel(this.engine, linked, this.currentContext).After(limits.MaxStartupDuration)) {
                // TODO: This scheme allows for engine interrupts to be propagated via exception to here.
                // TODO: But it exposes a pointless Interruptable function to do so.
                // TODO: Otherwise just checking the return type from the engine
                // TODO: allows us to see if the module startup succeeded.
                //engine.Script.Interruptable = new Func<dynamic, dynamic>(fn => {
                //    linked.Token.ThrowIfCancellationRequested();
                //    return fn();
                //});
                //var loadWorkerJs = "Interruptable(() => { return import('worker.js') })";

                var loadWorkerJs = $"import * as hooks from '{work.WorkerJs}'; hooks;";
                module = this.engine.Evaluate(docInfo, loadWorkerJs);
                Log.Information("Evaluated `{Js}` to {M}", loadWorkerJs, module);

                if (module is Task<object> moduleResultTask) {
                    throw new TrellUserException(
                        new TrellError(TrellErrorCode.TIMEOUT, $"worker.js took longer than {limits.MaxStartupDuration} to load"));
                }
            }

            if (!(module is IJavaScriptObject worker &&
                worker["default"] is IJavaScriptObject export &&
                export[work.Name] is IJavaScriptObject fn && fn.Kind == JavaScriptObjectKind.Function)) {
                throw new TrellUserException(
                    new TrellError(TrellErrorCode.ENTRY_POINT_NOT_DEFINED, $"{work.WorkerJs} does not export function `{work.Name}`"));
            }

            using (var t = Cancel(this.engine, linked, this.currentContext).After(limits.MaxExecutionDuration)) {
                //var constructor = (ScriptObject)engine.Script.Uint8Array; // ScriptEngine.Current.Script.Float64Array;
                //var typedArray = (ITypedArray<byte>)constructor.Invoke(true, work.Arg["body"]);
                //work.Arg["body"] = typedArray;
                var result = work.Arg switch {
                    Work.ArgType.None _ =>
                        ((IScriptObject)this.engine.Evaluate(
                            "((hookFn, env, ctx) => hookFn(null, JSON.parse(env), { id: ctx.Id, data: JSON.parse(ctx.JsonData) }))"
                        )).InvokeAsFunction(fn, work.JsonEnv, ctx),
                    Work.ArgType.Raw x =>
                        ((IScriptObject)this.engine.Evaluate(
                            "((hookFn, arg, env, ctx) => hookFn(arg, JSON.parse(env), { id: ctx.Id, data: JSON.parse(ctx.JsonData) }))"
                        )).InvokeAsFunction(fn, x, work.JsonEnv, ctx),
                    Work.ArgType.Json x =>
                        ((IScriptObject)this.engine.Evaluate(
                            "((hookFn, jsonData, env, ctx) => hookFn(JSON.parse(jsonData), JSON.parse(env), { id: ctx.Id, data: JSON.parse(ctx.JsonData) }))"
                        )).InvokeAsFunction(
                          fn,
                          x.JsonString,
                          work.JsonEnv,
                          ctx
                        ),
                    _ => throw new NotSupportedException()
                };
                if (result is ScriptObject so && so.GetProperty("then") is IJavaScriptObject then and { Kind: JavaScriptObjectKind.Function }) {
                    var tcs = new TaskCompletionSource<object?>();
                    so.InvokeMethod("then", (object v) => {
                        tcs.SetResult(v);
                    });
                    if (so.GetProperty("catch") is IJavaScriptObject ctch and { Kind: JavaScriptObjectKind.Function }) {
                        so.InvokeMethod("catch", (object err) => {
                            // TODO: figure out what kind of exception to use here and how to convert from js object
                            tcs.SetException(new Exception(err.ToString()));
                        });
                    }
                    result = await tcs.Task;
                } else if (result is Task<object?> resultTask) {
                    result = await resultTask;
                }

                result = ((ScriptObject)this.engine.Evaluate("JSON")).InvokeMethod("stringify", result);
                Log.Debug("Resolved `{Name}(...)` to {R}", work.Name, result);
                return await ResolveAsync(result);
            }
        } finally {
            this.currentContext.Reset(null);
        }
    }

    protected virtual void Dispose(bool disposing) {
        if (this.isDisposed.TrySet()) {
            //if (disposing) {
            //    TODO: dispose managed state (managed objects)
            //}
            this.engine.Dispose();

            // TODO: free unmanaged resources (unmanaged objects) and override finalizer
            // TODO: set large fields to null
        }
    }

    ~EngineWrapper() {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: false);
    }

    public void Dispose() {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    static CancelWrapper Cancel(V8ScriptEngine engine, CancellationTokenSource source, IAtomRead<TrellExecutionContext> ctx) =>
        new(engine, source, ctx);

    readonly struct CancelWrapper {

        readonly V8ScriptEngine engine;
        readonly CancellationTokenSource source;
        readonly IAtomRead<TrellExecutionContext> context;

        public CancelWrapper(V8ScriptEngine engine, CancellationTokenSource source, IAtomRead<TrellExecutionContext> context) {
            this.engine = engine;
            this.source = source;
            this.context = context;
            source.Token.Register(Interrupt);
        }

        void Interrupt(object? start) {
            Log.Warning("Execution {Id} for {User} interrupted.",
                this.context.Value?.Id, this.context.Value?.User.Id);
            // Interrupt deals with synchronous engine execution.
            this.engine.Interrupt();
            // Cancel deals with asynchronous execution in C# APIs.
            this.source.Cancel();
        }

        void Interrupt() {
            Interrupt(null);
        }

        public Timer After(TimeSpan timeout) {
            if (this.source.IsCancellationRequested) {
                this.engine.Interrupt();
                this.source.Token.ThrowIfCancellationRequested();
            }
            var timer = new Timer(Interrupt);
            // After the timeout, keep interrupting every 25 milliseconds until this timer is stopped.
            // No, seriously, engine, you need to stop running this code.
            timer.Change(timeout, TimeSpan.FromMilliseconds(25));
            return timer;
        }
    }
}
