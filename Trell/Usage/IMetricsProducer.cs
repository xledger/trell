using Trell.Engine.Extensibility;

namespace Trell.Usage;

interface IMetricsProducer {
    TrellMetrics? EmitMetrics(MetricsTrigger trigger);
    void PollProcessMetrics();
    IDisposable OnEmitMetrics(Action<MetricsTrigger> callback);
    void TrackDatabases(string userId, string? workerId, IReadOnlyList<string> dbs);
}
