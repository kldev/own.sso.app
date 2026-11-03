using Microsoft.EntityFrameworkCore;
using Npgsql;
using Sso.Data;

namespace Sso.Tests;

/// <summary>A DbContext of the tests' database as one of the application roles, without the host.</summary>
internal static class TestDb
{
    public static SsoDbContext Context(string connectionString)
    {
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var options = new DbContextOptionsBuilder<SsoDbContext>();
        SsoData.Configure(options, dataSource);
        return new SsoDbContext(options.Options);
    }

    /// <summary>Runs <paramref name="sql"/> and returns the SQLSTATE it failed with, or null when it worked.</summary>
    public static async Task<string?> SqlStateAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        try
        {
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException e)
        {
            return e.SqlState;
        }
    }
}
