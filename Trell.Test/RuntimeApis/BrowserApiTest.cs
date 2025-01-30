using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Text;
using System.Threading.Tasks;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;
using Trell.Engine;
using Trell.Engine.ClearScriptWrappers;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;

namespace Trell.Test.RuntimeApis;
public class BrowserApiTest {
    class FakeLogger : ITrellLogger {
        public TrellLogLevel? LogLevel { get; private set; }
        public string? Message { get; private set; }

        public void Log(TrellExecutionContext ctx, TrellLogLevel logLevel, string msg) {
            this.LogLevel = logLevel;
            this.Message = msg;
        }
    }

    readonly V8ScriptEngine engine;
    readonly FakeLogger logger = new();

    public BrowserApiTest() {
        var rt = new RuntimeWrapper(
            new TrellExtensionContainer(this.logger, null, [], []),
            new RuntimeWrapper.Config()
        );
        var engineWrapper = rt.CreateScriptEngine([]);
        var fieldInfo = engineWrapper.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(x => x.Name == "engine");
        if (fieldInfo is null || fieldInfo.GetValue(engineWrapper) is not V8ScriptEngine v8) {
            throw new Exception();
        }
        this.engine = v8;
    }

    [Fact]
    public async Task TestBlob() {
        const string BLOB_CONTENTS = "testing string";
        const string BLOB_TYPE = "fakeblobtype";

        Task<object?> Eval(string codeToRun) => (this.engine.Evaluate(codeToRun) as IScriptObject)!.InvokeAsFunction().ToTask();

        this.engine.Execute($"const blob = new Blob(['{BLOB_CONTENTS}'], {{ type: '{BLOB_TYPE}' }});");
        var blob = this.engine.Evaluate("blob");
        Assert.NotNull(blob);

        var arrayBufferResult = await Eval("async () => await blob.arrayBuffer();") as IArrayBuffer;
        Assert.NotNull(arrayBufferResult);
        Assert.Equal((ulong)BLOB_CONTENTS.Length, arrayBufferResult.Size);

        var bytesResult = await Eval("async () => await blob.bytes();") as ITypedArray<byte>;
        Assert.NotNull(bytesResult);
        Assert.Equal(Encoding.UTF8.GetBytes(BLOB_CONTENTS), bytesResult.GetBytes());

        var textResult = await Eval("async () => await blob.text();") as string;
        Assert.Equal(BLOB_CONTENTS, textResult);

        var sizeResult = await Eval("async () => await blob.size;") as int?;
        Assert.Equal(BLOB_CONTENTS.Length, sizeResult);

        var typeResult = this.engine.Evaluate("blob.type") as string;
        Assert.Equal(BLOB_TYPE, typeResult);

        const int START = 3;
        const int END = 9;

        this.engine.Execute($"const sliceBlob = blob.slice({START}, {END}, null);");
        var sliceBlob = this.engine.Evaluate("sliceBlob");
        Assert.NotNull(sliceBlob);
        var sliceResult = await Eval("async () => sliceBlob.text();") as string;
        Assert.Equal(BLOB_CONTENTS[START..END], sliceResult);
    }

    [Fact]
    public void TestConsole() {
        this.engine.Execute("console.log('normal log');");
        Assert.Equal(TrellLogLevel.Info, this.logger.LogLevel);
        Assert.Equal("normal log", this.logger.Message);

        this.engine.Execute("console.error('error log');");
        Assert.Equal(TrellLogLevel.Error, this.logger.LogLevel);
        Assert.Equal("error log", this.logger.Message);

        this.engine.Execute("console.warn('warning log');");
        Assert.Equal(TrellLogLevel.Warn, this.logger.LogLevel);
        Assert.Equal("warning log", this.logger.Message);

        this.engine.Execute("console.status('status log');");
        Assert.Equal(TrellLogLevel.Status, this.logger.LogLevel);
        // We stick the status message we get into a JSON object and output its deserialized string,
        // so rather than guessing the output format, we'll just check to make sure the deserialized
        // output still contains the original message.
        Assert.True(this.logger.Message is not null && this.logger.Message.Contains("status log"));
    }

    [Fact]
    public void TestTextEncoder() {
        this.engine.Execute("const encoder = new TextEncoder('utf-8');");
        var encoder = this.engine.Evaluate("encoder");
        Assert.NotNull(encoder);
        var encoding = this.engine.Evaluate("encoder.encoding");
        Assert.Equal("utf-8", encoding);

        const string TEST_STRING = "testing string";

        var actual = this.engine.Evaluate($"encoder.encode('{TEST_STRING}')") as ITypedArray<byte>;
        Assert.NotNull(actual);
        var expected = Encoding.UTF8.GetBytes(TEST_STRING);
        Assert.Equal(expected, actual.GetBytes());

        Assert.Throws<ScriptEngineException>(() => this.engine.Evaluate("new TextEncoder('utf-16')"));
    }

    [Fact]
    public void TestTextDecoder() {
        this.engine.Execute("const decoder = new TextDecoder('utf-8');");
        var decoder = this.engine.Evaluate("decoder");
        Assert.NotNull(decoder);
        var encoding = this.engine.Evaluate("decoder.encoding");
        Assert.Equal("utf-8", encoding);

        byte[] TEST_BYTES = [ 0x59, 0x69, 0x70, 0x70, 0x65, 0x65 ];

        var formattedBytes = TEST_BYTES.Select(x => x.ToString()).Aggregate((x, y) => $"{x}, {y}");
        this.engine.Execute($$"""
            const bytes = new Uint8Array([{{formattedBytes}}]);
            const arrayBuffer = bytes.buffer;
            """);
        var actual = this.engine.Evaluate($"decoder.decode(arrayBuffer)") as string;
        Assert.NotNull(actual);
        var expected = Encoding.UTF8.GetString(TEST_BYTES);
        Assert.Equal(expected, actual);

        Assert.Throws<ScriptEngineException>(() => this.engine.Evaluate("new TextDecoder('utf-16')"));
    }
}
