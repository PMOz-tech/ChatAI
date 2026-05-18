using System.ComponentModel;
using System.Text.Json;
using ChatAI.Interfaces;
using Microsoft.Extensions.AI;

namespace ChatAI.Services;

public sealed class DatabaseTools
{
    private readonly IDatabaseService _db;
    private readonly ILogger<DatabaseTools> _logger;

    public IReadOnlyList<AIFunction> All { get; }

    public DatabaseTools(IDatabaseService db, ILogger<DatabaseTools> logger)
    {
        _db = db;
        _logger = logger;

        Func<string, CancellationToken, Task<string>> queryFn = QueryDatabaseAsync;
        Func<CancellationToken, Task<string>> listFn = ListTablesAsync;
        Func<string, CancellationToken, Task<string>> schemaFn = GetTableSchemaAsync;

        All =
        [
            AIFunctionFactory.Create(queryFn, new AIFunctionFactoryOptions { Name = "query_database" }),
            AIFunctionFactory.Create(listFn,  new AIFunctionFactoryOptions { Name = "list_tables" }),
            AIFunctionFactory.Create(schemaFn, new AIFunctionFactoryOptions { Name = "get_table_schema" }),
        ];
    }

    [Description("Execute a SQL SELECT query against the database and return the results as a JSON array of row objects.")]
    private async Task<string> QueryDatabaseAsync(
        [Description("A valid SQL SELECT statement")] string sql,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Tool call: query_database — {Sql}", sql);
        try
        {
            var result = await _db.ExecuteSelectAsync(sql, cancellationToken);
            return JsonSerializer.Serialize(result.Rows);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "query_database failed");
            return $"Error: {ex.Message}";
        }
    }

    [Description("List all table names available in the database.")]
    private async Task<string> ListTablesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Tool call: list_tables");
        try
        {
            var tables = await _db.GetTablesAsync(cancellationToken);
            return JsonSerializer.Serialize(tables);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "list_tables failed");
            return $"Error: {ex.Message}";
        }
    }

    [Description("Get the column schema (name, type, nullable) for a database table.")]
    private async Task<string> GetTableSchemaAsync(
        [Description("The name of the table to inspect")] string tableName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Tool call: get_table_schema — {Table}", tableName);
        try
        {
            return await _db.GetTableSchemaAsync(tableName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "get_table_schema failed");
            return $"Error: {ex.Message}";
        }
    }
}
