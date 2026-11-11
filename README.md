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
    trell run upload example.csv
    trell run-file myFile.js myFunction

OPTIONS:
    -h, --help    Prints help information

COMMANDS:
    init                                    Initialize directory with a new worker to get started with trell faster
    serve                                   Start trell as a server, accepting commands via gRPC
    run <handler-fn>                        Run a handler on the worker
    run-file <file-name> <function-name>    Run a user-defined handler from a specified file
    install-skill                           Install the trell-worker skill, which teaches coding agents to write Trell workers
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

### Worker pool

The server runs each user's work in one of `worker.pool.size` worker processes. It binds a user to
a process on the user's first request, and the user keeps that process while any of its requests
run. When every process is bound and a new user sends a request, the server gives the new user
the process of the user that has gone longest without a running request, provided that user has
been idle for at least `worker.pool.idle_timeout` (default `"30s"`). If no user qualifies, the
request fails with "Too much work." A negative timeout, such as `"-00:00:01"`, keeps each user on
its process for the life of the server.

If a worker process exits or crashes, requests that were running on it fail. The next request
that would use the process gets a newly started process instead, whichever user sends it.

```toml
[worker.pool]
size = 8
idle_timeout = "30s"
```

# Agent skill

Trell ships with a skill, `trell-worker`, that teaches coding agents such as Claude Code and Codex how to write Trell workers. It covers the handler contract, the runtime APIs and the places where they differ from Node and the browser, SQLite, time limits, and hosting.

To install it, run `trell install-skill` and choose one or more locations from the list. To install without prompts, name each location:

```
trell install-skill --target claude            # ~/.claude/skills, for all projects
trell install-skill --target claude-project    # ./.claude/skills, for this project
trell install-skill --target codex             # ~/.codex/skills
trell install-skill --target agents            # ~/.agents/skills
trell install-skill --target agents-project    # ./.agents/skills
trell install-skill --path path/to/skills      # any other skills directory
```

If the skill is already installed in a location, `install-skill` asks before overwriting it. `--force` overwrites without asking. Run `install-skill` again after upgrading Trell to get the skill for the new version.

The skill's source is in [`Trell/Skill/trell-worker`](Trell/Skill/trell-worker).
