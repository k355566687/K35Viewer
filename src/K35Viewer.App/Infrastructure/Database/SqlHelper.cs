using Microsoft.Data.Sqlite;

namespace K35Viewer.Infrastructure.Database;

internal static class SqlHelper
{
    /// <summary>
    /// パラメーター化されたコマンドを作る。値は必ずパラメーターで渡し、SQL文字列へ連結しない。
    /// </summary>
    public static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        // 呼び出し元はすべてRepository内の固定SQLを渡す(値の埋め込みは行わない)。
#pragma warning disable CA2100
        command.CommandText = sql;
#pragma warning restore CA2100
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public static long? GetNullableInt64(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static string? GetNullableString(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
