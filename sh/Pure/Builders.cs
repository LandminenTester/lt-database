using System.Text;

namespace LtDatabase;

/// <summary>Filter builders. Each returns JSON text for the "filter" part of a request.</summary>
public static class F
{
    static string Cond(string field, string op, object? value) => JsonText.Object(("field", field), ("op", op), ("value", value));

    public static string Eq(string field, object? value) => Cond(field, "eq", value);
    public static string Ne(string field, object? value) => Cond(field, "ne", value);
    public static string Gt(string field, object? value) => Cond(field, "gt", value);
    public static string Gte(string field, object? value) => Cond(field, "gte", value);
    public static string Lt(string field, object? value) => Cond(field, "lt", value);
    public static string Lte(string field, object? value) => Cond(field, "lte", value);
    public static string In(string field, params object?[] values) => Cond(field, "in", new JsonText.Raw(JsonText.Array(values)));
    public static string NotIn(string field, params object?[] values) => Cond(field, "nin", new JsonText.Raw(JsonText.Array(values)));
    public static string IsNull(string field, bool isNull = true) => Cond(field, "isNull", isNull);
    public static string StartsWith(string field, string text) => Cond(field, "startsWith", text);
    public static string Contains(string field, string text) => Cond(field, "contains", text);
    public static string And(params string[] filters) => "{\"and\":[" + string.Join(",", filters) + "]}";
    public static string Or(params string[] filters) => "{\"or\":[" + string.Join(",", filters) + "]}";
    public static string Not(string filter) => "{\"not\":" + filter + "}";
}

/// <summary>Row, sort and key helpers.</summary>
public static class Q
{
    /// <summary>A row or a "set"/"inc" object: Q.Row(("name", "alice"), ("score", 10)).</summary>
    public static string Row(params (string Column, object? Value)[] values) => JsonText.Object(values);

    /// <summary>
    /// Marks one write as "durability":"immediate", which makes the API run it live even in a namespace it buffers (write-behind).
    /// The text is spliced into the object before its closing brace, so no JSON has to be parsed.
    /// </summary>
    public static string Immediate(string payloadJson)
    {
        var end = payloadJson.TrimEnd();
        if (end.Length < 2 || end[^1] != '}') throw new ArgumentException("The payload must be a JSON object.", nameof(payloadJson));
        var body = end[..^1].TrimEnd();
        return body + (body.EndsWith('{') ? "" : ",") + "\"durability\":\"immediate\"}";
    }

    public static string Sort(params (string Column, bool Descending)[] order) =>
        "[" + string.Join(",", order.Select(o => JsonText.Object(("field", o.Column), ("dir", o.Descending ? "desc" : "asc")))) + "]";
}

/// <summary>Builds the schema JSON for <c>schema/sync</c>.</summary>
public static class S
{
    public static string Schema(params string[] tables) => "{\"tables\":[" + string.Join(",", tables) + "]}";

    public static string Table(string name, string[] primaryKey, string[] columns, string[]? indexes = null, string? renamedFrom = null)
    {
        var sb = new StringBuilder("{");
        sb.Append("\"name\":").Append(JsonText.Quote(name));
        if (renamedFrom is not null) sb.Append(",\"renamedFrom\":").Append(JsonText.Quote(renamedFrom));
        sb.Append(",\"primaryKey\":").Append(JsonText.Array(primaryKey));
        sb.Append(",\"columns\":[").Append(string.Join(",", columns)).Append(']');
        if (indexes is { Length: > 0 }) sb.Append(",\"indexes\":[").Append(string.Join(",", indexes)).Append(']');
        return sb.Append('}').ToString();
    }

    public static string Index(string name, bool unique, params string[] columns) =>
        JsonText.Object(("name", name), ("columns", new JsonText.Raw(JsonText.Array(columns))), ("unique", unique));

    /// <param name="type">int32, int64, decimal, float64, bool, string, text, json, datetime, date, uuid or bytes.</param>
    public static string Column(string name, string type, int? length = null, int? precision = null, int? scale = null, bool nullable = false,
        bool unique = false, bool autoIncrement = false, object? @default = null, string? renamedFrom = null)
    {
        var parts = new List<(string, object?)> { ("name", name), ("type", type) };
        if (length is not null) parts.Add(("length", length));
        if (precision is not null) parts.Add(("precision", precision));
        if (scale is not null) parts.Add(("scale", scale));
        if (nullable) parts.Add(("nullable", true));
        if (unique) parts.Add(("unique", true));
        if (autoIncrement) parts.Add(("autoIncrement", true));
        if (@default is not null) parts.Add(("default", @default));
        if (renamedFrom is not null) parts.Add(("renamedFrom", renamedFrom));
        return JsonText.Object([.. parts]);
    }

    public static string Int32(string name, bool nullable = false, object? @default = null, bool unique = false, string? renamedFrom = null) => Column(name, "int32", nullable: nullable, @default: @default, unique: unique, renamedFrom: renamedFrom);
    public static string Int64(string name, bool autoIncrement = false, bool nullable = false, object? @default = null, bool unique = false, string? renamedFrom = null) => Column(name, "int64", autoIncrement: autoIncrement, nullable: nullable, @default: @default, unique: unique, renamedFrom: renamedFrom);
    public static string Text(string name, int length = 255, bool nullable = false, bool unique = false, object? @default = null, string? renamedFrom = null) => Column(name, "string", length: length, nullable: nullable, unique: unique, @default: @default, renamedFrom: renamedFrom);
    public static string LongText(string name, bool nullable = false, string? renamedFrom = null) => Column(name, "text", nullable: nullable, renamedFrom: renamedFrom);
    public static string Bool(string name, object? @default = null, bool nullable = false, string? renamedFrom = null) => Column(name, "bool", nullable: nullable, @default: @default, renamedFrom: renamedFrom);
    public static string Decimal(string name, int precision = 18, int scale = 2, bool nullable = false, object? @default = null, string? renamedFrom = null) => Column(name, "decimal", precision: precision, scale: scale, nullable: nullable, @default: @default, renamedFrom: renamedFrom);
    public static string Double(string name, bool nullable = false, object? @default = null, string? renamedFrom = null) => Column(name, "float64", nullable: nullable, @default: @default, renamedFrom: renamedFrom);
    public static string Json(string name, bool nullable = true, string? renamedFrom = null) => Column(name, "json", nullable: nullable, renamedFrom: renamedFrom);
    public static string DateTime(string name, bool nullable = false, bool defaultNow = false, string? renamedFrom = null) => Column(name, "datetime", nullable: nullable, @default: defaultNow ? "now" : null, renamedFrom: renamedFrom);
    public static string Date(string name, bool nullable = false, string? renamedFrom = null) => Column(name, "date", nullable: nullable, renamedFrom: renamedFrom);
    public static string Uuid(string name, bool nullable = false, bool unique = false, string? renamedFrom = null) => Column(name, "uuid", nullable: nullable, unique: unique, renamedFrom: renamedFrom);
    public static string Bytes(string name, int? length = null, bool nullable = false, string? renamedFrom = null) => Column(name, "bytes", length: length, nullable: nullable, renamedFrom: renamedFrom);
}
