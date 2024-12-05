using Microsoft.ClearScript.JavaScript;
using System.Net.Http;
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

        public string JsScript => """
            async function fetch(url, options) {
                const response = await dotNetBrowserApi.Fetch(url, options);
                const blob = async () => new Blob([await response.content()], {type: response.headers['Content-Type'] ?? ''});
                return {
                    ok: response.ok,
                    status: response.status,
                    statusText: response.statusText,
                    text: () => blob().then(x => x.text()),
                    json: () => blob().then(x => x.text()).then(x => JSON.parse(x)),
                    blob: blob,
                    headers: new Headers(response.headers)
                };
            }

            class TextEncoder {
              #encoding;

              constructor(encoding) {
                if (encoding !== undefined && encoding !== 'utf-8') {
                  throw new Error('unsupported encoding');
                }

                this.#encoding = encoding ?? 'utf-8';
              }

              get encoding() {
                return this.#encoding;
              }

              encode(string) {
                return new Uint8Array(dotNetBrowserApi.TextEncode(string, this.#encoding));
              }

              encodeInto(string, uint8Array) {
                throw new Error('not implemented yet');
              }
            }

            const BLOB_IMPL_SYM = Symbol('blobImpl');

            class BasicBlobImpl {
              #parts; #cache;

              constructor(blobParts) {
                this.#parts = blobParts;
              }

              async arrayBuffer() {
                if (this.#cache !== undefined) {
                  return this.#cache;
                }

                const encodedParts = []
                let encodedSize  = 0;
            
                for (const part of this.#parts) {
                  if (typeof part === 'string') {
                    const buf = dotNetBrowserApi.TextEncode(part, 'utf-8');
                    encodedParts.push(buf);
                    encodedSize += buf.byteLength;
                  } else if (part instanceof Blob) {
                    const buf = await part.arrayBuffer();
                    encodedParts.push(buf);
                    encodedSize += buf.byteLength;
                  } else if (part instanceof ArrayBuffer) {
                    encodedParts.push(part);
                    encodedSize += part.byteLength;
                  } else if (part?.buffer instanceof ArrayBuffer) {
                    encodedParts.push(part.buffer);
                    encodedSize += part.buffer.byteLength;
                  } else {
                    throw new Error(`invalid value for blob part: ${part}`);
                  }
                }

                const bytes = new Uint8Array(encodedSize);
                let i = 0;
                for (const part of encodedParts) {
                  for (const v of new Uint8Array(part)) {
                    bytes[i++] = v;
                  }
                }

                this.#cache = bytes.buffer;
                this.#parts = undefined;
                return bytes.buffer;
              }
            }

            class SliceBlobImpl {
              #source; #start; #end; #cache;

              constructor(source, start, end) {
                this.#source = source;
                this.#start = start;
                this.#end = end;
              }

              async arrayBuffer() {
                if (this.#cache !== undefined) {
                  return this.#cache;
                }

                const buf = await this.#source.arrayBuffer();
                this.#cache = buf.slice(this.#start, this.#end);
                this.#source = undefined;
                return this.#cache;
              }
            }

            class Blob {
              #impl; #type;

              constructor(parts, options) {
                this.#impl = options[BLOB_IMPL_SYM] ?? new BasicBlobImpl(parts);
                this.#type = options?.type ?? '';
              }

              arrayBuffer() {
                return this.#impl.arrayBuffer();
              }

              slice(start, end, type) {
                return new Blob(null, {[BLOB_IMPL_SYM]: new SliceBlobImpl(this, start, end), type: type ?? this.#type});
              }

              stream() {
                throw new Error('not implemted yet');
              }

              text() {
                return this.#impl.arrayBuffer().then(x => dotNetBrowserApi.TextDecode(x, 'utf-8'));
              }

              get type() {
                return this.#type;
              }
            }

            class File extends Blob {
              #filename;

              constructor(parts, filename, options) {
                super(parts, options);
                this.#filename = filename;
              }

              get name() {
                return this.#filename;
              }
            }

            function Headers(dotNetHeaders) {
                this.headers = {};
                for (let k of dotNetHeaders.Keys || []) {
                    this.headers[k] = dotNetHeaders.Item(k);
                }
                this.get = function(key) {
                    return this.headers[key];
                }
            }

            console = (function() {
                const LOG_MESSAGE_LENGTH_LIMIT = 4000; // If changing, also change in C# below.
                let getLogMessage = (args) => {
                    let parts = [];
                    let len = 0;
                    for (let i = 0; i < args.length && len < LOG_MESSAGE_LENGTH_LIMIT; i += 1) {
                        let arg = args[i];
                        if (typeof arg === 'string' || arg instanceof String) {
                            parts.push(arg);
                        } else if (arg?.hostException) {
                            parts.push(arg.toString());
                        } else {
                            let s = arg?.toString();
                            parts.push(s);
                        }
                        let sepLen = i > 0 ? 1 : 0;
                        len += parts[parts.length - 1].length + sepLen;
                    }
                    let msg = parts.join(' ');
                    if (msg.length > LOG_MESSAGE_LENGTH_LIMIT) {
                        msg = msg.substring(0, LOG_MESSAGE_LENGTH_LIMIT) + '…';
                    }
                    return msg;
                };
                return {
                    log: (...args) => {
                        let s = getLogMessage(args);
                        dotNetBrowserApi.LogInformation(s);
                    },
                    warn: (...args) => {
                        let s = getLogMessage(args);
                        dotNetBrowserApi.LogWarning(s);
                    },
                    error: (...args) => {
                        let s = getLogMessage(args);
                        dotNetBrowserApi.LogError(s);
                    },
                    status: (text, options) => {
                      dotNetBrowserApi.LogStatus(text, options ?? {});
                    },
                }
            })();
            """;

        public IReadOnlyList<string> TopLevelJsNamesExposed { get; } = new[] {
            "fetch",
            "Headers",
            "console",
            "Blob",
            "TextEncoder",
        };
    }

    namespace BrowserApiDotNetObjects {
        public class BrowserApi {
            static readonly HttpClientHandler HttpClientHandler;
            //static readonly UriCreationOptions UriCreationOptions;

            static BrowserApi() {
                HttpClientHandler = new HttpClientHandler {
                    UseCookies = false,
                    AllowAutoRedirect = false
                };
                //UriCreationOptions = new();
            }

            ITrellLogger Logger { get; }
            IAtomRead<TrellExecutionContext> Context { get; }

            readonly EngineWrapper engine;

            public BrowserApi(ITrellLogger logger, IAtomRead<TrellExecutionContext> context, EngineWrapper engine) {
                this.Logger = logger;
                this.Context = context;
                this.engine = engine;
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

                //        /*
                //         *
                //method: "POST", // *GET, POST, PUT, DELETE, etc.
                //mode: "cors", // no-cors, *cors, same-origin
                //cache: "no-cache", // *default, no-cache, reload, force-cache, only-if-cached
                //credentials: "same-origin", // include, *same-origin, omit
                //headers: {
                //  "Content-Type": "application/json",
                //  // 'Content-Type': 'application/x-www-form-urlencoded',
                //},
                //redirect: "follow", // manual, *follow, error
                //referrerPolicy: "no-referrer", // no-referrer, *no-referrer-when-downgrade, origin, origin-when-cross-origin, same-origin, strict-origin, strict-origin-when-cross-origin, unsafe-url
                //body: JSON.stringify(data), // body data type must match "Content-Type" header
                //         *
                //         */

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
                this.Logger.Log(this.Context.Value!, TrellLogLevel.Info, TrimLogMessage(msg));
            }

            public void LogWarning(string msg) {
                this.Logger.Log(this.Context.Value!, TrellLogLevel.Warn, TrimLogMessage(msg));
            }

            public void LogError(string msg) {
                this.Logger.Log(this.Context.Value!, TrellLogLevel.Error, TrimLogMessage(msg));
            }

            public void LogStatus(string text, dynamic options) {
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
                if (encoding != "utf-8") {
                    throw new ArgumentException("unsupported encoding");
                }
                return this.engine.CreateJsBuffer(Encoding.UTF8.GetBytes(text));
            }

            public string TextDecode(object bytes, string encoding) {
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
