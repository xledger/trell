using Trell.CliCommands;

namespace Trell.Test.CliCommands;

public class ExitCodeTest {
    [Fact]
    public async Task SettingsValidationErrorExitsNonZero() {
        var missing = Path.Combine(Path.GetTempPath(), $"trell-missing-{Guid.NewGuid():N}.csv");
        Assert.Equal(1, await Program.RunAsync(["run", "upload", missing,]));
    }

    [Fact]
    public async Task UnknownCommandExitsNonZero() {
        Assert.Equal(1, await Program.RunAsync(["no-such-command"]));
    }

    [Fact]
    public void SuccessfulWorkResultExitsZero() {
        var result = new Rpc.WorkResult { Code = Rpc.ResultCode.Success };
        Assert.Equal(0, RunCommand.ExitCode(result));
    }

    [Theory]
    [InlineData(Rpc.ResultCode.Unknown)]
    [InlineData(Rpc.ResultCode.UserException)]
    [InlineData(Rpc.ResultCode.Timeout)]
    [InlineData(Rpc.ResultCode.InvalidPath)]
    [InlineData(Rpc.ResultCode.PermissionError)]
    [InlineData(Rpc.ResultCode.InvalidRequest)]
    [InlineData(Rpc.ResultCode.EntryPointNotDefined)]
    [InlineData(Rpc.ResultCode.UnauthorizedDatabaseAccess)]
    public void FailedWorkResultExitsNonZero(Rpc.ResultCode code) {
        Assert.Equal(1, RunCommand.ExitCode(new Rpc.WorkResult { Code = code }));
    }
}
