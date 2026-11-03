using System.Globalization;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

namespace Sso.Api.Features.Common;

/// <summary>One page of a list: the rows, whether more follow, and the page asked for.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.Members)] // serialized to JSON
public sealed record ListPage<T>(IReadOnlyList<T> Items, bool HasMore, int Page, int PageSize);

/// <summary>What a list column allows in the query string.</summary>
[Flags]
public enum Filter
{
    None = 0,

    /// <summary><c>?name=value</c>.</summary>
    Eq = 1,

    /// <summary><c>?nameFrom=</c> (inclusive) and <c>?nameTo=</c> (exclusive).</summary>
    Range = 2,
}

/// <summary>
/// The query string of every list endpoint, the same for all of them (and for the generated client): page (from 1),
/// pageSize (default 50, at most 200), sort (a declared column, default the first one), desc, and the declared
/// filters. A value of the wrong type is a 400 naming the parameter. The columns are members of the row type, so
/// EF translates the filters, the order and the page into one query over the projection.
/// </summary>
public sealed class ListSpec<TRow>
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private readonly List<Column> _columns = [];

    private sealed record Column(
        string Name,
        LambdaExpression Member,
        Type Type,
        Filter Filter,
        bool Sortable
    );

    /// <summary>A column; the first one declared is the default order and the tie-breaker.</summary>
    public ListSpec<TRow> Add<T>(
        string name,
        Expression<Func<TRow, T>> member,
        Filter filter = Filter.None,
        bool sortable = true
    )
    {
        _columns.Add(new Column(name, member, typeof(T), filter, sortable));
        return this;
    }

    /// <summary>
    /// One page of <paramref name="rows"/> by the query string; <paramref name="complete"/> fills in what the query
    /// cannot (collections) for the rows of that page.
    /// </summary>
    public async Task<ListPage<TRow>> PageAsync(
        IQueryable<TRow> rows,
        IQueryCollection query,
        CancellationToken ct,
        Func<List<TRow>, CancellationToken, Task>? complete = null
    )
    {
        var page = Int(query, "page", 1, 1, int.MaxValue);
        var pageSize = Int(query, "pageSize", DefaultPageSize, 1, MaxPageSize);
        foreach (var column in _columns)
        {
            if (column.Filter.HasFlag(Filter.Eq) && Value(query, column, column.Name) is { } eq)
                rows = rows.Where(Predicate(column, eq, ExpressionType.Equal));
            if (column.Filter.HasFlag(Filter.Range))
            {
                if (Value(query, column, column.Name + "From") is { } from)
                    rows = rows.Where(Predicate(column, from, ExpressionType.GreaterThanOrEqual));
                if (Value(query, column, column.Name + "To") is { } to)
                    rows = rows.Where(Predicate(column, to, ExpressionType.LessThan));
            }
        }

        var sortKey = query["sort"].ToString();
        var sort =
            sortKey.Length == 0
                ? _columns[0]
                : _columns.FirstOrDefault(c =>
                    c.Sortable && c.Name.Equals(sortKey, StringComparison.OrdinalIgnoreCase)
                )
                    ?? throw Invalid(
                        "sort",
                        "One of: "
                            + string.Join(", ", _columns.Where(c => c.Sortable).Select(c => c.Name))
                            + "."
                    );
        var descending = query["desc"].ToString() is "true" or "1";
        var ordered = Order(rows, sort, descending, first: true);
        if (sort != _columns[0])
            ordered = Order(ordered, _columns[0], descending, first: false);

        var items = await ordered
            .Skip((int)Math.Min((long)(page - 1) * pageSize, int.MaxValue))
            .Take(pageSize + 1)
            .ToListAsync(ct);
        var hasMore = items.Count > pageSize;
        if (hasMore)
            items.RemoveAt(items.Count - 1);
        if (complete is not null && items.Count > 0)
            await complete(items, ct);
        return new ListPage<TRow>(items, hasMore, page, pageSize);
    }

    /// <summary>The list's query parameters in the OpenAPI document.</summary>
    public void Describe(OpenApiOperation operation)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(
            Parameter(
                "page",
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer,
                    Minimum = "1",
                    Default = 1,
                }
            )
        );
        operation.Parameters.Add(
            Parameter(
                "pageSize",
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer,
                    Minimum = "1",
                    Maximum = MaxPageSize.ToString(CultureInfo.InvariantCulture),
                    Default = DefaultPageSize,
                }
            )
        );
        var sortSchema = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [] };
        foreach (var column in _columns.Where(c => c.Sortable))
            sortSchema.Enum.Add(JsonValue.Create(column.Name));
        operation.Parameters.Add(Parameter("sort", sortSchema));
        operation.Parameters.Add(
            Parameter("desc", new OpenApiSchema { Type = JsonSchemaType.Boolean })
        );
        foreach (var column in _columns)
        {
            if (column.Filter.HasFlag(Filter.Eq))
                operation.Parameters.Add(Parameter(column.Name, Schema(column.Type)));
            if (column.Filter.HasFlag(Filter.Range))
            {
                operation.Parameters.Add(Parameter(column.Name + "From", Schema(column.Type)));
                operation.Parameters.Add(Parameter(column.Name + "To", Schema(column.Type)));
            }
        }
    }

    private static OpenApiParameter Parameter(string name, OpenApiSchema schema) =>
        new()
        {
            Name = name,
            In = ParameterLocation.Query,
            Required = false,
            Schema = schema,
        };

    private static OpenApiSchema Schema(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t switch
        {
            _ when t == typeof(Guid) => new() { Type = JsonSchemaType.String, Format = "uuid" },
            _ when t == typeof(DateTimeOffset) => new()
            {
                Type = JsonSchemaType.String,
                Format = "date-time",
            },
            _ when t == typeof(bool) => new() { Type = JsonSchemaType.Boolean },
            _ when t == typeof(int) => new() { Type = JsonSchemaType.Integer, Format = "int32" },
            _ when t == typeof(long) || t == typeof(uint) => new()
            {
                Type = JsonSchemaType.Integer,
                Format = "int64",
            },
            _ => new() { Type = JsonSchemaType.String },
        };
    }

    private static int Int(IQueryCollection query, string key, int fallback, int min, int max)
    {
        var raw = query[key].ToString();
        if (raw.Length == 0)
            return fallback;
        return
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value >= min
            && value <= max
            ? value
            : throw Invalid(key, $"A whole number from {min} to {max}.");
    }

    private static object? Value(IQueryCollection query, Column column, string key)
    {
        var raw = query[key].ToString();
        if (raw.Length == 0)
            return null;
        var t = Nullable.GetUnderlyingType(column.Type) ?? column.Type;
        object? value = t switch
        {
            _ when t == typeof(string) => raw,
            _ when t == typeof(Guid) => Guid.TryParse(raw, out var g) ? g : null,
            _ when t == typeof(bool) => bool.TryParse(raw, out var b) ? b : null,
            _ when t == typeof(int) => int.TryParse(
                raw,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var i
            )
                ? i
                : null,
            _ when t == typeof(long) => long.TryParse(
                raw,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var l
            )
                ? l
                : null,
            _ when t == typeof(uint) => uint.TryParse(
                raw,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var u
            )
                ? u
                : null,
            _ when t == typeof(DateTimeOffset) => DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var d
            )
                ? d.ToUniversalTime()
                : null,
            _ when t == typeof(IPAddress) => IPAddress.TryParse(raw, out var ip) ? ip : null,
            _ => null,
        };
        return value ?? throw Invalid(key, $"Expected {Describe(t)}.");
    }

    private static string Describe(Type t) =>
        t == typeof(Guid) ? "a UUID"
        : t == typeof(DateTimeOffset) ? "a date and time (ISO 8601)"
        : t == typeof(bool) ? "true or false"
        : t == typeof(IPAddress) ? "an IP address"
        : "a whole number";

    private static RuleException Invalid(string key, string message) =>
        new(400, "list.invalid_parameter", $"{key}: {message}")
        {
            Errors = new Dictionary<string, string[]> { [key] = [message] },
        };

    private static Expression<Func<TRow, bool>> Predicate(
        Column column,
        object value,
        ExpressionType comparison
    )
    {
        var parameter = column.Member.Parameters[0];
        var constant = Expression.Convert(Expression.Constant(value), column.Type);
        var body = Expression.MakeBinary(comparison, column.Member.Body, constant);
        return Expression.Lambda<Func<TRow, bool>>(body, parameter);
    }

    private static IOrderedQueryable<TRow> Order(
        IQueryable<TRow> rows,
        Column column,
        bool descending,
        bool first
    )
    {
        var method = (first, descending) switch
        {
            (true, false) => nameof(Queryable.OrderBy),
            (true, true) => nameof(Queryable.OrderByDescending),
            (false, false) => nameof(Queryable.ThenBy),
            (false, true) => nameof(Queryable.ThenByDescending),
        };
        var generic = typeof(Queryable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == method && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(TRow), column.Type);
        return (IOrderedQueryable<TRow>)generic.Invoke(null, [rows, column.Member])!;
    }
}

/// <summary>The query of a single row by id - the list's own filter, so the row looks the same.</summary>
public static class IdQuery
{
    public static IQueryCollection Of(object id) =>
        new QueryCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["id"] = id.ToString(),
            }
        );
}
