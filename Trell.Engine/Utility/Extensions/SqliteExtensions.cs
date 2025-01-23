using Microsoft.Data.Sqlite;
using static SQLitePCL.raw;

namespace Trell.Engine.Utility.Extensions;

public static class SqliteExtensions {
    public static void Interrupt(this SqliteConnection conn) {
        sqlite3_interrupt(conn.Handle);
    }
}
