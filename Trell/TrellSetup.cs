using Serilog;
using System.Reflection;
using System.Runtime.Loader;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;

namespace Trell;

static class TrellSetup {
    static readonly Type[] NO_PARAMS = Array.Empty<Type>();
    static readonly Type[] CONFIG_PARAMS = new Type[] { typeof(IReadOnlyDictionary<string, string>) };

    internal static TrellExtensionContainer ExtensionContainer(TrellConfig config) {
        var builder = new TrellExtensionContainer.Builder();

        // Initializer logger.
        {
            var asm = LoadAssembly(config.Logger.Asm);
            builder.Logger = LoadExtension<ITrellLogger>(asm, config.Logger);
        }

        // Initialize storage.
        builder.Storage = new LocalFolderStorage(
            config.Storage.Path,
            config.Storage.MaxDatabasePageCount);
        Log.Information("Local folder storage: {Path}", config.Storage.Path);

        // Initialize plugins.
        var pluginConfigsByAsm = config.Plugins.ToLookup(p => p.Asm);
        foreach (var (asmName, pluginConfigs) in pluginConfigsByAsm) {
            var asm = LoadAssembly(asmName);
            foreach (var pluginConfig in pluginConfigs) {
                var plugin = LoadExtension<IPlugin>(asm, pluginConfig);
                builder.Plugins.Add(plugin);
            }
        }

        // Initialize observers.
        var observerConfigsByAsm = config.Observers.ToLookup(p => p.Asm);
        foreach (var (asmName, observerConfigs) in observerConfigsByAsm) {
            var asm = LoadAssembly(asmName);
            foreach (var observerConfig in observerConfigs) {
                var observer = LoadExtension<ITrellObserver>(asm, observerConfig);
                builder.Observers.Add(observer);
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Loads the assembly in the named path, or returns the main Trell assembly if the path is empty.
    /// </summary>
    static Assembly LoadAssembly(string path) {
        if (string.IsNullOrEmpty(path)) {
            return typeof(TrellSetup).Assembly;
        }

        path = Path.GetFullPath(path);
        if (!File.Exists(path)) {
            throw new FileNotFoundException("Could not find assembly.", path);
        }
        var name = new AssemblyName(Path.GetFileNameWithoutExtension(path));
        Log.Information("Loading extension assembly {Name} from {Path}.", name, path);
        var loader = new TrellAssemblyLoadContext(path);
        return loader.LoadFromAssemblyName(name);
    }

    static T LoadExtension<T>(Assembly asm, TrellConfig.ExtensionConfig ext) where T : class {
        if (asm.GetType(ext.Type) is Type typ) {
            if (typeof(T).IsAssignableFrom(typ)) {
                Log.Information("Found extension {Asm}/{Type}.", asm.GetName().Name, typ);
                if (typ.GetConstructor(NO_PARAMS) is ConstructorInfo emptyCtor) {
                    return (T)emptyCtor.Invoke(null);
                } else if (typ.GetConstructor(CONFIG_PARAMS) is ConstructorInfo roDictCtor) {
                    return (T)roDictCtor.Invoke(new[] { ext.Config });
                } else {
                    var paramNames = string.Join(", ", CONFIG_PARAMS.Select(PrettyName));
                    Log.Error("For extension {Asm}/{Type}, could not find default constructor or constructor taking {Parameters}.",
                        asm.GetName().Name, typ, paramNames);
                    throw new TrellException("Missing extension class constructor.");
                }
            } else {
                Log.Error("Extension {Asm}/{Type} does not implement {Interface}.",
                    asm.GetName().Name, typ, typeof(T));
                throw new TrellException($"Extension class does not implement {typeof(T)}.");
            }
        } else {
            Log.Error("Assembly {Asm} does not define type {Type}.", asm.GetName().Name, ext.Type);
            throw new TrellException("Assembly does not define type.");
        }
    }

    static string PrettyName(Type t) {
        if (t.IsGenericType) {
            var sb = new StringBuilder();
            sb.Append(t.Name.Remove(t.Name.IndexOf('`')));
            sb.Append('<');
            foreach (var (i, arg) in t.GetGenericArguments().Indexed()) {
                if (i > 0) {
                    sb.Append(", ");
                }
                sb.Append(PrettyName(arg));
            }
            sb.Append('>');
            return sb.ToString();
        } else {
            return t.Name;
        }
    }
}

// https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support
class TrellAssemblyLoadContext : AssemblyLoadContext {
    readonly AssemblyDependencyResolver resolver;

    public TrellAssemblyLoadContext(string path) {
        this.resolver = new AssemblyDependencyResolver(path);
    }

    protected override Assembly? Load(AssemblyName name) {
        var path = this.resolver.ResolveAssemblyToPath(name);
        if (path != null) {
            return LoadFromAssemblyPath(path);
        }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) {
        var path = this.resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path != null) {
            return LoadUnmanagedDllFromPath(path);
        }

        return IntPtr.Zero;
    }
}
