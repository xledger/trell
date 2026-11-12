---
name: trell-worker
description: Write, run, and debug Trell workers. A worker is a JavaScript module (usually worker.js) that the Trell engine runs inside V8 through handlers such as onCronTrigger, onRequest, and onUpload. Use this skill whenever the user works in a directory containing Trell.toml or a Trell worker.js, mentions `trell init`, `trell run`, or `trell run-file`, asks to add or change a worker handler, store data in a worker's SQLite database, call an HTTP API from a worker, or asks why a worker fails, times out, or returns the wrong result, even if they only say "the worker". Trell is neither Node nor a browser, and many familiar globals are missing, so read this before writing any worker code.
---

# Writing Trell workers

Trell runs JavaScript inside V8 (through Microsoft ClearScript) on behalf of a .NET host. A *worker* is an ES module whose default export is an object of handler functions. Trell imports the module, calls one handler, and returns the handler's result as JSON.

The most common mistake is to write Node or browser code. Trell has no `setInterval`, no `URL`, no `require`, no `process`, no npm packages, and no `crypto`. Check the "What the runtime provides" section below before you reach for a global.

## How Trell calls a worker

The entry file is `worker.js` in the worker's code directory. Its default export maps handler names to functions:

```js
async function onCronTrigger(context) {
    console.log(`cron fired at ${context.trigger.timestamp.toISOString()}`);
}

async function onRequest(context) {
    const body = JSON.parse(new TextDecoder().decode(context.request.body));
    return { received: body };
}

async function onUpload(context) {
    const text = await context.file.text();
    return { lines: text.split('\n').length };
}

export default { onCronTrigger, onRequest, onUpload };
```

Trell passes each handler a single `context` object. Trell builds it like this:

```js
{ ...data, <argument>: value, env }
```

- `data` is the JSON object from the work order's `data` field, spread at the top level. The local CLI sends `{ timestamp: "<unix seconds, as a string>" }`. A data key named `env` or the same as the argument name is overwritten, so avoid those keys.
- `env` is the JSON object from the work order's `env` field: secrets and settings. `trell run` fills it from `.env`; `trell run-file` sends `{}`.
- The argument depends on which handler Trell calls:

| Handler | Argument | Shape |
|---|---|---|
| `onCronTrigger` | `context.trigger` | `{ cron: string, timestamp: Date }` |
| `onRequest` | `context.request` | `{ url: string, method: string, headers: object, body: ArrayBuffer }` |
| `onUpload` | `context.file` | a `File` with `name`, `type`, `size`, `text()`, `arrayBuffer()`, `bytes()`, `slice()` |
| any other exported name | `context.argv` | `string[]` |

Handlers may be `async` or plain functions. Trell awaits a returned promise, passes the value through `JSON.stringify`, and sends the string back as the result message. A `Date` becomes an ISO string; returning nothing yields an empty message.

If a handler throws, or its promise rejects, the result code is `UserException`. The message is the error's `toString()` (for example `Error: boom`), and the JavaScript stack trace comes back in the result's `scriptError` field. Other result codes a worker can cause:

| Code | Cause |
|---|---|
| `Timeout` | The module took longer than the startup limit to load, or the handler ran past the execution limit. |
| `EntryPointNotDefined` | The default export has no function with the requested name. Check the spelling and that the function is in the `export default { ... }` object. |
| `UnauthorizedDatabaseAccess` | The worker opened a `shared/...` database that the work order did not grant. |
| `InvalidPath` | The entry file name breaks Trell's path rules (see "Modules and files"). |

## Develop and test locally

Install the CLI with `dotnet tool install --global Trell`. Then, in an empty directory:

```sh
trell init                                   # writes Trell.toml, worker.js, .env, .gitignore, data/
trell run cron                               # calls onCronTrigger
trell run request payload.json               # calls onRequest; the file becomes the body
trell run request payload.json --url https://example.com/hook -H "X-Token: abc"
trell run upload report.csv                  # calls onUpload with the file
trell run-file jobs.js rebuild_index a b     # calls rebuild_index in jobs.js with argv ["a", "b"]
```

Run these from the directory that holds `Trell.toml`. Each command runs the handler once, in-process, and logs a line like:

```
Run: Execution Id: id-1 Result: { "code": "Success", "message": "{\"lines\":3}" }
```

The process exits with code 0 only when the result code is `Success`. When the handler throws, times out, or does not exist, or when the command line is wrong, it exits with 1, so scripts and CI can check the exit code. Lines logged by the worker appear above the `Result:` line as `Execution id-1 : <message>`.

`trell run request` always sends a `POST`, uses `http://www.example.com/fetch` as the URL unless you pass `--url`, and sets `Content-Type` to the file's MIME type, such as `application/json`. A `Content-Type` passed with `-H` replaces it.

`.env` uses TOML syntax, not dotenv syntax. Write `API_KEY = "abc"`, with the value quoted. The handler reads it as `context.env.API_KEY`.

To try a time limit locally, add a section to `Trell.toml`:

```toml
[worker.limits]
max_startup_duration = "1s"
max_execution_duration = "30s"
grace_period = "5s"
```

## What the runtime provides

Available as globals:

- `fetch(url, options)` and `Headers`: HTTP requests. Request bodies must be strings. Redirects are not followed.
- `setTimeout(callback, delay, ...args)` and `clearTimeout(id)`. A timer fires only while the handler is still running; see "Time limits".
- `console.log`, `console.warn`, `console.error`, and `console.status(text, { appearance })`.
- `TextEncoder` and `TextDecoder` (UTF-8 only), `Blob`, and `File`.
- `DOMParser` and `XMLSerializer`, from a bundled copy of `@xmldom/xmldom`, for XML.
- `sqlite.open(...)`: per-worker SQLite databases.
- Everything built into the JavaScript language itself: `JSON`, `Promise`, `Date`, `Map`, `Intl`, typed arrays, `BigInt`, and so on.

`Headers`, `Blob`, `File`, `TextEncoder`, `TextDecoder`, `DOMParser`, and `XMLSerializer` are top-level declarations, not properties of `globalThis`. Refer to them by bare name; `globalThis.Blob` is `undefined`.

Missing, with what to do instead:

| Missing | Instead |
|---|---|
| `setInterval`, `queueMicrotask` | Repeat with a loop that awaits a `setTimeout` promise, and queue a microtask with `Promise.resolve().then(fn)`. For work that repeats across runs, let the host send cron triggers. |
| `URL`, `URLSearchParams` | Build and parse URLs with string functions and `encodeURIComponent`. |
| `atob`, `btoa` | Write a small base64 helper over `Uint8Array`. |
| `crypto`, `structuredClone` | No hashing or random UUIDs. Use `JSON.parse(JSON.stringify(x))` to deep-copy plain data. |
| `Response`, `Request`, `FormData`, `AbortController` | Use the object `fetch` returns, and send form data as a URL-encoded or JSON string. |
| `require`, `process`, Node built-ins, npm packages | Use ES `import` of `.js` files inside the worker's directory. Configuration arrives in `context.env`. |

Read `references/runtime-api.md` for the exact behavior of `fetch`, `console`, `Blob`/`File`, text encoding, XML, and SQLite. It lists several sharp edges that differ from the web standards; read it before you write code that uses those APIs.

## Storing data with SQLite

Each worker has its own directory of SQLite databases. Open one, run SQL, and close it:

```js
const db = await sqlite.open();                     // the "default" database
await db.exec({ sql: 'CREATE TABLE IF NOT EXISTS seen (id TEXT PRIMARY KEY, at TEXT)' });
await db.exec({
    sql: 'INSERT OR IGNORE INTO seen (id, at) VALUES ($id, $at)',
    parameters: { $id: 'a1', $at: new Date().toISOString() },
});
const rows = (await db.query({ sql: 'SELECT * FROM seen' })) ?? [];
db.close();
```

Three facts cause most SQLite bugs in workers:

1. `query` returns `null`, not `[]`, when no rows match. Write `(await db.query(...)) ?? []`.
2. Every integer and real column comes back as a **string**, so 64-bit values survive `JSON.parse`. Convert with `Number(row.n)` or `BigInt(row.n)` before arithmetic; `row.n + 1` concatenates.
3. Always pass values through `parameters`. Never build SQL by string concatenation.

Database names may contain only lowercase letters, digits, and `_`. Each database is capped in size (1024 pages, 4 MiB at the default page size, unless the host configures otherwise), and writes fail with a "database or disk is full" error past the cap. `ATTACH` is refused, and only an allowlist of `PRAGMA`s runs (`cache_size`, for one, is refused).

A worker can also open shared databases named `shared/<name>`, which belong to the user rather than the worker. The work order must list each one; `trell run` grants every `data/shared/*.db` file. Transactions, `queryOne`, and the full API are in `references/runtime-api.md`.

## Time limits

The host sets three limits, and a work order can only shorten them:

- **Startup**: how long importing `worker.js` and its imports may take. Keep top-level code cheap; do work inside handlers.
- **Execution**: how long the handler may run.
- **Grace period**: extra time after both limits before Trell gives up on the execution entirely.

When a limit passes, Trell interrupts the script, even a synchronous `while (true) {}` or a pending `fetch`, and the result code is `Timeout`. After that, `console` and `sqlite` calls throw instead of running.

Timers belong to the execution that set them. When the handler returns or times out, Trell cancels every pending timer, and its callback never runs, not even in a later execution. To wait, await the timer before returning:

```js
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
await sleep(500);   // counts against the execution limit
``` For long jobs, process data in batches and record progress in SQLite, so that the next run can continue where a timed-out run stopped.

## Modules and files

- Use ES module syntax: `import` and `export`. CommonJS `require` does not exist.
- Import other files by relative path with the `.js` extension: `import { parse } from './lib/parse.js'`. The loader does not try other extensions, does not resolve bare package names, and refuses any file outside the worker's code directory.
- **File names are case-sensitive on every OS.** An import or entry file name must match the file on disk letter for letter, even on Windows and macOS. Importing `./utils.js` when the file is `Utils.js` fails with `"./utils.js" not found; the file on disk is "Utils.js"`.
- To use a third-party library, copy a self-contained ES module build of it into the worker's directory and import it by relative path. It must not depend on Node or browser globals from the "Missing" table.
- Name entry files (`worker.js`, or the file given to `trell run-file`) with letters, digits, `_`, `-`, and `.` only. Trell rejects spaces, `..`, and names that start or end with `.`, such as `.worker.js`.
- Each execution gets a fresh V8 engine. Module-level variables do not survive between runs; persist state in SQLite.
