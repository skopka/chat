using Skopka.Chat.ServerTrusted.Blazor;

namespace Skopka.Chat.ServerTrusted.Blazor.Tests;

public sealed class AssemblyBoundaryTests
{
    [Fact]
    public void Blazor_assembly_references_server_trusted_core_but_no_e2ee_server_or_persistence_assembly()
    {
        var references = typeof(ServerTrustedChat).Assembly.GetReferencedAssemblies().Select(item => item.Name!).ToArray();

        Assert.Contains("Skopka.Chat.ServerTrusted", references);
        foreach (var forbidden in new[]
        {
            "Skopka.Chat.Server",
            "Skopka.Chat.Client",
            "Skopka.Chat.UI.Core",
            "Skopka.Chat.UI.Blazor",
            "Skopka.Chat.Persistence.PostgreSql",
            "Skopka.Chat.ServerTrusted.PostgreSql",
            "Microsoft.EntityFrameworkCore",
            "Npgsql",
            "Microsoft.AspNetCore.Http",
            "Microsoft.AspNetCore.Mvc.Core",
        })
        {
            Assert.DoesNotContain(forbidden, references);
        }

        Assert.DoesNotContain(references, name => name.StartsWith("Skopka.Chat.Client", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("NSec", StringComparison.Ordinal));
    }
}
