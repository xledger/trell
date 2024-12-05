# Trell.Observer.Serilog

An implementation of `ITrellObserver` that logs all events to Serilog.

## Configuration

May be configured with something like the following:

```
[[observers]]
asm = "../Trell.Observer.Serilog/bin/Debug/net7.0/Trell.Observer.Serilog.dll"
type = "Trell.Observer.Serilog.SerilogObserver"
config = { level = "Debug" }
```
