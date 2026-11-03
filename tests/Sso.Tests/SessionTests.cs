using System.Net;

namespace Sso.Tests;

/// <summary>
/// The SSO session's limits (ADR 0011): every use moves the idle end (14 days), never past the absolute one (30 days);
/// an admin's deactivation ends it at once. Time is moved by shifting the session's timestamps in the database.
/// </summary>
[Trait("verify", "sessions")]
[Collection(ApiCollection.Name)]
public sealed class SessionTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Use_on_day_13_extends_the_session()
    {
        var (client, session) = await api.SessionAsync(await api.AddPersonAsync(role: "admin"));
        await Shift(session, days: 13);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/platform/me", Ct)).StatusCode);
        // The idle end is now 14 days from now again.
        Assert.True(
            await api.ScalarAsync<bool>(
                $"SELECT idle_expires_at > now() + interval '13 days 23 hours' FROM oidc.sessions WHERE id = '{session}'"
            )
        );
        Assert.Equal(1L, await Count(session, "session_extended"));
    }

    [Fact]
    public async Task Fourteen_days_without_use_end_the_session()
    {
        var (client, session) = await api.SessionAsync(await api.AddPersonAsync(role: "admin"));
        await Shift(session, days: 14, seconds: 1);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/platform/me", Ct)).StatusCode
        );
        var page = await client.GetAsync("/admin", Ct);
        Assert.StartsWith("/sign-in?returnUrl=", page.Headers.Location!.OriginalString);
        Assert.Equal(
            "idle",
            await api.ScalarAsync<string>(
                $"SELECT end_reason::text FROM oidc.sessions WHERE id = '{session}'"
            )
        );
        Assert.Equal(1L, await Count(session, "session_expired"));
    }

    [Fact]
    public async Task Day_30_ends_the_session_even_with_daily_use()
    {
        var (client, session) = await api.SessionAsync(await api.AddPersonAsync(role: "admin"));
        // Used yesterday (idle end far away), started 30 days ago.
        await api.ExecuteAsync(
            $"""
            UPDATE oidc.sessions
            SET created_at = now() - interval '30 days', last_seen_at = now() - interval '1 day',
                idle_expires_at = now() + interval '13 days', absolute_expires_at = now() - interval '1 second'
            WHERE id = '{session}'
            """
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/platform/me", Ct)).StatusCode
        );
        Assert.Equal(
            "absolute",
            await api.ScalarAsync<string>(
                $"SELECT end_reason::text FROM oidc.sessions WHERE id = '{session}'"
            )
        );
    }

    [Fact]
    public async Task The_idle_end_never_passes_the_absolute_one()
    {
        var (client, session) = await api.SessionAsync(await api.AddPersonAsync(role: "admin"));
        await api.ExecuteAsync(
            $"""
            UPDATE oidc.sessions
            SET last_seen_at = now() - interval '1 hour', absolute_expires_at = now() + interval '2 days'
            WHERE id = '{session}'
            """
        );
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/platform/me", Ct)).StatusCode);
        Assert.True(
            await api.ScalarAsync<bool>(
                $"SELECT idle_expires_at = absolute_expires_at FROM oidc.sessions WHERE id = '{session}'"
            )
        );
    }

    [Fact]
    public async Task A_deactivated_person_loses_every_session_at_once()
    {
        var username = await api.AddPersonAsync(role: "admin");
        var (client, session) = await api.SessionAsync(username);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/platform/me", Ct)).StatusCode);

        var admin = await api.AsAsync(ApiFixture.Admin);
        var person = (await admin.ListAsync($"/api/people/people?username={username}"))[0]!;
        await admin.CallAsync(
            "/api/people/person-set-status",
            new
            {
                id = person["id"]!.GetValue<Guid>(),
                status = "inactive",
                rowVersion = person["rowVersion"]!.GetValue<long>(),
            }
        );

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/platform/me", Ct)).StatusCode
        );
        Assert.Equal(
            "user_inactive",
            await api.ScalarAsync<string>(
                $"SELECT end_reason::text FROM oidc.sessions WHERE id = '{session}'"
            )
        );
    }

    private Task Shift(Guid session, int days, int seconds = 0) =>
        api.ExecuteAsync(
            $"""
            UPDATE oidc.sessions
            SET created_at = created_at - make_interval(days => {days}, secs => {seconds}),
                last_seen_at = last_seen_at - make_interval(days => {days}, secs => {seconds}),
                idle_expires_at = idle_expires_at - make_interval(days => {days}, secs => {seconds}),
                absolute_expires_at = absolute_expires_at - make_interval(days => {days}, secs => {seconds})
            WHERE id = '{session}'
            """
        );

    private Task<long> Count(Guid session, string type) =>
        api.ScalarAsync<long>(
            $"SELECT count(*) FROM oidc.events WHERE session_id = '{session}' AND event_type = '{type}'"
        );
}
