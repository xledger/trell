using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
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
using Trell.Engine.RuntimeApis.BrowserApiDotNetObjects;

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
    record FakeKeyValuePair(char Key, int Value);

    readonly V8ScriptEngine engine;
    readonly FakeLogger logger = new();
    readonly ITestOutputHelper outputHelper;

    public BrowserApiTest(ITestOutputHelper helper) {
        this.outputHelper = helper;

        var rt = new RuntimeWrapper(
            new TrellExtensionContainer(this.logger, null, [], []),
            new RuntimeWrapper.Config()
        );
        // We use reflection to pull out EngineWrapper's V8 engine so that we're always running tests
        // using a V8 engine that's configured the same way that it will be during normal usage.
        var engineWrapper = rt.CreateScriptEngine([]);
        var fieldInfo = engineWrapper.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(x => x.Name == "engine");
        if (fieldInfo is null || fieldInfo.GetValue(engineWrapper) is not V8ScriptEngine v8) {
            throw new Exception();
        }
        this.engine = v8;
    }

    Task<object?> Eval(string codeToRun) => (this.engine.Evaluate(codeToRun) as IScriptObject)!.InvokeAsFunction().ToTask();

    [Fact]
    public async Task TestBlob() {
        const string BLOB_CONTENTS = "testing string";
        const string BLOB_TYPE = "fakeblobtype";

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

        // Blob handles all strings as UTF-8, including when calculating size
        var sizeResult = this.engine.Evaluate("blob.size") as int?;
        Assert.Equal(Encoding.UTF8.GetByteCount(BLOB_CONTENTS), sizeResult);

        var typeResult = this.engine.Evaluate("blob.type") as string;
        Assert.Equal(BLOB_TYPE, typeResult);

        const int START = 3;
        const int END = 9;

        this.engine.Execute($"const sliceBlob = blob.slice({START}, {END}, null);");
        var sliceBlob = this.engine.Evaluate("sliceBlob");
        Assert.NotNull(sliceBlob);
        var sliceBlobSize = this.engine.Evaluate("sliceBlob.size") as int?;
        Assert.Equal(END - START, sliceBlobSize);
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

    [Fact]
    public void TestHeaders() {
        // Tests passing in a value in object notation: {a: x, b: y, c: z, ...}
        FakeKeyValuePair[] testKvps = [ new('a', 31), new('b', 44), new('c', 98) ];
        var aggregatedKvps = testKvps.Select(x => $"{x.Key}: {x.Value}").Aggregate((x, y) => $"{x}, {y}");

        this.engine.Execute($"let headers = new Headers({{{aggregatedKvps}}});");
        var headers = this.engine.Evaluate("headers");
        Assert.NotNull(headers);
        foreach (var (key, value) in testKvps) {
            var result = this.engine.Evaluate($"headers.get('{key}')") as int?;
            Assert.Equal(value, result);
        }

        // Tests passing in a value in array notation: [['a', 'x'], ['b', 'y'], ['c', 'z'], ...]
        testKvps = [new('d', 355), new('e', 220), new('f', 18)];
        aggregatedKvps = testKvps.Select(x => $"['{x.Key}', {x.Value}]").Aggregate((x, y) => $"{x}, {y}");

        this.engine.Execute($"headers = new Headers([{aggregatedKvps}]);");
        headers = this.engine.Evaluate("headers");
        Assert.NotNull(headers);
        foreach (var (key, value) in testKvps) {
            var result = this.engine.Evaluate($"headers.get('{key}')") as int?;
            Assert.Equal(value, result);
        }

        // Tests passing in a C# dictionary
        var testDict = new Dictionary<string, int>() { { "abc", 200 }, { "def", 77 }, { "ghi", 9876 } };

        this.engine.AddHostObject("testDict", testDict);
        this.engine.Execute($"headers = new Headers(testDict);");
        headers = this.engine.Evaluate("headers");
        Assert.NotNull(headers);
        foreach (var (key, value) in testDict) {
            var result = this.engine.Evaluate($"headers.get('{key}')") as int?;
            Assert.Equal(value, result);
        }
    }

    [Fact]
    public async Task TestFetch() {
        if (!HttpListener.IsSupported) {
            this.outputHelper.WriteLine("HttpListener is unsupported on this platform, running backup test instead...");
            await BackupTestFetch();
            return;
        }

        const string FAKE_URL = "http://localhost/this.does.not.exist/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(FAKE_URL);
        try {
            listener.Start();
        } catch(HttpListenerException) {
            this.outputHelper.WriteLine("Permissions denied for using HttpListener, running backup test instead...");
            await BackupTestFetch();
            return;
        }

        var responseTask = Eval($"async () => await fetch('{FAKE_URL}', null);");

        var ctx = await listener.GetContextAsync();
        ctx.Response.StatusCode = (int)HttpStatusCode.OK;
        var stream = ctx.Response.OutputStream;
        var bytes = Encoding.UTF8.GetBytes("TEST");
        stream.Write(bytes, 0, bytes.Length);
        stream.Close();

        var response = await responseTask as IScriptObject;
        Assert.NotNull(response);
        var responseCode = response["status"] as int?;
        Assert.Equal((int)HttpStatusCode.OK, responseCode);
        var responseMsg = await (response["text"] as IScriptObject)!.InvokeAsFunction().ToTask() as string;
        Assert.Equal("TEST", responseMsg);

        responseTask = Eval($"async () => await fetch('{FAKE_URL}', null);");

        ctx = await listener.GetContextAsync();
        ctx.Response.StatusCode = (int)HttpStatusCode.Forbidden;
        stream = ctx.Response.OutputStream;
        bytes = Encoding.UTF8.GetBytes("TEST 2");
        stream.Write(bytes, 0, bytes.Length);
        stream.Close();

        response = await responseTask as IScriptObject;
        Assert.NotNull(response);
        responseCode = response["status"] as int?;
        Assert.Equal((int)HttpStatusCode.Forbidden, responseCode);
        responseMsg = await (response["text"] as IScriptObject)!.InvokeAsFunction().ToTask() as string;
        Assert.Equal("TEST 2", responseMsg);

        listener.Stop();
    }

    async Task BackupTestFetch() {
        var backupResponse = await Eval($"async () => await fetch('https://www.xledger.net/version.htm', null);") as IScriptObject;
        Assert.NotNull(backupResponse);
        var backupResponseCode = backupResponse["status"] as int?;
        Assert.Equal((int)HttpStatusCode.OK, backupResponseCode);
        var backupResponseMsg = await (backupResponse["text"] as IScriptObject)!.InvokeAsFunction().ToTask() as string;
        Assert.False(string.IsNullOrWhiteSpace(backupResponseMsg));
    }

    [Fact]
    public void TestXmlParsingAndEditingWorks() {
        const string VALID_XML_TABLE = "<table><tbody><tr><td>something</td></tr></tbody></table>";
        const string EDITED_XML_TABLE = "<table><tbody><tr><td>something<span>some new text</span></td></tr></tbody></table>";

        this.engine.Execute("const dp = new DOMParser();");
        var dp = this.engine.Evaluate("dp");
        Assert.NotNull(dp);
        this.engine.Execute($"const xmlDoc = dp.parseFromString('{VALID_XML_TABLE}', 'text/xml');");
        var xmlDoc = this.engine.Evaluate("xmlDoc") as IScriptObject;
        Assert.NotNull(xmlDoc);
        var type = xmlDoc["type"] as string;
        Assert.Equal("xml", type);

        this.engine.Execute("const newSpan = xmlDoc.createElement('span'); newSpan.textContent = 'some new text';");
        var newSpan = this.engine.Evaluate("newSpan") as IScriptObject;
        Assert.NotNull(newSpan);
        var text = newSpan["textContent"] as string;
        Assert.Equal("some new text", text);

        this.engine.Execute("const cell = xmlDoc.getElementsByTagName('td')[0];");
        var cell = this.engine.Evaluate("cell") as IScriptObject;
        Assert.NotNull(cell);
        Assert.Equal("td", cell["nodeName"]);
        this.engine.Execute("cell.appendChild(newSpan);");
        var child = cell["lastChild"] as IScriptObject;
        Assert.NotNull(child);
        Assert.Equal("span", child["nodeName"]);

        this.engine.Execute("const xs = new XMLSerializer();");
        var serializer = this.engine.Evaluate("xs");
        Assert.NotNull(serializer);
        var output = this.engine.Evaluate("xs.serializeToString(xmlDoc)") as string;
        Assert.Equal(EDITED_XML_TABLE, output);
    }
}
