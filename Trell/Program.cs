using Serilog;
using Spectre.Console.Cli;

namespace Trell;

class Program {
    static async Task<int> Main(string[] args) {
        Console.CancelKeyPress += (object? sender, ConsoleCancelEventArgs e) => {
            e.Cancel = true;
            Environment.Exit(1);
        };

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();

        var app = new CommandApp();
        app.Configure(config => {
            config.SetApplicationName("trell");
            config.AddCommand<CliCommands.InitCommand>("init")
                .WithDescription("Prepare directory for using trell, creating example config and worker.")
                .WithExample("init");

            config.AddCommand<CliCommands.ServerCommand>("serve")
                .WithDescription("Start trell as a server, accepting commands via gRPC")
                .WithExample("serve");

            config.AddCommand<CliCommands.RunCommand>("run")
                .WithDescription("Runs a given worker to completion")
                .WithExample("run", ".", "scheduled")
                .WithExample("run", "local/test.js", "scheduled")
                .WithExample("run", ".", "upload", "path/to/file.csv");

            // Used by server
            config.AddCommand<CliCommands.WorkerCommand>("worker")
                .IsHidden();
            config.PropagateExceptions();
        });

        try {
            return await app.RunAsync(args);
        } catch (CommandRuntimeException ex) {
            if (ex.Pretty is not null) {
                Spectre.Console.AnsiConsole.Write(ex.Pretty);
            } else {
                Log.Error("{m}", ex.Message);
            }
            return 0;
        } catch (Exception ex) {
            Log.Fatal("Fatal Error: {t} {ex}", ex.GetType().Name, ex);
            return 1;
        } finally {
            Log.CloseAndFlush();
        }
    }
}

