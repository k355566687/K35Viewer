using K35Viewer.Domain;
using K35Viewer.Infrastructure.Database;

namespace K35Viewer.Services;

/// <summary>確定した操作結果を operation_history へ記録する。開始通知は記録しない。</summary>
public sealed class HistoryService(HistoryRepository history, DatabaseWriter writer)
{
    public Task RecordAsync(OperationResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        return writer.ExecuteAsync(
            (connection, transaction, token) => history.InsertAsync(connection, transaction, result, token),
            ct);
    }
}
