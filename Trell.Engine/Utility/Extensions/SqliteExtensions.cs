using Microsoft.Data.Sqlite;
using static SQLitePCL.raw;

namespace Trell.Engine.Utility.Extensions;

static class SqliteExtensions {
    internal static void Interrupt(this SqliteConnection conn) {
        sqlite3_interrupt(conn.Handle);
    }
}
