using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sso.Data;

/// <summary>
/// For dotnet ef (migrations add, script, has-pending-model-changes): the model without a database - the commands
/// that need one are not used, Liquibase applies the schema (ADR 0003).
/// </summary>
[UsedImplicitly]
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<SsoDbContext>
{
    public SsoDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SsoDbContext>();
        SsoData.Configure(options, null);
        return new SsoDbContext(options.Options);
    }
}
