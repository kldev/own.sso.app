using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Sso.Tests;

/// <summary>
/// The schema (ADR 0003, 0004): the database Liquibase made is the EF model - no pending model change, every migration
/// exported as a changeset whose SQL is what EF generates now, no EF history table - and there is no logic in the
/// database: no function, procedure, view, trigger or policy in the application's schemas.
/// </summary>
[Trait("verify", "schema")]
[Collection(ApiCollection.Name)]
public sealed partial class SchemaTests(ApiFixture api)
{
    private static readonly string[] Schemas =
    [
        "identity",
        "oidc",
        "audit",
        "marketing",
        "messaging",
        "wolverine",
    ];

    private static string InSchemas(string column) =>
        $"{column} IN ({string.Join(", ", Schemas.Select(s => $"'{s}'"))})";

    [Fact]
    public void The_model_has_no_pending_changes()
    {
        using var db = TestDb.Context(api.ConnectionString("sso", "sso_api"));
        Assert.False(
            db.Database.HasPendingModelChanges(),
            "The EF model changed without a migration: dotnet ef migrations add <Name>, then db/scripts/export-ef-migration.sh."
        );
    }

    [Fact]
    public void Every_migration_has_its_exported_changeset()
    {
        using var db = TestDb.Context(api.ConnectionString("sso", "sso_api"));
        var migrator = db.GetService<IMigrator>();
        var changesets = Directory
            .GetFiles(
                Path.Combine(DbDirectory(), "changelog"),
                "*.sql",
                SearchOption.AllDirectories
            )
            .Select(f => (File: f, Text: File.ReadAllText(f)))
            .ToList();
        string? previous = null;
        foreach (var id in db.Database.GetMigrations())
        {
            var changeset = changesets.SingleOrDefault(c =>
                c.Text.Contains($"--comment: ef-migration {id}\n", StringComparison.Ordinal)
            );
            Assert.True(
                changeset.File is not null,
                $"Migration {id} has no changeset - run db/scripts/export-ef-migration.sh."
            );
            var expected = migrator.GenerateScript(
                previous ?? Migration.InitialDatabase,
                id,
                MigrationsSqlGenerationOptions.NoTransactions
            );
            Assert.Equal(Normalise(WithoutHistory(expected)), Normalise(changeset.Text));
            previous = id;
        }
    }

    [Fact]
    public async Task There_is_no_ef_history_table()
    {
        Assert.Equal(
            0L,
            await api.ScalarAsync<long>(
                "SELECT count(*) FROM pg_class WHERE relname = '__EFMigrationsHistory'"
            )
        );
        Assert.True(
            await api.ScalarAsync<long>("SELECT count(*) FROM public.databasechangelog") > 0
        );
    }

    [Fact]
    public async Task There_are_no_routines_views_triggers_or_policies()
    {
        // Wolverine's storage (schema wolverine) is tables and sequences only, from its own dump.
        Assert.Equal(
            0L,
            await api.ScalarAsync<long>(
                $"SELECT count(*) FROM pg_proc AS p JOIN pg_namespace AS n ON n.oid = p.pronamespace WHERE {InSchemas("n.nspname")} OR n.nspname = 'public'"
            )
        );
        Assert.Equal(
            0L,
            await api.ScalarAsync<long>(
                $"SELECT count(*) FROM pg_views WHERE {InSchemas("schemaname")} OR schemaname = 'public'"
            )
        );
        Assert.Equal(
            0L,
            await api.ScalarAsync<long>(
                $"SELECT count(*) FROM pg_trigger AS t JOIN pg_class AS c ON c.oid = t.tgrelid JOIN pg_namespace AS n ON n.oid = c.relnamespace WHERE NOT t.tgisinternal AND {InSchemas("n.nspname")}"
            )
        );
        Assert.Equal(0L, await api.ScalarAsync<long>("SELECT count(*) FROM pg_policy"));
        Assert.Equal(
            0L,
            await api.ScalarAsync<long>(
                $"SELECT count(*) FROM pg_class AS c JOIN pg_namespace AS n ON n.oid = c.relnamespace WHERE c.relrowsecurity AND {InSchemas("n.nspname")}"
            )
        );
    }

    [Fact]
    public async Task Every_table_and_column_of_the_model_is_in_the_database()
    {
        await using var db = TestDb.Context(api.ConnectionString("sso", "sso_api"));
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            var schema = entity.GetSchema()!;
            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName(
                    Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(
                        table,
                        schema
                    )
                );
                Assert.True(
                    await api.ScalarAsync<bool>(
                        $"SELECT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = '{schema}.{table}'::regclass AND attname = '{column}' AND NOT attisdropped)"
                    ),
                    $"{schema}.{table}.{column} is in the model but not in the database."
                );
            }
        }
    }

    /// <summary>The statements of a script: no comments, no blank lines, no --rollback, trimmed.</summary>
    private static string Normalise(string sql) =>
        string.Join(
            "\n",
            sql.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal))
        );

    /// <summary>EF's history table never reaches the database (the export drops these statements).</summary>
    private static string WithoutHistory(string script) =>
        string.Join(
            "\n\n",
            BlankLines()
                .Split(script.Replace("\r\n", "\n"))
                .Where(b => b.Trim().Length > 0 && !b.Contains("__EFMigrationsHistory"))
        );

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex BlankLines();

    private static string DbDirectory()
    {
        for (
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            dir is not null;
            dir = dir.Parent
        )
            if (File.Exists(Path.Combine(dir.FullName, "db", "changelog.yaml")))
                return Path.Combine(dir.FullName, "db");
        throw new InvalidOperationException("db/changelog.yaml not found above the test output.");
    }
}
