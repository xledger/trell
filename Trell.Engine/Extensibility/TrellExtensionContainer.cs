using Trell.Engine.Extensibility.Interfaces;

namespace Trell.Engine.Extensibility;

public class TrellExtensionContainer {
    public class Builder {
        public ITrellLogger? Logger { get; set; }
        public IStorageProvider? Storage { get; set; }
        public List<IPlugin> Plugins { get; } = new();
        public List<ITrellObserver> Observers { get; } = new();

        public TrellExtensionContainer Build() {
            ArgumentNullException.ThrowIfNull(this.Logger);
            return new TrellExtensionContainer(this.Logger, this.Storage, this.Plugins, this.Observers);
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
        this.Logger = logger;
        this.Storage = storage;
        this.Plugins = plugins.ToArray();
        if (observers.Count == 0) {
            this.Observer = NoOpTrellObserver.Instance;
        } else if (observers.Count == 1) {
            this.Observer = observers[0];
        } else {
            this.Observer = new DelegatingTrellObserver(observers);
        }
    }
}

class NoOpTrellObserver : ITrellObserver {
    internal static readonly NoOpTrellObserver Instance = new();

    NoOpTrellObserver() { }
}
