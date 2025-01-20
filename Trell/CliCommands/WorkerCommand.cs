using Serilog.Events;
using Spectre.Console.Cli;

namespace Trell.CliCommands;

public class WorkerCommand : AsyncCommand<WorkerCommand.Settings> {
    public sealed class Settings : CommandSettings {
        /// <summary>
        /// Path to Trell TOML config
        /// </summary>
        [CommandOption("--config")]
        public string? Config { get; set; }

        [CommandOption("--log-level")]
        public LogEventLevel? LogLevel { get; set; } = null;

        [CommandArgument(0, "<worker-id>")]
        public required int WorkerId { get; set; }

        [CommandArgument(1, "<socket>")]
        public required string Socket { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings) {
        settings.Validate();
        App.BootstrapLogger(settings.LogLevel);
        var config = TrellConfig.LoadToml(settings.Config ?? "Trell.toml");
        config.Worker.Id = settings.WorkerId;
        config.Worker.Socket = settings.Socket;
        var args = context.Remaining.Raw.ToArray();
        using var app = App.InitWorker(config, args);
        await app.RunAsync();
        return 0;
    }
}
