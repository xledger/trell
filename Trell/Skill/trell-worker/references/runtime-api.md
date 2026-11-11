# Trell runtime API reference

This file describes every API Trell adds to the JavaScript language, including the places where it differs from the web standard of the same name. Each behavior listed here was checked against a running Trell engine.

## Contents

- [fetch and Headers](#fetch-and-headers)
- [console](#console)
- [TextEncoder and TextDecoder](#textencoder-and-textdecoder)
- [Blob and File](#blob-and-file)
- [XML: DOMParser and XMLSerializer](#xml-domparser-and-xmlserializer)
- [sqlite](#sqlite)

## fetch and Headers

```js
const res = await fetch('https://api.example.com/items', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${context.env.API_KEY}` },
    body: JSON.stringify({ name: 'widget' }),
});
if (!res.ok) {
    throw new Error(`items API returned ${res.status} ${res.statusText}: ${await res.text()}`);
}
const item = await res.json();
```

Request options:

- `method`: defaults to `GET`.
- `headers`: a plain object of header names to values. Do not pass a `Headers` instance here; Trell enumerates the object's own properties.
- `body`: must be a string. Passing an object, `ArrayBuffer`, `Blob`, or typed array throws `options.body type ... not allowed`, so call `JSON.stringify` on objects first. Trell sends the body as UTF-8 and appends `; charset=utf-8` to its content type. Without a `Content-Type` header, the content type is `text/plain`.
- `url` must be absolute. There is no `URL` class, so build query strings by hand with `encodeURIComponent`.

Differences from browser `fetch`:

- **Redirects are not followed.** A 3xx response comes back as is. To follow one, read `res.headers.get('Location')` and call `fetch` again.
- **No cookies** are stored or sent.
- **Response content headers are missing.** `res.headers` holds only response headers such as `Date`, `Location`, `Server`, and `ETag`. `Content-Type`, `Content-Length`, `Content-Encoding`, `Last-Modified`, and the other content headers are absent, and `get` returns `undefined` for them. Decide how to parse a body from what you know about the API, not from `Content-Type`.
- **`headers.get(name)` is case-sensitive.** It is a plain property lookup, so `get('date')` misses a header the server sent as `Date`. Use the exact capitalization the server sends, or search `Object.keys(res.headers.headers)` case-insensitively.
- `fetch` cannot be aborted. It ends when the server responds, when .NET's 100-second HTTP timeout passes, or when the execution limit stops the whole worker.

The response object has `ok`, `status`, `statusText`, `headers`, and three body readers: `text()`, `json()`, and `blob()`. Each returns a promise.

`new Headers(init)` accepts a plain object or an array of `[name, value]` pairs and offers only `get(name)`.

## console

```js
console.log('imported', count, 'rows');              // information
console.warn('skipped row', JSON.stringify(row));    // warning
console.error(err);                                  // error; an Error prints its stack
console.status('Imported 400 of 1000', { appearance: 'info' });
```

- Trell joins the arguments with spaces. Strings print as is, `Error` objects print their `stack`, and `null` and `undefined` print as words. **Every other value prints through `toString()`**, so objects print as `[object Object]` and arrays print comma-joined. Wrap structured values in `JSON.stringify`.
- Trell cuts each message to 4000 characters and appends `…`.
- `console.status(text, options)` reports progress, a status line the host can show to the user. Trell logs it as JSON: `{"text": ..., "appearance": ...}`. The `appearance` value is passed through unchanged; agree with the host on which values it understands. `console.status(null)` sends an empty status.
- There is no `console.info`, `console.debug`, `console.table`, or `console.time`.
- Messages go to the logger the host configures. Locally that is the terminal, as lines like `Execution id-1 : <message>`.

## TextEncoder and TextDecoder

Both support UTF-8 only. Constructing them with any other encoding label throws.

```js
const bytes = new TextEncoder().encode('héllo');        // Uint8Array
const text = new TextDecoder().decode(bytes.buffer);    // pass the ArrayBuffer, not the Uint8Array
```

- **`decode` accepts only an `ArrayBuffer`.** Passing a `Uint8Array` or other view throws `expected ArrayBuffer`. Pass `view.buffer`. If the view covers only part of its buffer (for example a `subarray`), pass `view.slice().buffer` so you decode only those bytes.
- `context.request.body` is already an `ArrayBuffer`, so `new TextDecoder().decode(context.request.body)` works directly.
- `encodeInto`, `fatal`, `ignoreBOM`, and the `stream` decode option are not implemented.

## Blob and File

```js
const blob = new Blob(['id,name\n', '1,widget\n'], { type: 'text/csv' });
const file = new File([blob], 'export.csv', { type: 'text/csv' });
```

- **Always pass the options argument**, even if empty: `new Blob(parts, {})`. Omitting it throws `TypeError: Cannot read properties of undefined`.
- Parts may be strings (encoded as UTF-8), `Blob`s, `ArrayBuffer`s, or typed arrays. A typed array contributes its whole underlying buffer, not just the viewed range, so pass `view.slice()` for partial views.
- Methods: `arrayBuffer()`, `bytes()` (a `Uint8Array`), `text()`, and `slice(start, end, type)`. Properties: `size` and `type`. `stream()` throws.
- A `File` adds `name`. There is no `lastModified`.
- The `context.file` passed to `onUpload` is one of these `File` objects.

## XML: DOMParser and XMLSerializer

Trell bundles `@xmldom/xmldom` 0.9.7 and exposes its exports as globals: `DOMParser`, `XMLSerializer`, `DOMImplementation`, `Node`, `Element`, `Document`, `ParseError`, `MIME_TYPE`, `NAMESPACE`, and the rest.

```js
const doc = new DOMParser().parseFromString(xmlText, 'text/xml');
for (const line of Array.from(doc.getElementsByTagName('Line'))) {
    const amount = line.getAttribute('amount');
}
const out = new XMLSerializer().serializeToString(doc);
```

- The MIME type argument is required. Omitting it throws.
- Malformed XML, such as mismatched tags, throws a `ParseError` and also logs an `[xmldom fatalError]` line. Recoverable problems, such as an unquoted attribute, log an `[xmldom warning]` line and parsing continues.
- This is a DOM implementation, not a browser. There is no `querySelector` and no `XPathEvaluator`; walk the tree with `getElementsByTagName`, `childNodes`, `getAttribute`, and similar methods.

## sqlite

### Opening a database

```js
const db = await sqlite.open();                          // the worker's "default" database
const cache = await sqlite.open({ dbname: 'cache' });    // the worker's "cache" database
const rates = await sqlite.open({ dbname: 'shared/fx_rates' });  // a shared database
```

- A database is created the first time it is opened.
- `dbname` may contain only lowercase letters, digits, and `_`, with an optional `shared/` prefix. Any other name throws `INVALID_DB_NAME`. The error message quotes `"default"` instead of the name you passed; that is a known Trell bug, so look at your own `dbname`.
- Plain names refer to the worker's own databases. `shared/<name>` refers to a database shared by all of the user's workers. Trell refuses (`UnauthorizedDatabaseAccess`) any shared name that the work order does not list. When granted, a shared database is readable and writable.
- Locally, worker databases are `data/<name>.db` and shared databases are `data/shared/<name>.db`, under the directory that holds `Trell.toml`. `trell run` and `trell run-file` grant every `.db` file in `data/shared/`.
- Call `db.close()` when finished.

### Running SQL

Every statement method takes one object: `{ sql, parameters, transaction }`. Only `sql` is required.

```js
await db.exec({ sql: 'CREATE TABLE IF NOT EXISTS item (id INTEGER PRIMARY KEY, name TEXT, price REAL)' });

await db.exec({
    sql: 'INSERT INTO item (name, price) VALUES ($name, $price)',
    parameters: { $name: 'widget', $price: 9.5 },
});

const items = (await db.query({ sql: 'SELECT * FROM item WHERE price < $max', parameters: { $max: 20 } })) ?? [];
const first = await db.queryOne({ sql: 'SELECT count(*) AS n FROM item' });
const count = Number(first.n);
```

- `exec` returns nothing. It accepts several statements separated by `;`.
- `query` returns an array of row objects keyed by column name, or **`null` when there are no rows**.
- `queryOne` returns the first row as an object, or `null`.
- `parameters` is a plain object. Keys may be written `$name`, `@name`, `:name`, or bare `name`; each matches the same-named placeholder in the SQL. Values may be strings, numbers, booleans (stored as 1 and 0), `null`, or `undefined` (both stored as NULL).
- **Binary parameters are not supported.** Binding a `Uint8Array` or `ArrayBuffer` throws `No mapping exists from object type ...`. Store binary data as base64 or hex text.

### Values that come back

- **Integer and real values come back as strings**: `{ id: "1", price: "9.5" }`. Trell does this so 64-bit integers survive `JSON.parse`. Convert with `Number(...)`, or `BigInt(...)` for integers beyond 2^53. Comparisons and arithmetic on unconverted values silently go wrong: `row.n + 1` gives `"11"`, not `2`.
- Booleans come back as `"1"` and `"0"`.
- Text comes back as strings, NULL as `null`, and BLOB values as base64 strings.
- JavaScript numbers lose precision above 2^53 before they reach SQLite. To store a larger integer exactly, pass it as a string into an `INTEGER` column; SQLite's type affinity converts it exactly.

### Transactions

```js
const tx = db.beginTransaction();
try {
    await db.exec({ sql: 'UPDATE account SET balance = balance - $amt WHERE id = $from', parameters: { $amt: 10, $from: 1 }, transaction: tx });
    await db.exec({ sql: 'UPDATE account SET balance = balance + $amt WHERE id = $to', parameters: { $amt: 10, $to: 2 }, transaction: tx });
    tx.commit();
} catch (e) {
    tx.rollback();
    throw e;
}
```

`beginTransaction()` returns `{ commit(), rollback() }`, and both of those run synchronously. Pass the transaction object as `transaction` to each statement that belongs to it. A single `exec` with `BEGIN; ...; COMMIT;` also works when all statements fit in one call.

### Limits and restrictions

- Each database has a page cap: 1024 pages by default, or 4 MiB at SQLite's default page size. The host changes it with `storage.max_database_page_count`. Writes past the cap fail with `database or disk is full`. Delete old rows or `VACUUM` to recover space.
- `ATTACH DATABASE` fails with `not authorized`.
- Only an allowlist of `PRAGMA`s runs. Common ones such as `journal_mode`, `foreign_keys`, `user_version`, `table_info`, `integrity_check`, and `busy_timeout` work. `cache_size` and testing-only pragmas such as `writable_schema` fail with `not authorized`.
