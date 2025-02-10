using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Hosting;
using System.Diagnostics;
using Trell.Engine.ClearScriptWrappers;
using Trell.Engine.Extensibility;
using Trell.Extensions;
using Trell.IPC.Server;
using Trell.IPC.Worker;

namespace Trell;

class App(
    TrellProcessInfo processInfo,
    WebApplication app,
    TrellConfig config,
    TrellExtensionContainer extensionContainer
) : IDisposable {
    bool disposedComplete;

    internal static void BootstrapLogger(LogEventLevel? level) {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level ?? LogEventLevel.Information)
            .WriteTo.Console()
            .CreateBootstrapLogger();
    }

    internal static App InitServer(TrellConfig config, string[] args) {
        var processInfo = new TrellProcessInfo(Environment.ProcessId, TrellProcessKind.Server);
        var socket = config.Socket;
        var builder = InitBuilder(processInfo, config, args);

        var extensionContainer = TrellSetup.ExtensionContainer(config);
        var server = new TrellServer(config, extensionContainer);

        builder.Services.AddSingleton(server);
        builder.Services.AddGrpc();
        builder.WebHost.ConfigureKestrel(serverOptions => {
            serverOptions.ListenAnyIP(3000, listenOptions => {
                listenOptions.Protocols = HttpProtocols.Http2;
            });
            serverOptions.ListenUnixSocket(socket, listenOptions => {
                listenOptions.Protocols = HttpProtocols.Http2;
            });
        });

        extensionContainer.Observer.OnStartup(processInfo);

        if (File.Exists(socket)) {
            Log.Warning("Unix domain socket {Socket} already exists. Deleting.", socket);
            File.Delete(socket);
        }

        var app = builder.Build();

        app.MapGrpcService<TrellServer>();
        app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

        return new App(processInfo, app, config, extensionContainer);
    }

    internal static App InitWorker(TrellConfig config, string[] args) {
        var processInfo = new TrellProcessInfo(Environment.ProcessId, TrellProcessKind.Worker);
        var socket = config.Worker.Socket;
        var builder = InitBuilder(processInfo, config, args);

        var extensionContainer = TrellSetup.ExtensionContainer(config);
        var rt = new RuntimeWrapper(extensionContainer, config.ToRuntimeConfig());
        var worker = new TrellWorkerImpl(config, extensionContainer, rt);

        builder.Services.AddSingleton(worker);
        builder.Services.AddGrpc();
        builder.WebHost.ConfigureKestrel(serverOptions => {
            serverOptions.ListenUnixSocket(socket, listenOptions => {
                listenOptions.Protocols = HttpProtocols.Http2;
            });
        });

        if (File.Exists(socket)) {
            Log.Information("Unix domain socket {0} already exists. Deleting.", socket);
            try {
                File.Delete(socket);
            } catch (Exception ex) {
                Log.Fatal("Shutting down. Could not delete old unix domain socket. {ex}", ex);
                throw;
            }
        }

        extensionContainer.Observer.OnStartup(processInfo);

        var app = builder.Build();

        app.MapGrpcService<TrellWorkerImpl>();

        return new App(processInfo, app, config, extensionContainer);
    }

    static WebApplicationBuilder InitBuilder(TrellProcessInfo processInfo, TrellConfig config, string[] args) {
        switch (processInfo.ProcessKind) {
            case TrellProcessKind.Server:
                Log.Debug("Server config (process {ProcessId}) {Config}",
                    Environment.ProcessId, config);
                Log.Information("Starting Server (process {ProcessId}) on socket: \"{p}\"",
                    Environment.ProcessId, config.Socket);
                break;
            case TrellProcessKind.Worker:
                Log.Debug("Worker config (process {ProcessId}) {Config}",
                    Environment.ProcessId, config);
                Log.Information("Starting Worker {WorkerId} (process {ProcessId}) on socket: \"{p}\"",
                    config.Worker.Id, Environment.ProcessId, config.Worker.Socket);
                break;
            default:
                throw new UnreachableException($"Unknown {nameof(TrellProcessKind)}");
        }

        // https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess-uds?view=aspnetcore-7.0
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddTrellConfig(config);

        if (config.HasSerilogSettings) {
            // Configure Serilog if there are any keys underneath Serilog.
            var logConfiguration = new LoggerConfiguration()
                .ReadFrom.Configuration(builder.Configuration)
                .Enrich.WithProcessId()
                .Enrich.FromLogContext();
            Log.Logger = logConfiguration.CreateLogger();
        } else {
            // Freeze bootstrapped logger if no additional Serilog configuration was provided.
            ((ReloadableLogger)Log.Logger).Freeze();
        }
        builder.Host.UseSerilog(Log.Logger);

        return builder;
    }

    public async Task RunAsync() {
        try {
            switch (processInfo.ProcessKind) {
                case TrellProcessKind.Server:
                    await app.RunAsync();
                    break;
                case TrellProcessKind.Worker:
                    await app.StartAsync(); {
                        using var serverCh = Utility.CreateUnixDomainSocketChannel(config.Socket);
                        var client = new Rpc.TrellServer.TrellServerClient(serverCh);
                        await client.NotifyWorkerReadyAsync(new Rpc.WorkerReady { WorkerId = config.Worker.Id });
                    }

                    while (Console.ReadLine() is not null) { }
                    break;
                default:
                    throw new UnreachableException($"Unknown {nameof(TrellProcessKind)}");
            }
        } finally {
            extensionContainer.Observer.OnShutdown(processInfo);
        }
    }

    public Task StartAsync(CancellationToken tok = default) => app.StartAsync(tok);

    public async Task StopAsync(CancellationToken tok = default) {
        try {
            await app.StopAsync(tok);
        } finally {
            extensionContainer.Observer.OnShutdown(processInfo);
        }
    }

    protected virtual void Dispose(bool disposing) {
        if (!this.disposedComplete) {
            if (disposing) {
                (app as IDisposable)?.Dispose();
            }

            app = null!;
            extensionContainer = null!;
            this.disposedComplete = true;
        }
    }

    public void Dispose() {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

static class TrellConfigurationBuilderExtensions {
    public static IConfigurationBuilder AddTrellConfig(this IConfigurationBuilder builder, TrellConfig config) {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(new TrellConfigurationSource(config));
        return builder;
    }
}

class TrellConfigurationSource(TrellConfig c) : IConfigurationSource {
    public IConfigurationProvider Build(IConfigurationBuilder builder) => c;
}
