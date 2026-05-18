namespace ChatAI.Models;

public record QueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);
