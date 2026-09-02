using Testcontainers.PostgreSql;
using Xunit;

namespace Skopka.Chat.Testing;

public sealed class PostgreSqlTestDatabase : IAsyncLifetime
{
    public const string ConnectionStringVariable = "SKOPKA_CHAT_POSTGRES";
    public const string RequiredVariable = "SKOPKA_CHAT_POSTGRES_REQUIRED";
    public const string TestcontainersVariable = "SKOPKA_CHAT_POSTGRES_TESTCONTAINERS";

    private const string PostgreSqlImage =
        "postgres:18.6-alpine3.24@sha256:d3e1620b530c944afa6e887d22eb899824da68e19c52024bf98f5220c88a65b2";
    private PostgreSqlContainer? _container;
    private string? _connectionString;

    public static async ValueTask<string> GetConnectionStringOrSkipAsync()
    {
        var database = await TestContext.Current.GetFixture<PostgreSqlTestDatabase>();
        return database is null
            ? throw new InvalidOperationException("The PostgreSQL assembly fixture was not registered.")
            : database.GetConnectionStringOrSkip();
    }

    public async ValueTask InitializeAsync()
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            _connectionString = configuredConnectionString;
            return;
        }

        if (!IsEnabled(TestcontainersVariable))
        {
            return;
        }

        _container = new PostgreSqlBuilder(PostgreSqlImage)
            .WithDatabase("skopka_chat_tests")
            .WithUsername("postgres")
            .WithPassword("skopka-chat-test-only")
            .Build();
        await _container.StartAsync();
        _connectionString = $"{_container.GetConnectionString()};Pooling=false";
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public async ValueTask<bool> RestartOwnedContainerAsync(CancellationToken cancellationToken = default)
    {
        if (_container is null) { return false; }
        await _container.StopAsync(cancellationToken);
        await _container.StartAsync(cancellationToken);
        _connectionString = $"{_container.GetConnectionString()};Pooling=false";
        return true;
    }

    public string GetConnectionStringOrSkip()
    {
        if (_connectionString is not null)
        {
            return _connectionString;
        }

        if (IsEnabled(RequiredVariable))
        {
            Assert.Fail(
                $"Set {ConnectionStringVariable} or enable {TestcontainersVariable} to run the required PostgreSQL gate.");
        }

        Assert.Skip(
            $"Set {ConnectionStringVariable} to a disposable database or enable {TestcontainersVariable} to run this integration test.");
        return null!;
    }

    private static bool IsEnabled(string variableName) =>
        bool.TryParse(Environment.GetEnvironmentVariable(variableName), out var enabled) && enabled;
}
