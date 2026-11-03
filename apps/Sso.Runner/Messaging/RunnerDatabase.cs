using Npgsql;

namespace Sso.Runner.Messaging;

/// <summary>The runner's connection, as sso_runner: the outbox and Wolverine's tables (ADR 0004).</summary>
public static class RunnerDatabase
{
    public const string ConnectionStringName = "sso";

    /// <summary>
    /// The connection string for both the runner's pool and Wolverine's own: no Kerberos - without
    /// GssEncryptionMode.Disable Npgsql probes GSS encryption first and logs a missing libgssapi_krb5 in the image.
    /// </summary>
    public static string Normalize(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "sso-runner",
            GssEncryptionMode = GssEncryptionMode.Disable,
        }.ConnectionString;
}
