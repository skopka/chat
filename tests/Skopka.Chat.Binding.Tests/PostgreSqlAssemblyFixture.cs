using Skopka.Chat.Testing;

[assembly: AssemblyFixture(typeof(PostgreSqlTestDatabase))]
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
