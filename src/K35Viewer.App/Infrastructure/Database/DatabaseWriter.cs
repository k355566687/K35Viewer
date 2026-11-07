using Microsoft.Data.Sqlite;

namespace K35Viewer.Infrastructure.Database;

/// <summary>
/// DB書き込みの単一キュー。書き込みは必ずここを経由して1つずつ(直列に)、1トランザクションで実行する。
/// 読み取りは SqliteConnectionFactory から各自で接続を開いて並行に行う(WALのため書き込みと衝突しない)。
/// </summary>
public sealed class DatabaseWriter(SqliteConnectionFactory connectionFactory) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>work を1トランザクションで実行する。例外が出た場合はロールバックして再スローする。</summary>
    public async Task<T> ExecuteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> work,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await connectionFactory.OpenAsync(ct);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            try
            {
                var result = await work(connection, transaction, ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task ExecuteAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> work,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        return ExecuteAsync<bool>(
            async (connection, transaction, token) =>
            {
                await work(connection, transaction, token);
                return true;
            },
            ct);
    }

    public void Dispose() => _gate.Dispose();
}
