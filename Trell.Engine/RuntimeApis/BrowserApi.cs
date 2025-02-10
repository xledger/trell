using Microsoft.ClearScript.JavaScript;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Trell.Engine.ClearScriptWrappers;
using Trell.Engine.Extensibility.Interfaces;
using Trell.Engine.Utility.Concurrent;
using static Trell.Engine.ClearScriptHelpers.ScriptingInterop;

namespace Trell.Engine.RuntimeApis {
    public class BrowserApi : IPlugin {
        public BrowserApi(ITrellLogger logger) {
            this.DotNetObject = new PluginDotNetObject(
                (ctx, eng) => new BrowserApiDotNetObjects.BrowserApi(logger, ctx, eng),
                "dotNetBrowserApi");
        }

        public PluginDotNetObject DotNetObject { get; }

        public string JsScript => CachedJsScript.Value;
        static readonly Lazy<string> CachedJsScript = new(() =>
            new StreamReader(
                Assembly.GetExecutingAssembly().GetManifestResourceStream("Trell.Engine.RuntimeApis.ExposeBrowserApi.js")!
            ).ReadToEnd()
        );

        public IReadOnlyList<string> TopLevelJsNamesExposed { get; } = new[] {
            "fetch",
            "Headers",
            "console",
            "Blob",
            "TextEncoder",
            "TextDecoder",
        };
    }

    namespace BrowserApiDotNetObjects {
        public class BrowserApi {
            static readonly HttpClientHandler HttpClientHandler;

            static BrowserApi() {
                HttpClientHandler = new HttpClientHandler {
                    UseCookies = false,
                    AllowAutoRedirect = false
                };
            }

            ITrellLogger Logger { get; }
            IAtomRead<TrellExecutionContext> Context { get; }

            readonly EngineWrapper engine;

            public BrowserApi(ITrellLogger logger, IAtomRead<TrellExecutionContext> context, EngineWrapper engine) {
                this.Logger = logger;
                this.Context = context;
                this.engine = engine;

                var dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                engine.LoadJsLibrary(Path.GetFullPath("RuntimeApis/Vendor/xmldom/index.js", dllDir!), ModuleCategory.CommonJS);
            }

            static T? GetProperty<T>(dynamic obj, string propName, T? defaultValue = default) {
                if (IsNullLike(obj)) {
                    return defaultValue;
                }
                var v = obj[propName];
                return IsNullLike(v)
                    ? defaultValue
                    : (T)obj[propName];
            }

            public async Task<object> Fetch(string url, dynamic options) {
                var request = new HttpRequestMessage {
                    Method = new HttpMethod(GetProperty(options, "method", "GET")),
                    RequestUri = new Uri(url)
                };

                // The default media type for StringContent is text/plain. https://learn.microsoft.com/en-us/dotnet/api/system.net.http.stringcontent.-ctor?view=net-8.0#system-net-http-stringcontent-ctor(system-string-system-text-encoding)
                var contentType = "text/plain";

                if (!IsNullLike(options)
                    && !IsNullLike(options.headers)) {
                    foreach (var header in options.headers) {
                        if (header.Key.ToString() == "Content-Type") {
                            contentType = header.Value.ToString();
                        } else {
                            request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToString());
                        }
                    }
                }

                object body = GetProperty<object>(options, "body", null);
                if (body != null) {
                    if (body is string bodyStr) {
                        request.Content = new StringContent(bodyStr, Encoding.UTF8, contentType);
                    } else {
                        throw new ApplicationException($"options.body type {body.GetType().Name} not allowed");
                    }
                }

                // method: "POST", // *GET, POST, PUT, DELETE, etc.
                // mode: "cors", // no-cors, *cors, same-origin
                // cache: "no-cache", // *default, no-cache, reload, force-cache, only-if-cached
                // credentials: "same-origin", // include, *same-origin, omit
                // headers: {
                //   "Content-Type": "application/json",
                //   // 'Content-Type': 'application/x-www-form-urlencoded',
                // },
                // redirect: "follow", // manual, *follow, error
                // referrerPolicy: "no-referrer", // no-referrer, *no-referrer-when-downgrade, origin, origin-when-cross-origin, same-origin, strict-origin, strict-origin-when-cross-origin, unsafe-url
                // body: JSON.stringify(data), // body data type must match "Content-Type" header

                using var httpClient = new HttpClient(HttpClientHandler, false);
                var response = await httpClient.SendAsync(request);
                var headers = new Dictionary<string, string>();

                foreach (var header in response.Headers) {
                    headers[header.Key] = string.Join(", ", header.Value);
                }

                Func<Task<IArrayBuffer>> contentFn = async () => this.engine.CreateJsBuffer(await response.Content.ReadAsByteArrayAsync());

                return new {
                    ok = response.IsSuccessStatusCode,
                    status = (int)response.StatusCode,
                    statusText = response.ReasonPhrase,
                    content = contentFn,
                    headers
                };
            }

            public async Task SetTimeout(object function, int timeout) {
                await Task.Delay(TimeSpan.FromMilliseconds(timeout));
                Console.WriteLine("SetTimeout delayed...");
            }

            const int LOG_MESSAGE_LENGTH_LIMIT = 4000; // If changing, also change in JS above. @TODOL - make this a config setting.

            string TrimLogMessage(string msg) {
                if (msg.Length <= LOG_MESSAGE_LENGTH_LIMIT) {
                    return msg;
                }
                return msg[..Math.Min(LOG_MESSAGE_LENGTH_LIMIT - 1, msg.Length)] + "…";
            }

            public void LogInformation(string msg) {
                this.Context.Value?.CancellationToken.ThrowIfCancellationRequested();
                this.Logger.Log(this.Context.Value!, TrellLogLevel.Info, TrimLogMessage(msg));
            }

            public void LogWarning(string msg) {
                this.Context.Value?.CancellationToken.ThrowIfCancellationRequested();
                this.Logger.Log(this.Context.Value!, TrellLogLevel.Warn, TrimLogMessage(msg));
            }

            public void LogError(string msg) {
                this.Context.Value?.CancellationToken.ThrowIfCancellationRequested();
                this.Logger.Log(this.Context.Value!, TrellLogLevel.Error, TrimLogMessage(msg));
            }

            public void LogStatus(string text, dynamic options) {
                this.Context.Value?.CancellationToken.ThrowIfCancellationRequested();
                if (text == null) {
                    this.Logger.Log(this.Context.Value!, TrellLogLevel.Status, "");
                } else {
                    text = TrimLogMessage(text);
                    var status = options.appearance is string appearance
                        ? (object)new { text, appearance }
                        : (object)new { text };
                    this.Logger.Log(this.Context.Value!, TrellLogLevel.Status, JsonSerializer.Serialize(status));
                }
            }

            public IArrayBuffer TextEncode(string text, string encoding) {
                this.Context.Value?.CancellationToken.ThrowIfCancellationRequested();
                if (encoding != "utf-8") {
                    throw new ArgumentException("unsupported encoding");
                }
                return this.engine.CreateJsBuffer(Encoding.UTF8.GetBytes(text));
            }

            public string TextDecode(object bytes, string encoding) {
                this.Context.Value?.CancellationToken.ThrowIfCancellationRequested();
                if (encoding != "utf-8") {
                    throw new ArgumentException("unsupported encoding");
                }

                if (bytes is IArrayBuffer ab) {
                    var buf = new byte[ab.Size];
                    ab.ReadBytes(0, ab.Size, buf, 0);
                    return Encoding.UTF8.GetString(buf);
                } else {
                    throw new ArgumentException("expected ArrayBuffer");
                }
            }
        }
    }
}
