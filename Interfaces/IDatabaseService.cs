using ChatAI.Models;

namespace ChatAI.Interfaces;

public interface IDatabaseService
{
    Task<QueryResult> ExecuteSelectAsync(string sql, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetTablesAsync(CancellationToken ct = default);
    Task<string> GetTableSchemaAsync(string tableName, CancellationToken ct = default);
}
