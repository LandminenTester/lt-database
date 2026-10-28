using System.Globalization;

namespace LtDatabase;

/// <summary>Portable column types, identical to lt-database-api (docs/SCHEMA.md).</summary>
public static class ColumnTypes
{
    public const string Int32 = "int32", Int64 = "int64", Decimal = "decimal", Float64 = "float64", Bool = "bool",
        String = "string", Text = "text", Json = "json", DateTime = "datetime", Date = "date", Uuid = "uuid", Bytes = "bytes";
}

public sealed record SqlColumn(string Name, string Type, int? Length = null, bool Nullable = false, bool HasDefault = false, bool AutoIncrement = false);

/// <summary>UniqueKeys lists the column sets of unique indexes (the primary key is separate).</summary>
public sealed record SqlTable(string Name, IReadOnlyList<SqlColumn> Columns, IReadOnlyList<string> PrimaryKey, IReadOnlyList<IReadOnlyList<string>>? UniqueKeys = null);

/// <summary>A request that cannot be served. Code and status mirror the lt-database-api error body.</summary>
public sealed class SqlRequestException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Collects positional bind parameters ($1, $2, ...) for one statement. Both Postgres and SQLite accept this form in re.sql.</summary>
public sealed class SqlParams
{
    public List<object?> Values { get; } = [];

    /// <summary>A null never becomes a parameter: it is written as the NULL literal, which needs no driver-specific null binding.</summary>
    public string Add(object? value)
    {
        if (value is null) return "NULL";
        Values.Add(value);
        return "$" + Values.Count.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>A finished statement and the values to bind to its placeholders.</summary>
public sealed record SqlStatement(string Sql, IReadOnlyList<object?> Parameters);
