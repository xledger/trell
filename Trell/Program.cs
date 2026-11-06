using Serilog;
using Spectre.Console.Cli;

namespace Trell;

class Program {
    static async Task<int> Main(string[] args) {
        Console.CancelKeyPress += (object? sender, ConsoleCancelEventArgs e) => {
            e.Cancel = true;
            Environment.Exit(1);
        };

        Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
        try {
            return await RunAsync(args);
        } finally {
            Log.CloseAndFlush();
        }
    }

    // Returns the process exit code: 0 when the command succeeds, 1 when it fails.
    internal static async Task<int> RunAsync(string[] args) {
        var app = new CommandApp();
        app.Configure(config => {
            config.SetApplicationName("trell");
            config
                .AddCommand<CliCommands.InitCommand>("init")
                .WithDescription("Initialize directory with a new worker to get started with trell faster")
                .WithExample("init");

            config
                .AddCommand<CliCommands.ServerCommand>("serve")
                .WithDescription("Start trell as a server, accepting commands via gRPC")
                .WithExample("serve");

            config
                .AddCommand<CliCommands.RunCommand>("run")
                .WithDescription("Run a handler on the worker")
                .WithExample("run", "cron")
                .WithExample("run", "upload", "example.csv");

            config
                .AddCommand<CliCommands.RunFileCommand>("run-file")
                .WithDescription("Run a user-defined handler from a specified file")
                .WithExample("run-file", "myFile.js", "myFunction")
                .WithExample(
                    "run-file",
                    "myFile.js",
                    "myFunction",
                    "optionalArg1",
                    "optionalArg2",
                    "..."
                );

            // Used by server
            config.AddCommand<CliCommands.WorkerCommand>("worker").IsHidden();
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
            return 1;
        } catch (Exception ex) {
            Log.Fatal("Fatal Error: {t} {ex}", ex.GetType().Name, ex);
            return 1;
        }
    }
}
