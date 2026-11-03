using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Sso.Api.Features.Common;

/// <summary>An operation of a module: a scoped service the API endpoint and the MCP tool both call (ADR 0013).</summary>
[UsedImplicitly(
    ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature,
    ImplicitUseTargetFlags.WithInheritors
)]
public interface IHandler;

public static class EndpointExtensions
{
    /// <summary>The problems an endpoint may answer with, in the OpenAPI document (as ProblemDetails).</summary>
    public static RouteHandlerBuilder WithProblems(
        this RouteHandlerBuilder builder,
        params int[] statuses
    )
    {
        foreach (var status in statuses.Prepend(401).Prepend(500).Distinct())
            builder.Produces<ApiProblem>(status, "application/problem+json");
        return builder;
    }

    /// <summary>A list endpoint: its query parameters in the OpenAPI document, 400 for a wrong one.</summary>
    public static RouteHandlerBuilder WithList<TRow>(
        this RouteHandlerBuilder builder,
        ListSpec<TRow> spec
    ) =>
        builder
            .WithProblems(400)
            .AddOpenApiOperationTransformer(
                (operation, _, _) =>
                {
                    spec.Describe(operation);
                    return Task.CompletedTask;
                }
            );

    /// <summary>A single row of a list by id: 404 when the caller may not see it or it does not exist.</summary>
    public static async Task<IResult> OneAsync<TRow>(Task<ListPage<TRow>> page) =>
        (await page).Items is [var row] ? Results.Ok(row) : Results.NotFound();
}

/// <summary>Registers every <see cref="IHandler"/> of this assembly as a scoped service.</summary>
public static class HandlerRegistration
{
    public static IServiceCollection AddHandlers(this IServiceCollection services)
    {
        foreach (
            var type in typeof(IHandler)
                .Assembly.GetTypes()
                .Where(t =>
                    t is { IsClass: true, IsAbstract: false }
                    && typeof(IHandler).IsAssignableFrom(t)
                )
        )
            services.AddScoped(type);
        return services;
    }
}

/// <summary>Helpers of the handlers: the row version, a unique index a parallel request won.</summary>
public static class Handlers
{
    /// <summary>The row version a client holds (xmin as a number).</summary>
    public static long Version(uint xmin) => xmin;

    /// <summary>409 with the operation's sentence when the row changed since the client read it.</summary>
    public static void CheckVersion(uint current, long? expected, string message)
    {
        if (expected != current)
            throw Rule.Conflict("concurrency", message);
    }

    /// <summary>
    /// Saves; a unique index that a parallel request won between the check and the insert becomes the rule's own
    /// refusal (the constraint's name -> the rule), a stale row version the operation's 409.
    /// </summary>
    public static async Task SaveAsync(
        DbContext db,
        CancellationToken ct,
        string? staleMessage = null,
        IReadOnlyDictionary<string, RuleException>? unique = null
    )
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException) when (staleMessage is not null)
        {
            throw Rule.Conflict("concurrency", staleMessage);
        }
        catch (DbUpdateException e)
            when (e.InnerException
                    is PostgresException
                    {
                        SqlState: PostgresErrorCodes.UniqueViolation,
                        ConstraintName: { } constraint,
                    }
                && unique?.GetValueOrDefault(constraint) is { } rule
            )
        {
            throw rule;
        }
    }
}
