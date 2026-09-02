using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Skopka.Chat.Bots.Tests;

public sealed class BotGatewayStartupTests
{
    [Fact]
    public async Task Explicit_initialization_preserves_device_across_process_restart_without_network()
    {
        var directory = Directory.CreateTempSubdirectory("skopka-bot-startup-").FullName;
        try
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=synthetic-gateway-startup", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(BotFixture.Now.AddDays(-1), BotFixture.Now.AddDays(1));
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var certificateFile = Path.Combine(directory, "synthetic.pfx");
            var passwordFile = Path.Combine(directory, "synthetic-password");
            await File.WriteAllBytesAsync(certificateFile, certificate.Export(X509ContentType.Pfx, password));
            await File.WriteAllTextAsync(passwordFile, password);
            var environment = new Dictionary<string, string>
            {
                ["Bot__ServiceId"] = "synthetic.example",
                ["Bot__UserId"] = Guid.NewGuid().ToString("D"),
                ["Bot__InstallationId"] = Guid.NewGuid().ToString("D"),
                ["Bot__Name"] = "Synthetic bot",
                ["Bot__OperatorId"] = "synthetic-operator",
                ["Bot__OperatorName"] = "Synthetic operator",
                ["Bot__Hosting"] = "OwnerHosted",
                ["Bot__Revision"] = Guid.NewGuid().ToString("D"),
                ["Bot__DataDirectory"] = Path.Combine(directory, "data"),
                ["Bot__CertificateFile"] = certificateFile,
                ["Bot__CertificatePasswordFile"] = passwordFile,
            };
            var first = await RunAsync(directory, environment, initialize: true);
            Assert.Equal(0, first.Code);
            Assert.StartsWith("Initialized device: ", first.Output, StringComparison.Ordinal);
            var second = await RunAsync(directory, environment, initialize: true);
            Assert.Equal(0, second.Code);
            Assert.Equal(first.Output, second.Output);
            var notConfigured = await RunAsync(directory, environment, initialize: false);
            Assert.Equal(1, notConfigured.Code);
            Assert.DoesNotContain(directory, notConfigured.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(password, notConfigured.Error, StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(directory, "*.keys", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(string directory,
        Dictionary<string, string> environment, bool initialize)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
        };
        // No inherited product configuration/credentials can direct this synthetic process to a real host.
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("Bot", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(key);
        }
        foreach (var pair in environment) { start.Environment[pair.Key] = pair.Value; }
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Skopka.Chat.BotGateway.dll"));
        if (initialize) { start.ArgumentList.Add("--initialize"); }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        return (process.ExitCode, await output, await error);
    }
}
