using FoodDelivery.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace FoodDelivery.Api.Tests.Infrastructure;

/// <summary>
/// Boots the real API in memory against a real PostgreSQL database.
/// Uses TEST_DB_CONNECTION if set (e.g. a local/CI database); otherwise
/// starts a throwaway PostgreSQL container with Testcontainers (needs Docker).
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Test-only signing key. Not a secret: it is never used outside the test run.
    public const string TestJwtKey = "test-signing-key-only-for-integration-tests-0123456789";

    private PostgreSqlContainer? _container;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION");
        if (!string.IsNullOrWhiteSpace(external))
        {
            _connectionString = external;
        }
        else
        {
            // Same major version as docker-compose.yml.
            _container = new PostgreSqlBuilder("postgres:16").Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }

        // Apply the real migrations, so broken migrations fail the test run too.
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing": skip appsettings.Development.json and user-secrets.
        builder.UseEnvironment("Testing");

        // UseSetting is applied early enough for values Program.cs reads while building.
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Jwt:Key", TestJwtKey);
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public class ApiCollection : ICollectionFixture<ApiFactory>
{
    // All integration test classes share one ApiFactory (one database start-up).
    public const string Name = "Api";
}
