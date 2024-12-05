# Trell

<img src="assets/images/Trell_Logo.png" title="Trell Logo" width="142" height="143" />

# Usage

Via `trell -h`:

```
USAGE:
    trell [OPTIONS] <COMMAND>

EXAMPLES:
    trell serve --config Trell.toml
    trell run file worker.js --handler cron
    trell run dir my-worker-dir --handler webhook
    trell run worker-id worker-123 --handler upload

OPTIONS:
    -h, --help    Prints help information

COMMANDS:
    serve    Start trell as a server, accepting commands via gRPC
    run      Run scripts, directories, or workers (by id)

```