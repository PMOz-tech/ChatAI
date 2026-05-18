using System.Text.Json;
using System.Text.RegularExpressions;
using ChatAI.Interfaces;
using ChatAI.Models;
using Microsoft.Data.Sqlite;

namespace ChatAI.Services;

public sealed class DatabaseService : IDatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(IConfiguration configuration, ILogger<DatabaseService> logger)
    {
        _connectionString = configuration.GetConnectionString("Sqlite") ?? "Data Source=chatai.db";
        _logger = logger;
    }

    public async Task<QueryResult> ExecuteSelectAsync(string sql, CancellationToken ct = default)
    {
        if (!sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only SELECT queries are permitted.");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(ct);

        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(i => reader.GetName(i))
            .ToList();

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        _logger.LogDebug("SQL returned {Rows} row(s): {Sql}", rows.Count, sql);
        return new QueryResult(columns, rows);
    }

    public async Task<IReadOnlyList<string>> GetTablesAsync(CancellationToken ct = default)
    {
        var result = await ExecuteSelectAsync(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name", ct);
        return result.Rows.Select(r => r["name"]?.ToString() ?? string.Empty).ToList();
    }

    public async Task<string> GetTableSchemaAsync(string tableName, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(tableName, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
            throw new InvalidOperationException($"Invalid table name: '{tableName}'");

        var result = await ExecuteSelectAsync($"PRAGMA table_info({tableName})", ct);
        return JsonSerializer.Serialize(result.Rows);
    }
}
