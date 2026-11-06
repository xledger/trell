# Trell

[![NuGet version (Trell)](https://img.shields.io/nuget/v/Trell.svg?style=flat-square)](https://www.nuget.org/packages/Trell/)

<img src="assets/images/Trell_Logo.png" title="Trell Logo" width="142" height="143" />

Trell is an observable and extensible execution engine that wraps V8.

# Installation

Make sure .NET is installed:

https://dotnet.microsoft.com/en-us/download

Then run this command:

```
dotnet tool install --global Trell
```

# Usage

Via `trell -h`:

```
USAGE:
    trell [OPTIONS] <COMMAND>

EXAMPLES:
    trell init
    trell serve
    trell run cron
    trell run upload path/to/file.csv

OPTIONS:
    -h, --help    Prints help information

COMMANDS:
    init                Initialize directory with a new worker to get started with trell faster
    serve               Start trell as a server, accepting commands via gRPC
    run <handler-fn>    Runs a handler on the worker
```

## Serving gRPC

`trell serve` accepts gRPC requests on the Unix socket named by `socket` in `Trell.toml`.
To accept them over TCP as well, set `listen` to a `host:port`:

```toml
socket = "server.sock"
listen = "127.0.0.1:3000"
```

The host is an IP address (IPv6 in brackets, as in `[::1]:3000`), `localhost`, or `*` for every
address. Without `listen`, the server opens no TCP port. The gRPC API has no authentication, so
anyone who can reach the address can run workers, query their databases, and delete them. Bind
it to an address that only trusted clients can reach.
