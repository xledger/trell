using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;
using Trell.Engine.Utility;
using Trell.IPC.Server;

namespace Trell.Usage;

partial class EventCountersWorkerMetrics : IMetricsProducer {
    readonly WorkerHandle worker;
    readonly ITrellObserver observer;

    TrellConfig Config => this.worker.Options.Config;
    WorkerAddress WorkerAddress => this.worker.Options.WorkerAddress;
    Process Process => this.worker.Process;

    // Tracked via EventPipe.
    long bytesReceived, bytesSent;
    double cpuUsage;

    // Tracked via GetProcessIoCounters on Windows, or /proc/{pid}/io on Linux
    long bytesRead, bytesWritten;
    readonly ConcurrentQueue<TrellStorageMetric> bytesStored = [];

    // Event handlers
    readonly HashSet<Action<MetricsTrigger>> onEmitMetricsCallbacks = [];

    public EventCountersWorkerMetrics(WorkerHandle worker, ITrellObserver observer) {
        this.worker = worker;
        this.observer = observer;
    }

    TrellMetrics? CaptureSnapshot(MetricsTrigger trigger = MetricsTrigger.Interval) {
        if (this.Process.HasExited) {
            return null;
        }

        // Refresh the current process property values.
        this.Process.Refresh();

        var bytesStoredCopy = new List<TrellStorageMetric>(this.bytesStored.Count);
        while (this.bytesStored.TryDequeue(out var stored)) {
            bytesStoredCopy.Add(stored);
        }

        // Some of these values are refreshed asynchronously. That is OK.
        return new TrellMetrics(this.WorkerAddress.WorkerId, trigger) {
            ProcessId = this.Process.Id,
            Executions = this.worker.Executions,
            PrivateMemorySize64 = this.Process.PrivateMemorySize64,
            WorkingSet64 = this.Process.WorkingSet64,
            PeakPagedMemorySize64 = this.Process.PeakPagedMemorySize64,
            PeakVirtualMemorySize64 = this.Process.PeakVirtualMemorySize64,
            PeakWorkingSet64 = this.Process.PeakWorkingSet64,
            HandleCount = this.Process.HandleCount,
            UserProcessorTime = this.Process.UserProcessorTime,
            PrivilegedProcessorTime = this.Process.PrivilegedProcessorTime,
            TotalProcessorTime = this.Process.TotalProcessorTime,
            CpuUsage = this.cpuUsage,
            BytesReceived = this.bytesReceived,
            BytesSent = this.bytesSent,
            BytesRead = this.bytesRead,
            BytesWritten = this.bytesWritten,
            BytesStored = bytesStoredCopy,
        };
    }

    public TrellMetrics? EmitMetrics(MetricsTrigger trigger) {
        if (CaptureSnapshot(trigger) is TrellMetrics metrics) {
            this.observer.OnMetricsCollected(metrics);
            foreach (var cb in this.onEmitMetricsCallbacks) {
                cb(trigger);
            }
            return metrics;
        }
        return null;
    }

    public void PollProcessMetrics() {
        using var _m = WithMetrics(MetricsTrigger.WorkerStart, MetricsTrigger.WorkerStop);

        // https://learn.microsoft.com/en-us/dotnet/core/diagnostics/available-counters

        // https://stackoverflow.com/questions/53560561/get-disk-usage-of-a-specific-process-in-c-sharp

        _ = Task.Run(TraceEventPipe);
        _ = Task.Run(TraceIO);

        do {
            if (EmitMetrics(MetricsTrigger.Interval) is TrellMetrics metrics) {
                // Display current process statistics.

                //Console.WriteLine();
                //Console.WriteLine($"{Process} -");
                //Console.WriteLine("-------------------------------------");
                //Console.WriteLine(metrics);

                //if (Process.Responding) {
                //    Console.WriteLine("Status = Running");
                //} else {
                //    Console.WriteLine("Status = Not Responding");
                //}
                //Console.WriteLine();
            }
        } while (!this.Process.WaitForExit(1000));
    }

    IDisposable WithMetrics(MetricsTrigger begin, MetricsTrigger end) {
        EmitMetrics(begin);
        return Disposable.FromAction(() => EmitMetrics(end));
    }

    public IDisposable OnEmitMetrics(Action<MetricsTrigger> callback) {
        lock (this.onEmitMetricsCallbacks) {
            this.onEmitMetricsCallbacks.Add(callback);
        }

        return Disposable.FromAction(() => {
            lock (this.onEmitMetricsCallbacks) {
                this.onEmitMetricsCallbacks.Remove(callback);
            }
        });
    }

    public void TrackDatabases(string userId, string? workerId, IReadOnlyList<string> dbs) {
        var userDataDir = Path.Join(this.Config.Storage.Path, "users", userId, "data");
        var workerDataDir = Path.Join(this.Config.Storage.Path, "users", userId, "workers", workerId, "data");

        Dictionary<string, TrellStorageMetric> metricsByName = [];
        foreach (var db in dbs) {
            var isShared = db.StartsWith("shared/");
            string dbName = db, dataDir;
            if (isShared) {
                dbName = db["shared/".Length..];
                dataDir = userDataDir;
            } else if (workerId is null) {
                continue;
            } else {
                dataDir = workerDataDir;
            }

            foreach (var f in EnumerateFiles(dataDir, $"{dbName}.db*")) {
                var m = ToMetrics(userId, isShared ? null : workerId, f);
                AccumulateMetrics(metricsByName, m);
            }
        }

        this.bytesStored.EnqueueRange(metricsByName.Values);
    }

    #region EventPipe
    // Showing well-known counters for .NET (Core) version 8.0 only. Specific processes may support additional counters.
    // System.Runtime
    //     cpu-usage                                    The percent of process' CPU usage relative to all of the system CPU resources [0-100]
    //     working-set                                  Amount of working set used by the process (MB)
    //     gc-heap-size                                 Total heap size reported by the GC (MB)
    //     gen-0-gc-count                               Number of Gen 0 GCs between update intervals
    //     gen-1-gc-count                               Number of Gen 1 GCs between update intervals
    //     gen-2-gc-count                               Number of Gen 2 GCs between update intervals
    //     time-in-gc                                   % time in GC since the last GC
    //     gen-0-size                                   Gen 0 Heap Size
    //     gen-1-size                                   Gen 1 Heap Size
    //     gen-2-size                                   Gen 2 Heap Size
    //     loh-size                                     LOH Size
    //     poh-size                                     POH (Pinned Object Heap) Size
    //     alloc-rate                                   Number of bytes allocated in the managed heap between update intervals
    //     gc-fragmentation                             GC Heap Fragmentation
    //     assembly-count                               Number of Assemblies Loaded
    //     exception-count                              Number of Exceptions / sec
    //     threadpool-thread-count                      Number of ThreadPool Threads
    //     monitor-lock-contention-count                Number of times there were contention when trying to take the monitor lock between update intervals
    //     threadpool-queue-length                      ThreadPool Work Items Queue Length
    //     threadpool-completed-items-count             ThreadPool Completed Work Items Count
    //     active-timer-count                           Number of timers that are currently active
    //     il-bytes-jitted                              Total IL bytes jitted
    //     methods-jitted-count                         Number of methods jitted
    //     gc-committed                                 Size of committed memory by the GC (MB)

    // Microsoft.AspNetCore.Hosting
    //     requests-per-second                  Number of requests between update intervals
    //     total-requests                       Total number of requests
    //     current-requests                     Current number of requests
    //     failed-requests                      Failed number of requests

    // Microsoft-AspNetCore-Server-Kestrel
    //     connections-per-second               Number of connections between update intervals
    //     total-connections                    Total Connections
    //     tls-handshakes-per-second            Number of TLS Handshakes made between update intervals
    //     total-tls-handshakes                 Total number of TLS handshakes made
    //     current-tls-handshakes               Number of currently active TLS handshakes
    //     failed-tls-handshakes                Total number of failed TLS handshakes
    //     current-connections                  Number of current connections
    //     connection-queue-length              Length of Kestrel Connection Queue
    //     request-queue-length                 Length total HTTP request queue

    // System.Net.Http
    //     requests-started                             Total Requests Started
    //     requests-started-rate                        Number of Requests Started between update intervals
    //     requests-aborted                             Total Requests Aborted
    //     requests-aborted-rate                        Number of Requests Aborted between update intervals
    //     current-requests                             Current Requests
    //     http11-connections-current-total             Current number of HTTP 1.1 connections
    //     http20-connections-current-total             Current number of HTTP 2.0 connections
    //     http30-connections-current-total             Current number of HTTP 3.0 connections
    //     http11-requests-queue-duration               Average duration of the time HTTP 1.1 requests spent in the request queue
    //     http20-requests-queue-duration               Average duration of the time HTTP 2.0 requests spent in the request queue
    //     http30-requests-queue-duration               Average duration of the time HTTP 3.0 requests spent in the request queue

    // System.Net.NameResolution
    //     dns-lookups-requested                The number of DNS lookups requested since the process started
    //     dns-lookups-duration                 Average DNS Lookup Duration
    //     current-dns-lookups                  The current number of DNS lookups that have started but not yet completed

    // System.Net.Security
    //     tls-handshake-rate                   The number of TLS handshakes completed per update interval
    //     total-tls-handshakes                 The total number of TLS handshakes completed since the process started
    //     current-tls-handshakes               The current number of TLS handshakes that have started but not yet completed
    //     failed-tls-handshakes                The total number of TLS handshakes failed since the process started
    //     all-tls-sessions-open                The number of active all TLS sessions
    //     tls10-sessions-open                  The number of active TLS 1.0 sessions
    //     tls11-sessions-open                  The number of active TLS 1.1 sessions
    //     tls12-sessions-open                  The number of active TLS 1.2 sessions
    //     tls13-sessions-open                  The number of active TLS 1.3 sessions
    //     all-tls-handshake-duration           The average duration of all TLS handshakes
    //     tls10-handshake-duration             The average duration of TLS 1.0 handshakes
    //     tls11-handshake-duration             The average duration of TLS 1.1 handshakes
    //     tls12-handshake-duration             The average duration of TLS 1.2 handshakes
    //     tls13-handshake-duration             The average duration of TLS 1.3 handshakes

    // System.Net.Sockets
    //     outgoing-connections-established             The total number of outgoing connections established since the process started
    //     incoming-connections-established             The total number of incoming connections established since the process started
    //     current-outgoing-connect-attempts            The current number of outgoing connect attempts that have started but not yet completed
    //     bytes-received                               The total number of bytes received since the process started
    //     bytes-sent                                   The total number of bytes sent since the process started
    //     datagrams-received                           The total number of datagrams received since the process started
    //     datagrams-sent                               The total number of datagrams sent since the process started

    void TraceSingle(TraceEvent e) {
        if (e.EventName != "EventCounters") {
            return;
        }

        var providerName = e.ProviderName;
        var pid = e.ProcessID;

        if (pid != this.Process.Id) {
            throw new Exception("Received event for wrong process id.");
        }

        // https://github.com/dotnet/diagnostics/blob/main/documentation/design-docs/diagnostics-client-library.md#3-trigger-a-core-dump-when-cpu-usage-goes-above-a-certain-threshold
        var values = (IDictionary<string, object>)e.PayloadValue(0);
        var fields = (IDictionary<string, object>)values["Payload"];
        // Name, Min, Max, Mean
        var name = fields["Name"].ToString();

        switch (providerName, name) {
            case ("System.Runtime", "cpu-usage"):
                this.cpuUsage = (double)fields["Mean"];
                break;
            case ("System.Net.Sockets", "bytes-received"):
                this.bytesReceived = (long)(double)fields["Max"];
                break;
            case ("System.Net.Sockets", "bytes-sent"):
                this.bytesSent = (long)(double)fields["Max"];
                break;
            default:
                return;
        }
    }

    internal void TraceEventPipe() {
        var providers = new List<EventPipeProvider>() {
            new EventPipeProvider(
                "System.Runtime",
                EventLevel.LogAlways,
                (long)ClrTraceEventParser.Keywords.None,
                new Dictionary<string, string> {
                    ["EventCounterIntervalSec"] = "1"
                }
            ),
            new EventPipeProvider(
                "System.Net.Sockets",
                EventLevel.LogAlways,
                (long)ClrTraceEventParser.Keywords.None,
                new Dictionary<string, string> {
                    ["EventCounterIntervalSec"] = "1"
                }
            ),
        };

        var client = new DiagnosticsClient(this.Process.Id);
        Log.Information("Starting diagnostics on {ProcessId}.", this.Process.Id);
        using (var session = client.StartEventPipeSession(providers)) {
            var source = new EventPipeEventSource(session.EventStream);

            source.Dynamic.All += TraceSingle;

            try {
                source.Process();
            } catch (Exception e) {
                Console.WriteLine("Error encountered while processing events");
                Console.WriteLine(e.ToString());
            }
        }
    }
    #endregion

    #region IO
    void TraceIO() {
        // TODO: Should reading all stored bytes be done when each worker starts?
        // TODO: Or when the server starts? Or never?
        //ReadAllStoredBytes();
        try {
            do {
                ReadDiskBytes();
            } while (!this.Process.WaitForExit(1000));
        } catch (Exception ex) {
            Log.Fatal("TraceIO died. {Ex}", ex);
        }
    }

    static readonly IReadOnlySet<string> DATABASE_EXTENSIONS = new HashSet<string>() { ".db", ".db-wal", ".db-shm" };

    void ReadAllStoredBytes() {
        var usersDir = Path.Join(this.Config.Storage.Path, "users");
        foreach (var userDir in EnumerateDirectories(usersDir)) {
            var userId = Path.GetFileName(userDir)!;
            var userDataDir = Path.Join(userDir, "data");
            var metricsByName = new Dictionary<string, TrellStorageMetric>();
            foreach (var userDataFile in EnumerateFiles(userDataDir)) {
                var m = ToMetrics(userId, null, userDataFile);
                AccumulateMetrics(metricsByName, m);
            }
            this.bytesStored.EnqueueRange(metricsByName.Values);

            var userWorkerDir = Path.Join(userDir, "workers");
            foreach (var workerDir in EnumerateDirectories(userWorkerDir)) {
                var workerId = Path.GetFileName(workerDir)!;
                var workerDataDir = Path.Join(workerDir, "data");
                metricsByName.Clear();
                foreach (var workerDataFile in EnumerateFiles(workerDataDir)) {
                    var m = ToMetrics(userId, workerId, workerDataFile);
                    AccumulateMetrics(metricsByName, m);
                }
                this.bytesStored.EnqueueRange(metricsByName.Values);
            }
        }
    }

    static TrellStorageMetric ToMetrics(
        string userId,
        string? workerId,
        string file
    ) {
        var fileName = Path.GetFileName(file);
        if (workerId is null) {
            fileName = "shared/" + fileName;
        }
        long length = 0;
        try {
            var info = new FileInfo(file);
            length = info.Length;
        } catch {
        }
        return new TrellStorageMetric(userId, workerId, fileName, length);
    }

    static void AccumulateMetrics(
        Dictionary<string, TrellStorageMetric> metricsByName,
        TrellStorageMetric metric
    ) {
        var extension = Path.GetExtension(metric.Name);
        var baseName = metric.Name[..^extension.Length];
        if (DATABASE_EXTENSIONS.Contains(extension)) {
            var dbName = baseName + ".db";
            if (metricsByName.TryGetValue(dbName, out var dbFile)) {
                metric = metric with {
                    Name = dbName,
                    Bytes = metric.Bytes + dbFile.Bytes
                };
            } else if (metric.Name != dbName) {
                metric = metric with { Name = dbName };
            }
            metricsByName[dbName] = metric;
        } else {
            metricsByName[metric.Name] = metric;
        }
    }

    static IEnumerable<string> EnumerateDirectories(string dir) {
        if (Directory.Exists(dir)) {
            return Directory.EnumerateDirectories(dir, "*", new EnumerationOptions {
                IgnoreInaccessible = true,
            });
        } else {
            return [];
        }
    }

    static IEnumerable<string> EnumerateFiles(string dir, string pattern = "*") {
        if (Directory.Exists(dir)) {
            return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions {
                IgnoreInaccessible = true,
            });
        } else {
            return [];
        }
    }

#if TARGET_WINDOWS
    // https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-io_counters
    struct IO_COUNTERS {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    // https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getprocessiocounters
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(IntPtr ProcessHandle, out IO_COUNTERS IoCounters);

    void ReadDiskBytes() {
        var res = GetProcessIoCounters(this.Process.Handle, out IO_COUNTERS ioc);
        //Log.Fatal("GetProcessIoCounters {res} {ioc}", res, ioc);
        this.bytesRead = (long)ioc.ReadTransferCount;
        this.bytesWritten = (long)ioc.WriteTransferCount;
    }

#elif TARGET_LINUX
    // $ cat /proc/{pid}/io
    // rchar: 810071
    // wchar: 31425
    // syscr: 910
    // syscw: 199
    // read_bytes: 11042816
    // write_bytes: 16384
    // cancelled_write_bytes: 0
    void ReadDiskBytes() {
        try {
            var lines = File.ReadAllLines($"/proc/{Process.Id}/io");
            foreach (var line in lines) {
                if (line.StartsWith("rchar:")) {
                    bytesRead = long.Parse(line.Substring("rchar:".Length));
                } else if (line.StartsWith("wchar:")) {
                    bytesWritten = long.Parse(line.Substring("wchar:".Length));
                }
            }
        } catch (Exception ex) {
            Log.Fatal("ReadDiskBytes died. {Ex}", ex);
        }
    }

#else

#endif
    #endregion
}
