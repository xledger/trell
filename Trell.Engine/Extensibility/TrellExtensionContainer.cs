using Trell.Engine.Extensibility.Interfaces;

namespace Trell.Engine.Extensibility;

public class TrellExtensionContainer {
    public class Builder {
        public ITrellLogger? Logger { get; set; }
        public IStorageProvider? Storage { get; set; }
        public List<IPlugin> Plugins { get; } = new();
        public List<ITrellObserver> Observers { get; } = new();

        public TrellExtensionContainer Build() {
            ArgumentNullException.ThrowIfNull(Logger);
            return new TrellExtensionContainer(Logger, Storage, Plugins, Observers);
        }
    }

    public ITrellLogger Logger { get; }
    public IStorageProvider? Storage { get; }
    public IReadOnlyList<IPlugin> Plugins { get; }
    public ITrellObserver Observer { get; }

    public TrellExtensionContainer(
        ITrellLogger logger,
        IStorageProvider? storage,
        IReadOnlyList<IPlugin> plugins,
        IReadOnlyList<ITrellObserver> observers
    ) {
        Logger = logger;
        Storage = storage;
        Plugins = plugins.ToArray();
        if (observers.Count == 0) {
            Observer = NoOpTrellObserver.Instance;
        } else if (observers.Count == 1) {
            Observer = observers[0];
        } else {
            Observer = new DelegatingTrellObserver(observers);
        }
    }
}

class NoOpTrellObserver : ITrellObserver {
    internal static readonly NoOpTrellObserver Instance = new();

    NoOpTrellObserver() { }
}
