using Trell.Engine.Extensibility.Interfaces;

namespace Trell.Engine.Extensibility;

class DelegatingTrellObserver : ITrellObserver {
    delegate void OnProcessStateChange(TrellProcessInfo info);
    delegate void OnMetrics(TrellMetrics metrics);
    delegate void OnJob(object job);

    readonly OnProcessStateChange onStartup = null!;
    readonly OnProcessStateChange onShutdown = null!;
    readonly OnMetrics onMetricsCollected = null!;
    readonly OnJob onJobStarted = null!;
    readonly OnJob onJobStopped = null!;

    internal DelegatingTrellObserver(IReadOnlyList<ITrellObserver> observers) {
        if (observers.Count < 2) {
            throw new ArgumentException($"Must pass at least 2 observers to {nameof(DelegatingTrellObserver)}.");
        }
        foreach (var observer in observers) {
            onStartup += observer.OnStartup;
            onShutdown += observer.OnShutdown;
            onMetricsCollected += observer.OnMetricsCollected;
            onJobStarted += observer.OnJobStarted;
            onJobStopped += observer.OnJobStopped;
        }
    }

    void ITrellObserver.OnStartup(TrellProcessInfo info) => onStartup(info);
    void ITrellObserver.OnShutdown(TrellProcessInfo info) => onShutdown(info);
    void ITrellObserver.OnMetricsCollected(TrellMetrics metrics) => onMetricsCollected(metrics);
    void ITrellObserver.OnJobStarted(object job) => onJobStarted(job);
    void ITrellObserver.OnJobStopped(object job) => onJobStopped(job);
}
