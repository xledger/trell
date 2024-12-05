# Trell.Logger.Redis

An implementation of `ITrellLogger` that logs messages to a Redis stream.

## Configuration

May be configured with something like the following:

```
[logger]
asm = "../Trell.Logger.Redis/bin/Debug/net7.0/Trell.Logger.Redis.dll"
type = "Trell.Logger.Redis.RedisLogger"
config = { configuration = "localhost:6379,defaultDatabase=2", stream = "trell_logs" }
```

`logger.config.configuration` is a string in the same shape as defined in https://stackexchange.github.io/StackExchange.Redis/Configuration.html.
