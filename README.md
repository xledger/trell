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
