using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
            LogLevel = logLevel;
            Message = msg;
        }
    }

    record FakeKeyValuePair(char Key, int Value);

    readonly V8ScriptEngine engine;
    readonly FakeLogger logger = new();
    readonly ITestOutputHelper outputHelper;

    public BrowserApiTest(ITestOutputHelper helper) {
        outputHelper = helper;

        var rt = new RuntimeWrapper(
            new TrellExtensionContainer(logger, null, [], []),
            new RuntimeWrapper.Config()
        );
        // We use reflection to pull out EngineWrapper's V8 engine so that we're always running tests
        // using a V8 engine that's configured the same way that it will be during normal usage.
        var engineWrapper = rt.CreateScriptEngine([]);
        var fieldInfo = engineWrapper
            .GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(x => x.Name == "engine");
        if (fieldInfo is null || fieldInfo.GetValue(engineWrapper) is not V8ScriptEngine v8) {
            throw new Exception();
        }
        engine = v8;
    }

    Task<object?> Eval(string codeToRun) =>
        (engine.Evaluate(codeToRun) as IScriptObject)!.InvokeAsFunction().ToTask();

    [Fact]
    public async Task TestBlob() {
        const string BLOB_CONTENTS = "testing string";
        const string BLOB_TYPE = "fakeblobtype";

        engine.Execute($"const blob = new Blob(['{BLOB_CONTENTS}'], {{ type: '{BLOB_TYPE}' }});");
        var blob = engine.Evaluate("blob");
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
        var sizeResult = engine.Evaluate("blob.size") as int?;
        Assert.Equal(Encoding.UTF8.GetByteCount(BLOB_CONTENTS), sizeResult);

        var typeResult = engine.Evaluate("blob.type") as string;
        Assert.Equal(BLOB_TYPE, typeResult);

        const int START = 3;
        const int END = 9;

        engine.Execute($"const sliceBlob = blob.slice({START}, {END}, null);");
        var sliceBlob = engine.Evaluate("sliceBlob");
        Assert.NotNull(sliceBlob);
        var sliceBlobSize = engine.Evaluate("sliceBlob.size") as int?;
        Assert.Equal(END - START, sliceBlobSize);
        var sliceResult = await Eval("async () => sliceBlob.text();") as string;
        Assert.Equal(BLOB_CONTENTS[START..END], sliceResult);
    }

    [Fact]
    public void TestConsole() {
        engine.Execute("console.log('normal log');");
        Assert.Equal(TrellLogLevel.Info, logger.LogLevel);
        Assert.Equal("normal log", logger.Message);

        engine.Execute("console.error('error log');");
        Assert.Equal(TrellLogLevel.Error, logger.LogLevel);
        Assert.Equal("error log", logger.Message);

        engine.Execute("console.warn('warning log');");
        Assert.Equal(TrellLogLevel.Warn, logger.LogLevel);
        Assert.Equal("warning log", logger.Message);

        engine.Execute("console.status('status log');");
        Assert.Equal(TrellLogLevel.Status, logger.LogLevel);
        // We stick the status message we get into a JSON object and output its deserialized string,
        // so rather than guessing the output format, we'll just check to make sure the deserialized
        // output still contains the original message.
        Assert.True(logger.Message is not null && logger.Message.Contains("status log"));
    }

    [Fact]
    public void TestTextEncoder() {
        engine.Execute("const encoder = new TextEncoder('utf-8');");
        var encoder = engine.Evaluate("encoder");
        Assert.NotNull(encoder);
        var encoding = engine.Evaluate("encoder.encoding");
        Assert.Equal("utf-8", encoding);

        const string TEST_STRING = "testing string";

        var actual = engine.Evaluate($"encoder.encode('{TEST_STRING}')") as ITypedArray<byte>;
        Assert.NotNull(actual);
        var expected = Encoding.UTF8.GetBytes(TEST_STRING);
        Assert.Equal(expected, actual.GetBytes());

        Assert.Throws<ScriptEngineException>(() => engine.Evaluate("new TextEncoder('utf-16')"));
    }

    [Fact]
    public void TestTextDecoder() {
        engine.Execute("const decoder = new TextDecoder('utf-8');");
        var decoder = engine.Evaluate("decoder");
        Assert.NotNull(decoder);
        var encoding = engine.Evaluate("decoder.encoding");
        Assert.Equal("utf-8", encoding);

        byte[] TEST_BYTES = [0x59, 0x69, 0x70, 0x70, 0x65, 0x65,];

        var formattedBytes = TEST_BYTES.Select(x => x.ToString()).Aggregate((x, y) => $"{x}, {y}");
        engine.Execute($$"""
            const bytes = new Uint8Array([{{formattedBytes}}]);
            const arrayBuffer = bytes.buffer;
            """);
        var actual = engine.Evaluate($"decoder.decode(arrayBuffer)") as string;
        Assert.NotNull(actual);
        var expected = Encoding.UTF8.GetString(TEST_BYTES);
        Assert.Equal(expected, actual);

        Assert.Throws<ScriptEngineException>(() => engine.Evaluate("new TextDecoder('utf-16')"));
    }

    [Fact]
    public void TestHeaders() {
        // Tests passing in a value in object notation: {a: x, b: y, c: z, ...}
        FakeKeyValuePair[] testKvps = [new('a', 31), new('b', 44), new('c', 98),];
        var aggregatedKvps =
            testKvps.Select(x => $"{x.Key}: {x.Value}").Aggregate((x, y) => $"{x}, {y}");

        engine.Execute($"let headers = new Headers({{{aggregatedKvps}}});");
        var headers = engine.Evaluate("headers");
        Assert.NotNull(headers);
        foreach (var (key, value) in testKvps) {
            var result = engine.Evaluate($"headers.get('{key}')") as int?;
            Assert.Equal(value, result);
        }

        // Tests passing in a value in array notation: [['a', 'x'], ['b', 'y'], ['c', 'z'], ...]
        testKvps = [new('d', 355), new('e', 220), new('f', 18),];
        aggregatedKvps =
            testKvps.Select(x => $"['{x.Key}', {x.Value}]").Aggregate((x, y) => $"{x}, {y}");

        engine.Execute($"headers = new Headers([{aggregatedKvps}]);");
        headers = engine.Evaluate("headers");
        Assert.NotNull(headers);
        foreach (var (key, value) in testKvps) {
            var result = engine.Evaluate($"headers.get('{key}')") as int?;
            Assert.Equal(value, result);
        }

        // Tests passing in a C# dictionary
        var testDict = new Dictionary<string, int>() {
            { "abc", 200 },
            { "def", 77 },
            { "ghi", 9876 },
        };

        engine.AddHostObject("testDict", testDict);
        engine.Execute($"headers = new Headers(testDict);");
        headers = engine.Evaluate("headers");
        Assert.NotNull(headers);
        foreach (var (key, value) in testDict) {
            var result = engine.Evaluate($"headers.get('{key}')") as int?;
            Assert.Equal(value, result);
        }
    }

    [Fact]
    public void TestHeadersIgnoreNameCase() {
        engine.Execute("""
            const mixed = new Headers({ 'Content-Type': 'text/plain', 'X-Custom': 'one' });
            """);
        Assert.Equal("text/plain", engine.Evaluate("mixed.get('content-type')"));
        Assert.Equal("text/plain", engine.Evaluate("mixed.get('CONTENT-TYPE')"));
        Assert.Equal("text/plain", engine.Evaluate("mixed.get('Content-Type')"));
        Assert.Equal(true, engine.Evaluate("mixed.has('x-custom')"));
        Assert.Equal(false, engine.Evaluate("mixed.has('x-missing')"));
        Assert.Equal(
            "content-type=text/plain;x-custom=one",
            engine.Evaluate("[...mixed].map(([k, v]) => `${k}=${v}`).join(';')")
        );
        Assert.Equal(
            "content-type=text/plain;x-custom=one",
            engine.Evaluate("[...mixed.entries()].map(([k, v]) => `${k}=${v}`).join(';')")
        );
        Assert.Equal("content-type,x-custom", engine.Evaluate("[...mixed.keys()].join(',')"));
        Assert.Equal("text/plain,one", engine.Evaluate("[...mixed.values()].join(',')"));
        Assert.Equal(
            "content-type,x-custom",
            engine.Evaluate("""
                (() => {
                    const ks = [];
                    mixed.forEach((v, k) => ks.push(k));
                    return ks.join(',');
                })()
                """)
        );

        // The same name in different case keeps both values.
        engine.Execute("const dup = new Headers([['Accept', 'a'], ['accept', 'b']]);");
        Assert.Equal("a, b", engine.Evaluate("dup.get('ACCEPT')"));

        engine.Execute("const empty = new Headers();");
        Assert.Equal(false, engine.Evaluate("empty.has('a')"));
    }

    [Fact]
    public async Task TestFetch() {
        if (!HttpListener.IsSupported) {
            outputHelper.WriteLine(
                "HttpListener is unsupported on this platform, running backup test instead..."
            );
            await BackupTestFetch();
            return;
        }

        const string FAKE_URL = "http://localhost/this.does.not.exist/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(FAKE_URL);
        try {
            listener.Start();
        } catch (HttpListenerException) {
            outputHelper.WriteLine(
                "Permissions denied for using HttpListener, running backup test instead..."
            );
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
        var responseMsg =
            await (response["text"] as IScriptObject)!.InvokeAsFunction().ToTask() as string;
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
        responseMsg =
            await (response["text"] as IScriptObject)!.InvokeAsFunction().ToTask() as string;
        Assert.Equal("TEST 2", responseMsg);

        listener.Stop();
    }

    [Fact]
    public async Task TestFetchReturnsContentHeaders() {
        // Windows lets a non-admin user listen on localhost only on ports other than 80.
        var portFinder = new TcpListener(IPAddress.Loopback, 0);
        portFinder.Start();
        var port = ((IPEndPoint)portFinder.LocalEndpoint).Port;
        portFinder.Stop();
        var url = $"http://localhost:{port}/trell.fetch.content.headers/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(url);
        listener.Start();

        var responseTask = Eval($$"""
            async () => {
                const res = await fetch('{{url}}', null);
                const blob = await res.blob();
                return {
                    contentType: res.headers.get('Content-Type'),
                    contentLength: res.headers.get('Content-Length'),
                    custom: res.headers.get('X-Trell-Test'),
                    lowerContentType: res.headers.get('content-type'),
                    lowerCustom: res.headers.get('x-trell-test'),
                    blobType: blob.type,
                };
            }
            """);

        var ctx = await listener.GetContextAsync();
        ctx.Response.StatusCode = (int)HttpStatusCode.OK;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers.Add("X-Trell-Test", "yes");
        var bytes = Encoding.UTF8.GetBytes("{}");
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();

        var result = await responseTask as IScriptObject;
        Assert.NotNull(result);
        Assert.Equal("application/json; charset=utf-8", result["contentType"]);
        Assert.Equal("2", result["contentLength"]);
        Assert.Equal("yes", result["custom"]);
        Assert.Equal("application/json; charset=utf-8", result["lowerContentType"]);
        Assert.Equal("yes", result["lowerCustom"]);
        Assert.Equal("application/json; charset=utf-8", result["blobType"]);

        listener.Stop();
    }

    async Task BackupTestFetch() {
        var backupResponse = await Eval(
                $"async () => await fetch('https://www.xledger.net/version.htm', null);"
            ) as IScriptObject;
        Assert.NotNull(backupResponse);
        var backupResponseCode = backupResponse["status"] as int?;
        Assert.Equal((int)HttpStatusCode.OK, backupResponseCode);
        var backupResponseMsg =
            await (backupResponse["text"] as IScriptObject)!.InvokeAsFunction().ToTask() as string;
        Assert.False(string.IsNullOrWhiteSpace(backupResponseMsg));
    }

    [Fact]
    public void TestXmlParsingAndEditingWorks() {
        const string VALID_XML_TABLE = "<table><tbody><tr><td>something</td></tr></tbody></table>";
        const string EDITED_XML_TABLE =
            "<table><tbody><tr><td>something<span>some new text</span></td></tr></tbody></table>";

        engine.Execute("const dp = new DOMParser();");
        var dp = engine.Evaluate("dp");
        Assert.NotNull(dp);
        engine.Execute($"const xmlDoc = dp.parseFromString('{VALID_XML_TABLE}', 'text/xml');");
        var xmlDoc = engine.Evaluate("xmlDoc") as IScriptObject;
        Assert.NotNull(xmlDoc);
        var type = xmlDoc["type"] as string;
        Assert.Equal("xml", type);

        engine.Execute(
            "const newSpan = xmlDoc.createElement('span'); newSpan.textContent = 'some new text';"
        );
        var newSpan = engine.Evaluate("newSpan") as IScriptObject;
        Assert.NotNull(newSpan);
        var text = newSpan["textContent"] as string;
        Assert.Equal("some new text", text);

        engine.Execute("const cell = xmlDoc.getElementsByTagName('td')[0];");
        var cell = engine.Evaluate("cell") as IScriptObject;
        Assert.NotNull(cell);
        Assert.Equal("td", cell["nodeName"]);
        engine.Execute("cell.appendChild(newSpan);");
        var child = cell["lastChild"] as IScriptObject;
        Assert.NotNull(child);
        Assert.Equal("span", child["nodeName"]);

        engine.Execute("const xs = new XMLSerializer();");
        var serializer = engine.Evaluate("xs");
        Assert.NotNull(serializer);
        var output = engine.Evaluate("xs.serializeToString(xmlDoc)") as string;
        Assert.Equal(EDITED_XML_TABLE, output);
    }
}
