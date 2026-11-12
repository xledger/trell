using Trell.CliCommands;

namespace Trell.Test.CliCommands;

public class RunCommandTest : IDisposable {
    readonly string dir;

    public RunCommandTest() {
        dir = Path.Combine(Path.GetTempPath(), $"trell-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
    }

    public void Dispose() {
        Directory.Delete(dir, true);
    }

    RunCommandSettings Settings(string fileName, params string[] headers) {
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, "{}");
        var settings = new RunCommandSettings {
            HandlerFn = "request",
            DataFile = path,
            Headers = headers,
        };
        Assert.True(settings.Validate().Successful);
        return settings;
    }

    [Fact]
    public void RequestSendsFileMimeTypeAsContentType() {
        var request = RunCommand.GenerateRequest(Settings("body.json"));
        Assert.Equal("application/json", request.Headers["Content-Type"]);
        Assert.False(request.Headers.ContainsKey("Content-Encoding"));
    }

    [Fact]
    public void RequestKeepsContentTypeGivenOnCommandLine() {
        var request = RunCommand.GenerateRequest(Settings("body.json", "content-type: text/plain"));
        Assert.Equal("text/plain", request.Headers["content-type"]);
        Assert.False(request.Headers.ContainsKey("Content-Type"));
    }
}
