﻿class TextEncoder {
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

class TextDecoder {
    #encoding;

    constructor(label, options) {
        if (label !== undefined && label !== 'utf-8') {
            throw new Error('unsupported encoding');
        }

        this.#encoding = 'utf-8';
    }

    get encoding() {
        return this.#encoding;
    }

    get fatal() {
        throw new Error('TextDecoder.fatal: not implemented yet');
    }

    get ignoreBOM() {
        throw new Error('TextDecoder.ignoreBOM: not implemented yet');
    }

    decode(buffer, options) {
        return dotNetBrowserApi.TextDecode(buffer, this.#encoding);
    }
}

class BasicBlobImpl {
    #parts; #cache; #size;

    constructor(blobParts) {
        this.#parts = blobParts;

        let size = 0;
        for (const part of this.#parts) {
            if (typeof part === 'string') {
                const buf = dotNetBrowserApi.TextEncode(part, 'utf-8');
                size += buf.byteLength;
            } else if (part instanceof Blob) {
                size += part.size;
            } else if (part instanceof ArrayBuffer) {
                size += part.byteLength;
            } else if (part?.buffer instanceof ArrayBuffer) {
                size += part.buffer.byteLength;
            } else {
                throw new Error(`invalid value for blob part: ${part}`);
            }
        }
        this.#size = size;
    }

    async arrayBuffer() {
        if (this.#cache !== undefined) {
            return this.#cache;
        }

        const encodedParts = []
        let encodedSize = 0;

        for (const part of this.#parts) {
            if (typeof part === 'string') {
                const buf = dotNetBrowserApi.TextEncode(part, 'utf-8');
                encodedParts.push(buf);
            } else if (part instanceof Blob) {
                const buf = await part.arrayBuffer();
                encodedParts.push(buf);
            } else if (part instanceof ArrayBuffer) {
                encodedParts.push(part);
            } else if (part?.buffer instanceof ArrayBuffer) {
                encodedParts.push(part.buffer);
            } else {
                throw new Error(`invalid value for blob part: ${part}`);
            }
        }

        const bytes = new Uint8Array(this.#size);
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

    get size() {
        return this.#size;
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

    get size() {
        return this.#end - this.#start;
    }
}

const BLOB_IMPL_SYM = Symbol('blobImpl');

class Blob {
    #impl; #type;

    constructor(parts, options) {
        this.#impl = options[BLOB_IMPL_SYM] ?? new BasicBlobImpl(parts);
        this.#type = options?.type ?? '';
    }

    arrayBuffer() {
        return this.#impl.arrayBuffer();
    }

    bytes() {
        return this.#impl.arrayBuffer().then(x => new Uint8Array(x));
    }

    slice(start, end, type) {
        return new Blob(null, { [BLOB_IMPL_SYM]: new SliceBlobImpl(this, start, end), type: type ?? this.#type });
    }

    stream() {
        throw new Error('not implemented yet');
    }

    text() {
        return this.#impl.arrayBuffer().then(x => dotNetBrowserApi.TextDecode(x, 'utf-8'));
    }

    get size() {
        return this.#impl.size;
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
    // This is to support passing in C# dictionaries from dotNetBrowserApi.Fetch
    if (Object.hasOwn(dotNetHeaders, 'Keys')) {
        for (let k of dotNetHeaders.Keys) {
            this.headers[k] = dotNetHeaders.Item(k);
        }
    } else if (dotNetHeaders instanceof Array) {
        for (let [k, v] of dotNetHeaders) {
            this.headers[k] = v;
        }
    } else {
        for (let [k, v] of Object.entries(dotNetHeaders)) {
            this.headers[k] = v;
        }
    }

    this.get = function (key) {
        return this.headers[key];
    }
}

async function fetch(url, options) {
    const response = await dotNetBrowserApi.Fetch(url, options);
    const blob = async () => new Blob([await response.content()], { type: response.headers['Content-Type'] ?? '' });
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

console = (function () {
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
            } else if (arg === undefined) {
                parts.push("undefined");
            } else if (arg === null) {
                parts.push("null");
            } else if (arg instanceof Error) {
                parts.push(arg.stack);
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