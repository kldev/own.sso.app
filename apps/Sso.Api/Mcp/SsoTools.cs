using System.ComponentModel;
using JetBrains.Annotations;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sso.Api.Auth;
using Sso.Api.Features.Activity;
using Sso.Api.Features.Clients;
using Sso.Api.Features.People;

namespace Sso.Api.Mcp;

/// <summary>
/// Every operation of the panel as an MCP tool for Claude Code ("MCP everywhere", ADR 0013): people, applications,
/// the log and the sessions. Each tool is a thin call of the same handler as the panel's API endpoint - no rule lives
/// here. A new panel feature gets its tool here too. MCP tokens themselves are not a tool: a person issues them in
/// the panel.
/// </summary>
[McpServerToolType]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)] // Found and called by the MCP server.
public sealed class SsoTools(McpToolRunner run)
{
    // ---- people ----

    [McpServerTool(
        Name = "people_list",
        Title = "List people",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "The people who may sign in through Great SSO, with their username and work e-mail, role (admin or member), "
            + "status, provider e-mails and whether each provider account is linked yet (linkedAt), and the last sign-in."
    )]
    public Task<IReadOnlyList<PeoplePeopleRow>> PeopleList(
        [Description("Only this username (exact, lower case).")] string? username = null,
        [Description("active or inactive.")] string? status = null,
        [Description("admin or member.")] string? role = null,
        CancellationToken ct = default
    ) =>
        run.RunAsync(
            "people_list",
            async () =>
                (
                    await run.Handler<ListPeople>()
                        .HandleAsync(
                            run.User,
                            McpToolRunner.Query(
                                ("username", username?.Trim().ToLowerInvariant()),
                                ("status", status),
                                ("role", role),
                                ("sort", "displayName"),
                                ("pageSize", 200)
                            ),
                            ct
                        )
                ).Items,
            ct
        );

    [McpServerTool(
        Name = "person_add",
        Title = "Add a person",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false
    )]
    [Description(
        "Enters a person who may sign in - the only way anyone gets into Great SSO (no registration). At least one "
            + "provider e-mail: the person's first sign-in with that verified e-mail links the account. Applications "
            + "receive the username and the work e-mail; roles in an application come from the application's own account."
    )]
    public Task<PeoplePeopleRow> PersonAdd(
        [Description("Username: lower case letters, digits, dot, dash, underscore.")]
            string username,
        [Description("Work e-mail - what applications receive.")] string email,
        [Description("Full name.")] string displayName,
        [Description("member (signs in to applications) or admin (also runs the SSO panel).")]
            string role = "member",
        [Description("E-mail of the person's GitHub account (its primary, verified e-mail).")]
            string? githubEmail = null,
        [Description("E-mail of the person's Google account.")] string? googleEmail = null,
        [Description(
            "E-mail of the person's work Microsoft account (an allowed organisation only)."
        )]
            string? microsoftEmail = null,
        CancellationToken ct = default
    ) =>
        run.RunAsync(
            "person_add",
            async () =>
            {
                await run.Handler<CreatePerson>()
                    .HandleAsync(
                        run.User,
                        new PeoplePersonCreateRequest(
                            Guid.CreateVersion7(),
                            username,
                            email,
                            displayName,
                            role,
                            Blank(githubEmail),
                            Blank(googleEmail),
                            Blank(microsoftEmail)
                        ),
                        ct
                    );
                return await PersonAsync(username, ct) ?? throw NoPerson(username);
            },
            ct
        );

    [McpServerTool(
        Name = "person_update",
        Title = "Change a person",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "Changes a person's data. Only the arguments given change; an empty string removes a provider e-mail. Changing "
            + "a provider e-mail of a linked account removes the link at once (its sessions end) - this is how a person's "
            + "account is changed, at their request; the next sign-in with the new e-mail links the new account. Nobody "
            + "can take away their own admin role."
    )]
    public Task<PeoplePeopleRow> PersonUpdate(
        [Description("The person's current username.")] string username,
        [Description("A new username.")] string? newUsername = null,
        string? email = null,
        string? displayName = null,
        [Description("member or admin.")] string? role = null,
        string? githubEmail = null,
        string? googleEmail = null,
        string? microsoftEmail = null,
        CancellationToken ct = default
    ) =>
        run.RunAsync(
            "person_update",
            async () =>
            {
                var row = await PersonAsync(username, ct) ?? throw NoPerson(username);
                await run.Handler<UpdatePerson>()
                    .HandleAsync(
                        run.User,
                        new PeoplePersonUpdateRequest(
                            row.Id,
                            newUsername ?? row.Username,
                            email ?? row.Email,
                            displayName ?? row.DisplayName,
                            role ?? row.Role,
                            githubEmail is null ? row.GithubEmail : Blank(githubEmail),
                            googleEmail is null ? row.GoogleEmail : Blank(googleEmail),
                            microsoftEmail is null ? row.MicrosoftEmail : Blank(microsoftEmail),
                            row.RowVersion
                        ),
                        ct
                    );
                return await PersonAsync(newUsername ?? username, ct)
                    ?? throw NoPerson(newUsername ?? username);
            },
            ct
        );

    [McpServerTool(
        Name = "person_set_status",
        Title = "Activate or deactivate a person",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "active or inactive. Deactivating ends every session of the person and their applications' refresh tokens at once. Nobody can deactivate themselves."
    )]
    public Task<PeoplePeopleRow> PersonSetStatus(
        string username,
        [Description("active or inactive.")] string status,
        CancellationToken ct = default
    ) =>
        run.RunAsync(
            "person_set_status",
            async () =>
            {
                var row = await PersonAsync(username, ct) ?? throw NoPerson(username);
                await run.Handler<SetPersonStatus>()
                    .HandleAsync(
                        run.User,
                        new PeoplePersonSetStatusRequest(row.Id, status, row.RowVersion),
                        ct
                    );
                return await PersonAsync(username, ct) ?? throw NoPerson(username);
            },
            ct
        );

    [McpServerTool(
        Name = "person_unlink",
        Title = "Unlink a provider account",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "Removes the link of a person's account at one provider, keeping the e-mail: the account stops at once with its sessions; the next sign-in with the record's e-mail links an account again."
    )]
    public Task<PeoplePeopleRow> PersonUnlink(
        string username,
        [Description("github, google or microsoft.")] string provider,
        CancellationToken ct = default
    ) =>
        run.RunAsync(
            "person_unlink",
            async () =>
            {
                var row = await PersonAsync(username, ct) ?? throw NoPerson(username);
                await run.Handler<UnlinkPerson>()
                    .HandleAsync(run.User, new PeoplePersonUnlinkRequest(row.Id, provider), ct);
                return await PersonAsync(username, ct) ?? throw NoPerson(username);
            },
            ct
        );

    // ---- applications ----

    [McpServerTool(
        Name = "clients_list",
        Title = "List applications",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "The applications (OpenID Connect clients) that sign people in through Great SSO: client id, name, exact redirect URIs, accepted providers, status, last sign-in. Never a secret."
    )]
    public Task<IReadOnlyList<ClientsClientsRow>> ClientsList(CancellationToken ct = default) =>
        run.RunAsync(
            "clients_list",
            async () =>
                (
                    await run.Handler<ListClients>()
                        .HandleAsync(
                            run.User,
                            McpToolRunner.Query(("sort", "name"), ("pageSize", 200)),
                            ct
                        )
                ).Items,
            ct
        );

    [McpServerTool(
        Name = "client_register",
        Title = "Register an application",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false
    )]
    [Description(
        "Registers an application and returns its client secret ONCE - Great SSO keeps only its hash. Hand the secret "
            + "to the person to put in the application's environment (never into a repository). Redirect URIs match "
            + "exactly: https, or http only on localhost; ASP.NET Core's handler uses /signin-oidc (or its CallbackPath)."
    )]
    public Task<object> ClientRegister(
        [Description("Client id: lower case letters, digits, dot, dash, underscore.")]
            string clientId,
        string name,
        [Description("Exact redirect URIs (1-10).")] string[] redirectUris,
        [Description("Providers people may use for this application: github, google, microsoft.")]
            string[] providers,
        CancellationToken ct = default
    ) =>
        run.RunAsync<object>(
            "client_register",
            async () =>
            {
                var secret = Secrets.NewToken();
                await run.Handler<CreateClient>()
                    .HandleAsync(
                        run.User,
                        new ClientsClientCreateRequest(
                            Guid.CreateVersion7(),
                            clientId,
                            name,
                            redirectUris,
                            providers,
                            Secrets.HashText(secret)
                        ),
                        ct
                    );
                return new
                {
                    client = await ClientAsync(clientId, ct),
                    clientSecret = secret,
                    note = "Shown once. Great SSO keeps only its hash.",
                };
            },
            ct
        );

    [McpServerTool(
        Name = "client_update",
        Title = "Change an application",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "Changes an application's name, redirect URIs or providers; only the arguments given change. The client id never changes."
    )]
    public Task<ClientsClientsRow> ClientUpdate(
        string clientId,
        string? name = null,
        string[]? redirectUris = null,
        string[]? providers = null,
        CancellationToken ct = default
    ) =>
        run.RunAsync(
            "client_update",
            async () =>
            {
                var row = await ClientAsync(clientId, ct) ?? throw NoClient(clientId);
                await run.Handler<UpdateClient>()
                    .HandleAsync(
                        run.User,
                        new ClientsClientUpdateRequest(
                            row.Id,
                            name ?? row.Name,
                            redirectUris ?? row.RedirectUris,
                            providers ?? row.Providers,
                            row.RowVersion
                        ),
                        ct
                    );
                return await ClientAsync(clientId, ct) ?? throw NoClient(clientId);
            },
            ct
        );

    [McpServerTool(
        Name = "client_new_secret",
        Title = "New secret for an application",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false
    )]
    [Description(
        "Gives an application a new client secret and returns it ONCE; the old secret stops working at once."
    )]
    public Task<object> ClientNewSecret(string clientId, CancellationToken ct = default) =>
        run.RunAsync<object>(
            "client_new_secret",
            async () =>
            {
                var row = await ClientAsync(clientId, ct) ?? throw NoClient(clientId);
                var secret = Secrets.NewToken();
                await run.Handler<SetClientSecret>()
                    .HandleAsync(
                        run.User,
                        new ClientsClientSetSecretRequest(
                            row.Id,
                            Secrets.HashText(secret),
                            row.RowVersion
                        ),
                        ct
                    );
                return new
                {
                    clientId = row.ClientId,
                    clientSecret = secret,
                    note = "Shown once. Great SSO keeps only its hash.",
                };
            },
            ct
        );

    [McpServerTool(
        Name = "client_set_status",
        Title = "Switch an application on or off",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "active or inactive. Switched off, the application gets no codes and no tokens and its refresh tokens stop."
    )]
    public Task<ClientsClientsRow> ClientSetStatus(
        string clientId,
        [Description("active or inactive.")] string status,
        CancellationToken ct = default
    ) =>
        run.RunAsync(
            "client_set_status",
            async () =>
            {
                var row = await ClientAsync(clientId, ct) ?? throw NoClient(clientId);
                await run.Handler<SetClientStatus>()
                    .HandleAsync(
                        run.User,
                        new ClientsClientSetStatusRequest(row.Id, status, row.RowVersion),
                        ct
                    );
                return await ClientAsync(clientId, ct) ?? throw NoClient(clientId);
            },
            ct
        );

    // ---- activity ----

    [McpServerTool(
        Name = "events_list",
        Title = "Read the sign-in log",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "The full log, newest first: sign_in (success/refused with the reason), account_linked, session_started, "
            + "session_extended, session_expired, session_ended, code_issued, tokens_issued, token_refreshed, token and "
            + "authorize refusals, link_removed, mcp_tool, mcp_token_issued/revoked. Never a token, code or secret."
    )]
    public Task<IReadOnlyList<ActivityEventsRow>> EventsList(
        string? username = null,
        string? clientId = null,
        [Description("One event type, e.g. sign_in.")] string? eventType = null,
        [Description("success or refused.")] string? outcome = null,
        [Description("How many, 1-200 (50 by default).")] int limit = 50,
        CancellationToken ct = default
    ) =>
        run.RunAsync(
            "events_list",
            async () =>
                (
                    await run.Handler<ListEvents>()
                        .HandleAsync(
                            run.User,
                            McpToolRunner.Query(
                                ("username", username?.Trim().ToLowerInvariant()),
                                ("clientId", clientId?.Trim().ToLowerInvariant()),
                                ("eventType", eventType),
                                ("outcome", outcome),
                                ("sort", "id"),
                                ("desc", true),
                                ("pageSize", Math.Clamp(limit, 1, 200))
                            ),
                            ct
                        )
                ).Items,
            ct
        );

    [McpServerTool(
        Name = "sessions_list",
        Title = "List SSO sessions",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "SSO sessions, newest first: person, provider, start, last use, end (14 days idle, 30 at most), the applications signed in to, state (active, ended, expired) and why it ended."
    )]
    public Task<IReadOnlyList<ActivitySessionsRow>> SessionsList(
        string? username = null,
        [Description("active, ended or expired.")] string? state = null,
        [Description("How many, 1-200 (50 by default).")] int limit = 50,
        CancellationToken ct = default
    ) =>
        run.RunAsync(
            "sessions_list",
            async () =>
                (
                    await run.Handler<ListSessions>()
                        .HandleAsync(
                            run.User,
                            McpToolRunner.Query(
                                ("username", username?.Trim().ToLowerInvariant()),
                                ("state", state),
                                ("sort", "createdAt"),
                                ("desc", true),
                                ("pageSize", Math.Clamp(limit, 1, 200))
                            ),
                            ct
                        )
                ).Items,
            ct
        );

    [McpServerTool(
        Name = "session_end",
        Title = "End a session",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false
    )]
    [Description(
        "Ends an SSO session at once: the browser has to sign in again and the applications' refresh tokens of that session stop."
    )]
    public Task<object> SessionEnd(Guid sessionId, CancellationToken ct = default) =>
        run.RunAsync<object>(
            "session_end",
            async () =>
            {
                await run.Handler<EndSession>()
                    .HandleAsync(run.User, new ActivitySessionEndRequest(sessionId), ct);
                return new { sessionId, state = "ended" };
            },
            ct
        );

    // ---- helpers ----

    private async Task<PeoplePeopleRow?> PersonAsync(string username, CancellationToken ct) =>
        (
            await run.Handler<ListPeople>()
                .HandleAsync(
                    run.User,
                    McpToolRunner.Query(("username", username.Trim().ToLowerInvariant())),
                    ct
                )
        ).Items.SingleOrDefault();

    private async Task<ClientsClientsRow?> ClientAsync(string clientId, CancellationToken ct) =>
        (
            await run.Handler<ListClients>()
                .HandleAsync(
                    run.User,
                    McpToolRunner.Query(("clientId", clientId.Trim().ToLowerInvariant())),
                    ct
                )
        ).Items.SingleOrDefault();

    /// <summary>An empty string removes a value; null keeps it.</summary>
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static McpException NoPerson(string username) =>
        new($"There is no person with the username '{username}' (people_list shows them).");

    private static McpException NoClient(string clientId) =>
        new($"There is no application with the client id '{clientId}' (clients_list shows them).");
}
