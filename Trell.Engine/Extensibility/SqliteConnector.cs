using Microsoft.Data.Sqlite;
using Serilog;
using SQLitePCL;
using Trell.Engine.Extensibility;
using Trell.Engine.Extensibility.Interfaces;

namespace Trell;

public sealed record SqliteConnectionOptions(bool ReadOnly);

public class SqliteConnector(IStorageProvider storage) {
    public async Task<SqliteConnection> Open(string dbName, SqliteConnectionOptions options) {
        var dbfilename = dbName + ".db";

        if (!storage.TryResolvePath(dbfilename, out var resolvedPath, out var err)) {
            throw new TrellUserException(err);
        }

        var conn = new SqliteConnection(new SqliteConnectionStringBuilder {
            Mode = options.ReadOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            DataSource = resolvedPath
        }.ToString());

        await conn.OpenAsync();
        Log.Verbose("Opened database {Path}", resolvedPath);

        // Ensure database is size constrained.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA max_page_count = {storage.MaxDatabasePageCount}; ";
        await cmd.ExecuteNonQueryAsync();

        // Disallow certain action types.
        raw.sqlite3_set_authorizer(conn.Handle, SqliteAuthorizer, null);

        return conn;
    }

    // https://www.sqlite.org/pragma.html
    static readonly IReadOnlySet<string> ALLOWED_PRAGMAS = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase) {
        "analysis_limit",
        "application_id",
        "auto_vacuum",
        "automatic_index",
        "busy_timeout",
        // Allows setting a page cache size that takes up too much memory.
        // Could probably be enabled if we set the Application Defined Page Cache: https://www.sqlite.org/c3ref/pcache_methods2.html
        // "cache_size",
        "cache_spill",
        // Deprecated
        // "case_sensitive_like",
        "cell_size_check",
        "checkpoint_fullfsync",
        "collation_list",
        "compile_options",
        // Deprecated
        // "count_changes",
        // Deprecated
        // "data_store_directory",
        "data_version",
        "database_list",
        // Deprecated
        // "default_cache_size",
        "defer_foreign_keys",
        // Deprecated
        // "empty_result_callbacks",
        "encoding",
        "foreign_key_check",
        "foreign_key_list",
        "foreign_keys",
        "freelist_count",
        // Deprecated
        // "full_column_names",
        "fullfsync",
        "function_list",
        // This pragma can only lower the heap limit, never raise it.
        "hard_heap_limit",
        "ignore_check_constraints",
        "incremental_vacuum",
        "index_info",
        "index_list",
        "index_xinfo",
        "integrity_check",
        "journal_mode",
        "journal_size_limit",
        "legacy_alter_table",
        "legacy_file_format",
        "locking_mode",
        "max_page_count",
        // The PRAGMA mmap_size statement will never increase the amount of address space used for memory-mapped I/O above the hard limit set by the SQLITE_MAX_MMAP_SIZE compile-time option, nor the hard limit set at startup-time by the second argument to sqlite3_config(SQLITE_CONFIG_MMAP_SIZE)
        "mmap_size",
        "module_list",
        "optimize",
        "page_count",
        "page_size",
        // Non-standard compile time option builds
        // "parser_trace",
        "pragma_list",
        "query_only",
        "quick_check",
        "read_uncommitted",
        "recursive_triggers",
        "reverse_unordered_selects",
        // Used for testing SQLite, not recommended for applications
        // "schema_version",
        "secure_delete",
        // Deprecated
        // "short_column_names",
        "shrink_memory",
        "soft_heap_limit",
        // Used for testing SQLite, not recommended for applications
        // "stats",
        "synchronous",
        "table_info",
        "table_list",
        "table_xinfo",
        "temp_store",
        // Deprecated
        // "temp_store_directory",
        "threads",
        "trusted_schema",
        "user_version",
        // Non-standard compile time option builds
        // "vdbe_addoptrace",
        // Non-standard compile time option builds
        // "vdbe_debug",
        // Non-standard compile time option builds
        // "vdbe_listing",
        // Non-standard compile time option builds
        // "vdbe_trace",
        "wal_autocheckpoint",
        "wal_checkpoint",
        // Used for testing SQLite, not recommended for applications
        // "writable_schema",
    };

    static int SqliteAuthorizer(object user_data, int action_code, utf8z param0, utf8z param1, utf8z dbName, utf8z inner_most_trigger_or_view) {
        // https://www.sqlite.org/c3ref/c_alter_table.html
        switch (action_code) {
            // Deny attaching to other databases.
            case raw.SQLITE_ATTACH:
                return raw.SQLITE_DENY;
            // Filter pragmas.
            case raw.SQLITE_PRAGMA:
                // #define SQLITE_PRAGMA               19   /* Pragma Name     1st arg or NULL */
                try {
                    var name = param0.utf8_to_string();
                    if (name.IndexOf('.', StringComparison.InvariantCultureIgnoreCase) is int ix and >= 0) {
                        name = name[ix..];
                    }

                    if (ALLOWED_PRAGMAS.Contains(name)) {
                        return raw.SQLITE_OK;
                    }

                    return raw.SQLITE_DENY;
                } catch {
                    return raw.SQLITE_DENY;
                }
            // Allow all other statements.
            default:
                return raw.SQLITE_OK;
        }
    }
}
