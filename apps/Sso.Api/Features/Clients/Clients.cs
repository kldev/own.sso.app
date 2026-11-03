using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Sso.Api.Features.Common;
using Sso.Data;
using Sso.Data.Identity;
using Sso.Data.Oidc;

namespace Sso.Api.Features.Clients;

/// <summary>An application that signs people in through Great SSO - never the secret or its hash.</summary>
public sealed class ClientsClientsRow
{
    public required Guid Id { get; init; }
    public required string ClientId { get; init; }
    public required string Name { get; init; }
    public required string[] RedirectUris { get; init; }
    public required string[] Providers { get; init; }
    public required bool RequirePkce { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset SecretSetAt { get; init; }
    public DateTimeOffset? LastSignInAt { get; init; }

    [JsonIgnore]
    public uint Xmin { get; init; }

    public long RowVersion => Xmin;
}

/// <summary>GET /api/clients/clients: the registered applications - for an active SSO admin only.</summary>
public sealed class ListClients(SsoDbContext db, Actors actors) : IHandler
{
    public static readonly ListSpec<ClientsClientsRow> Spec = new ListSpec<ClientsClientsRow>()
        .Add("id", r => r.Id, Filter.Eq)
        .Add("clientId", r => r.ClientId, Filter.Eq)
        .Add("name", r => r.Name)
        .Add("requirePkce", r => r.RequirePkce, Filter.Eq)
        .Add("status", r => r.Status, Filter.Eq)
        .Add("createdAt", r => r.CreatedAt, Filter.Range)
        .Add("secretSetAt", r => r.SecretSetAt, Filter.Range)
        .Add("lastSignInAt", r => r.LastSignInAt, Filter.Range)
        .Add("rowVersion", r => r.Xmin, Filter.Eq);

    public async Task<ListPage<ClientsClientsRow>> HandleAsync(
        ClaimsPrincipal user,
        IQueryCollection query,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        var visible = actor.IsAdmin;
        var rows = db
            .Clients.AsNoTracking()
            .Where(_ => visible)
            .Select(c => new ClientsClientsRow
            {
                Id = c.Id,
                ClientId = c.ClientId,
                Name = c.Name,
                RedirectUris = c.RedirectUris,
                Providers = c.Providers,
                RequirePkce = c.RequirePkce,
                Status = c.Status,
                CreatedAt = c.CreatedAt,
                SecretSetAt = c.SecretSetAt,
                LastSignInAt = db
                    .Events.Where(e => e.ClientId == c.ClientId && e.EventType == "tokens_issued")
                    .Max(e => (DateTimeOffset?)e.OccurredAt),
                Xmin = c.RowVersion,
            });
        return await Spec.PageAsync(rows, query, ct);
    }
}

/// <summary>The rules of an application's record (ADR 0008).</summary>
internal static partial class ClientRules
{
    public const string Forbidden = "clients.forbidden";
    private const string NotFound = "clients.not_found";
    public const string Stale =
        "Someone changed this application in the meantime - reload and try again.";

    public static string Name(string? name)
    {
        var value = Text.Trim(name);
        return string.IsNullOrEmpty(value) || value.Length > 200
            ? throw Rule.Invalid(
                "clients.name_invalid",
                "Enter the application's name (up to 200 characters)."
            )
            : value;
    }

    /// <summary>1-10 absolute URLs without a fragment: https, or http only on the loopback (RFC 9700) - exact matches.</summary>
    public static string[] RedirectUris(string[]? uris)
    {
        var values = (uris ?? [])
            .Select(u => u.Trim())
            .Where(u => u.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return
            values.Length is < 1 or > 10
            || values.Any(u => u.Length > 2000 || !RedirectUri().IsMatch(u))
            ? throw Rule.Invalid(
                "clients.redirect_uris_invalid",
                "Redirect URIs: 1-10 absolute URLs without a fragment - https, or http only on localhost."
            )
            : values;
    }

    public static string[] ProviderList(string[]? providers)
    {
        var values = (providers ?? [])
            .Select(p => p.Trim().ToLowerInvariant())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return values.Length == 0 || values.Any(p => !ProviderNames.All.Contains(p))
            ? throw Rule.Invalid(
                "clients.providers_invalid",
                "Choose at least one provider: github, google or microsoft."
            )
            : values;
    }

    public static string SecretHash(string? hash) =>
        hash is not null && SecretHashPattern().IsMatch(hash)
            ? hash
            : throw Rule.Invalid(
                "clients.secret_hash_invalid",
                "The secret's hash is not base64url SHA-256."
            );

    [GeneratedRegex(
        @"^(https://[^/\s#?]+|http://(localhost|127\.0\.0\.1|\[::1\])(:[0-9]{1,5})?)(/[^\s#]*)?$"
    )]
    private static partial Regex RedirectUri();

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex SecretHashPattern();

    public static async Task<RegisteredClient> LoadAsync(
        SsoDbContext db,
        Guid? id,
        long? rowVersion,
        CancellationToken ct
    )
    {
        var client =
            await db.Clients.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw Rule.NotFound(NotFound, "There is no such application.");
        Handlers.CheckVersion(client.RowVersion, rowVersion, Stale);
        db.Entry(client).Property(c => c.RowVersion).OriginalValue = (uint)rowVersion!.Value;
        return client;
    }
}

public sealed record ClientsClientCreateRequest(
    Guid? Id,
    string? ClientId,
    string? Name,
    string[]? RedirectUris,
    string[]? Providers,
    string? SecretHash
);

[UsedImplicitly(ImplicitUseTargetFlags.Members)] // serialized to JSON
public sealed record ClientsClientCreateResult(long RowVersion);

/// <summary>
/// POST /api/clients/client-create: the SSO admin registers an application - its client id, name, the exact redirect
/// URIs and the providers it lets people sign in with. The page generates the secret, shows it once and sends only
/// base64url(SHA-256(secret)). The same id with the same values is a no-op.
/// </summary>
public sealed class CreateClient(SsoDbContext db, Actors actors, TimeProvider time) : IHandler
{
    private static readonly RuleException ClientIdTaken = Rule.Conflict(
        "clients.client_id_taken",
        "This client id is taken."
    );

    public async Task<ClientsClientCreateResult> HandleAsync(
        ClaimsPrincipal user,
        ClientsClientCreateRequest r,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        actor.RequireAdmin(ClientRules.Forbidden, "Only an SSO admin registers applications.");
        if (r.Id is not { } id || id == Guid.Empty)
            throw Rule.Invalid("clients.id_missing", "The application's id is missing.");
        var clientId = Text.Lower(r.ClientId);
        if (!Text.IsName(clientId))
            throw Rule.Invalid(
                "clients.client_id_invalid",
                "Client id: 2-64 characters - lower-case letters, digits, dot, dash or underscore."
            );
        var name = ClientRules.Name(r.Name);
        var uris = ClientRules.RedirectUris(r.RedirectUris);
        var providers = ClientRules.ProviderList(r.Providers);
        var secretHash = ClientRules.SecretHash(r.SecretHash);

        var existing = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (existing is not null)
            return
                existing.ClientId == clientId
                && existing.Name == name
                && existing.RedirectUris.SequenceEqual(uris)
                && existing.Providers.SequenceEqual(providers)
                ? new ClientsClientCreateResult(Handlers.Version(existing.RowVersion))
                : throw Rule.Conflict(
                    "clients.id_taken",
                    "This id already belongs to another application."
                );
        if (await db.Clients.AnyAsync(c => c.ClientId == clientId, ct))
            throw ClientIdTaken;

        var now = time.GetUtcNow();
        var client = new RegisteredClient
        {
            Id = id,
            ClientId = clientId!,
            Name = name,
            SecretHash = secretHash,
            RedirectUris = uris,
            Providers = providers,
            RequirePkce = true,
            Status = Statuses.Active,
            CreatedAt = now,
            SecretSetAt = now,
        };
        db.Clients.Add(client);
        await Handlers.SaveAsync(
            db,
            ct,
            unique: new Dictionary<string, RuleException>
            {
                ["ix_clients_client_id"] = ClientIdTaken,
            }
        );
        return new ClientsClientCreateResult(Handlers.Version(client.RowVersion));
    }
}

public sealed record ClientsClientUpdateRequest(
    Guid? Id,
    string? Name,
    string[]? RedirectUris,
    string[]? Providers,
    long? RowVersion
);

[UsedImplicitly(ImplicitUseTargetFlags.Members)] // serialized to JSON
public sealed record ClientsClientUpdateResult(long RowVersion);

/// <summary>
/// POST /api/clients/client-update: the SSO admin changes an application's name, redirect URIs or providers. The
/// client id stays (applications are configured with it).
/// </summary>
public sealed class UpdateClient(SsoDbContext db, Actors actors) : IHandler
{
    public async Task<ClientsClientUpdateResult> HandleAsync(
        ClaimsPrincipal user,
        ClientsClientUpdateRequest r,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        actor.RequireAdmin(ClientRules.Forbidden, "Only an SSO admin changes applications.");
        var client = await ClientRules.LoadAsync(db, r.Id, r.RowVersion, ct);
        client.Name = ClientRules.Name(r.Name);
        client.RedirectUris = ClientRules.RedirectUris(r.RedirectUris);
        client.Providers = ClientRules.ProviderList(r.Providers);
        await Handlers.SaveAsync(db, ct, ClientRules.Stale);
        return new ClientsClientUpdateResult(Handlers.Version(client.RowVersion));
    }
}

public sealed record ClientsClientSetSecretRequest(Guid? Id, string? SecretHash, long? RowVersion);

[UsedImplicitly(ImplicitUseTargetFlags.Members)] // serialized to JSON
public sealed record ClientsClientSetSecretResult(long RowVersion);

/// <summary>
/// POST /api/clients/client-set-secret: the SSO admin gives an application a new secret (the page generates it, shows
/// it once, sends its hash). The old secret stops working at once.
/// </summary>
public sealed class SetClientSecret(SsoDbContext db, Actors actors, TimeProvider time) : IHandler
{
    public async Task<ClientsClientSetSecretResult> HandleAsync(
        ClaimsPrincipal user,
        ClientsClientSetSecretRequest r,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        actor.RequireAdmin(
            ClientRules.Forbidden,
            "Only an SSO admin changes an application's secret."
        );
        var client = await ClientRules.LoadAsync(db, r.Id, r.RowVersion, ct);
        client.SecretHash = ClientRules.SecretHash(r.SecretHash);
        client.SecretSetAt = time.GetUtcNow();
        await Handlers.SaveAsync(db, ct, ClientRules.Stale);
        return new ClientsClientSetSecretResult(Handlers.Version(client.RowVersion));
    }
}

public sealed record ClientsClientSetStatusRequest(Guid? Id, string? Status, long? RowVersion);

[UsedImplicitly(ImplicitUseTargetFlags.Members)] // serialized to JSON
public sealed record ClientsClientSetStatusResult(long RowVersion);

/// <summary>
/// POST /api/clients/client-set-status: the SSO admin switches an application off or on. Switched off, it gets no
/// codes and no tokens, and its refresh tokens stop working.
/// </summary>
public sealed class SetClientStatus(SsoDbContext db, Actors actors) : IHandler
{
    public async Task<ClientsClientSetStatusResult> HandleAsync(
        ClaimsPrincipal user,
        ClientsClientSetStatusRequest r,
        CancellationToken ct
    )
    {
        var actor = await actors.RequireAsync(user, ct);
        actor.RequireAdmin(ClientRules.Forbidden, "Only an SSO admin switches applications.");
        var client = await ClientRules.LoadAsync(db, r.Id, r.RowVersion, ct);
        var status = Text.Trim(r.Status);
        if (status is null || !Statuses.All.Contains(status))
            throw Rule.Invalid("clients.status_invalid", "The status is active or inactive.");
        if (client.Status != status)
        {
            client.Status = status;
            await Handlers.SaveAsync(db, ct, ClientRules.Stale);
        }
        return new ClientsClientSetStatusResult(Handlers.Version(client.RowVersion));
    }
}
