using Npgsql;

namespace Sso.DbMigrator;

/// <summary>What the bootstrap sets up: names come from configuration, passwords are secrets (environment).</summary>
/// <param name="Logins">Role name to password: LOGIN for the roles db/changelog creates NOLOGIN.</param>
public sealed record BootstrapSettings(
    string Database,
    string Migrator,
    string MigratorPassword,
    IReadOnlyDictionary<string, string> Logins
);

/// <summary>
/// The server admin's part of a deployment, around Liquibase (ADR 0004). Idempotent: run again, it only resets the
/// passwords. Names go through format(%I / %L) in PostgreSQL itself - nothing is concatenated into SQL here. The admin
/// is a superuser locally (deploy/infrastructure.yml, the tests) or a managed server's admin with CREATEROLE and
/// CREATEDB but no superuser: PostgreSQL 16+ gives it only the roles it created itself, so everything inside the
/// database runs as the migrator (SET ROLE), which owns the database and every object and created the application
/// roles sso_api and sso_runner.
/// </summary>
public static class DatabaseBootstrap
{
    /// <summary>Before Liquibase: the migrator (LOGIN, CREATEROLE, never a superuser) and its database.</summary>
    public static async Task PrepareAsync(
        string adminConnection,
        BootstrapSettings settings,
        CancellationToken ct
    )
    {
        await using var connection = new NpgsqlConnection(adminConnection);
        await connection.OpenAsync(ct);
        await ExecuteFormattedAsync(
            connection,
            """
            SELECT format('CREATE ROLE %I LOGIN CREATEROLE PASSWORD %L', $1, $2)
            WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = $1)
            """,
            ct,
            settings.Migrator,
            settings.MigratorPassword
        );
        // No NOSUPERUSER here: only a superuser may name that attribute; the role is created without it.
        await ExecuteFormattedAsync(
            connection,
            "SELECT format('ALTER ROLE %I LOGIN CREATEROLE PASSWORD %L', $1, $2)",
            ct,
            settings.Migrator,
            settings.MigratorPassword
        );
        // CREATE DATABASE ... OWNER and SET ROLE need SET on the migrator: PostgreSQL 16+ gives its creator ADMIN only.
        // INHERIT FALSE - the admin never gets the migrator's privileges in its own sessions. A superuser has it already.
        await ExecuteFormattedAsync(
            connection,
            """
            SELECT format('GRANT %I TO CURRENT_USER WITH INHERIT FALSE, SET TRUE', $1)
            WHERE NOT pg_has_role(current_user, $1, 'SET')
            """,
            ct,
            settings.Migrator
        );
        await ExecuteFormattedAsync(
            connection,
            """
            SELECT format('CREATE DATABASE %I OWNER %I', $1, $2)
            WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = $1)
            """,
            ct,
            settings.Database,
            settings.Migrator
        );
        // On a shared server nobody else connects to this database: revoked as its owner; the admin keeps CONNECT for
        // finish, the application roles get it in finish (they exist only after Liquibase).
        await ExecuteFormattedAsync(
            connection,
            "SELECT format('SET ROLE %I', $1)",
            ct,
            settings.Migrator
        );
        await ExecuteFormattedAsync(
            connection,
            "SELECT format('REVOKE CONNECT, TEMPORARY ON DATABASE %I FROM PUBLIC', $1)",
            ct,
            settings.Database
        );
        await ExecuteFormattedAsync(
            connection,
            "SELECT format('GRANT CONNECT ON DATABASE %I TO %I', $1, session_user)",
            ct,
            settings.Database
        );
        await ExecuteFormattedAsync(connection, "SELECT 'RESET ROLE'", ct);
    }

    /// <summary>
    /// After Liquibase: LOGIN, a password and CONNECT for each application role (they must exist - db/changelog creates
    /// them) - as the migrator, which created those roles. Nobody has a password in Great SSO itself (ADR 0011).
    /// </summary>
    public static async Task FinishAsync(
        string adminConnection,
        BootstrapSettings settings,
        CancellationToken ct
    )
    {
        var target = new NpgsqlConnectionStringBuilder(adminConnection)
        {
            Database = settings.Database,
        }.ConnectionString;
        await using var connection = new NpgsqlConnection(target);
        await connection.OpenAsync(ct);
        await ExecuteFormattedAsync(
            connection,
            "SELECT format('SET ROLE %I', $1)",
            ct,
            settings.Migrator
        );
        foreach (var (role, password) in settings.Logins)
        {
            if (!await RoleExistsAsync(connection, role, ct))
                throw new InvalidOperationException(
                    $"Role {role} does not exist - run Liquibase (db/) before the logins."
                );
            await ExecuteFormattedAsync(
                connection,
                "SELECT format('ALTER ROLE %I LOGIN PASSWORD %L', $1, $2)",
                ct,
                role,
                password
            );
            await ExecuteFormattedAsync(
                connection,
                "SELECT format('GRANT CONNECT ON DATABASE %I TO %I', $1, $2)",
                ct,
                settings.Database,
                role
            );
        }
    }

    private static async Task<bool> RoleExistsAsync(
        NpgsqlConnection connection,
        string role,
        CancellationToken ct
    )
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = $1)",
            connection
        );
        command.Parameters.AddWithValue(role);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Lets PostgreSQL build the statement (format with %I and %L), then runs it - when there is one.</summary>
    private static async Task ExecuteFormattedAsync(
        NpgsqlConnection connection,
        string formatQuery,
        CancellationToken ct,
        params object[] values
    )
    {
        string? statement;
        await using (var format = new NpgsqlCommand(formatQuery, connection))
        {
            foreach (var value in values)
                format.Parameters.AddWithValue(value);
            statement = await format.ExecuteScalarAsync(ct) as string;
        }
        if (statement is null)
            return;
        await using var command = new NpgsqlCommand(statement, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}
