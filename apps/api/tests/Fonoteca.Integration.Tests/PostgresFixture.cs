using Fonoteca.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Fonoteca.Integration.Tests;

/// <summary>
/// A real PostgreSQL 18, started once and shared by every test in the collection.
/// </summary>
/// <remarks>
/// Deliberately not an in-memory or SQLite provider. The schema depends on
/// things only PostgreSQL has — <c>pg_trgm</c> GIN indexes, <c>jsonb</c>
/// columns, filtered indexes — so a migration that "passes" against a fake
/// provider proves nothing about whether it will apply in production.
///
/// On this host the container runtime is podman. Testcontainers speaks the
/// Docker API, and podman's user socket serves it; the constructor points
/// DOCKER_HOST at that socket when nothing else has set it. Enable it once with:
///
///     systemctl --user enable --now podman.socket
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Points Testcontainers at podman before anything asks it to.</summary>
    /// <remarks>
    /// <c>PostgreSqlBuilder.Build()</c> validates that a container runtime is
    /// reachable, and it runs as part of the field initialiser below — before
    /// <see cref="InitializeAsync"/> gets a chance to set DOCKER_HOST. Doing the
    /// discovery there set the variable long after the only code that reads it,
    /// so the whole suite failed with "Docker is either not running or
    /// misconfigured" on a host where podman was running perfectly. A static
    /// constructor is ordered before instance field initialisers, so this
    /// happens in time.
    /// </remarks>
    static PostgresFixture() => EnsureContainerRuntimeDiscoverable();

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("docker.io/library/postgres:18-alpine")
        .WithDatabase("fonoteca_test")
        .WithUsername("fonoteca")
        .WithPassword("fonoteca")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() =>
        await _container.StartAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

    public FonotecaDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<FonotecaDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new FonotecaDbContext(options);
    }

    /// <summary>
    /// Point Testcontainers at podman's socket when DOCKER_HOST is unset, so the
    /// suite runs on this host without per-developer environment setup.
    /// </summary>
    private static void EnsureContainerRuntimeDiscoverable()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return;
        }

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtimeDir))
        {
            return;
        }

        var socket = Path.Combine(runtimeDir, "podman", "podman.sock");
        if (File.Exists(socket))
        {
            Environment.SetEnvironmentVariable("DOCKER_HOST", $"unix://{socket}");

            // Podman does not serve Docker's Ryuk resource-reaper image, so
            // Testcontainers' automatic cleanup has to be disabled. Containers
            // are removed by DisposeAsync instead.
            Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
        }
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
