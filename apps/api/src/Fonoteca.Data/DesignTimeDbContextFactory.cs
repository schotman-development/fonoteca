using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Fonoteca.Data;

/// <summary>
/// Builds a <see cref="FonotecaDbContext"/> for the <c>dotnet ef</c> tooling.
/// </summary>
/// <remarks>
/// Without this, EF's design-time services try to construct the API's host to
/// find the context — which means <c>dotnet ef migrations add</c> would run the
/// startup path, including applying migrations to a live database. Generating a
/// migration should never touch a real server, so the tooling gets its own
/// minimal, offline configuration instead.
///
/// The connection string here is only used to pick the provider and its
/// version-specific SQL generation. Nothing connects.
/// </remarks>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FonotecaDbContext>
{
    private const string DesignTimeConnection =
        "Host=localhost;Port=5432;Database=fonoteca_designtime;Username=postgres;Password=postgres";

    public FonotecaDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("FONOTECA_DESIGNTIME_CONNECTION")
            ?? DesignTimeConnection;

        var options = new DbContextOptionsBuilder<FonotecaDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(
                typeof(FonotecaDbContext).Assembly.FullName))
            .Options;

        return new FonotecaDbContext(options);
    }
}
