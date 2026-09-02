using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Skopka.Chat.Bots;
using Skopka.Chat.Bots.AspNetCore;
using Skopka.Chat.Bots.Sqlite;
using Skopka.Chat.Client;
using Skopka.Chat.Client.Http;
using Skopka.Chat.Client.Storage.Sqlite;
using Skopka.Chat.Protocol;
using Skopka.Chat.BotGateway;

// This executable is a composition example, not a consent issuer or a production Auth service.
try
{
    var initialize = args.Contains("--initialize", StringComparer.Ordinal);
    var builder = WebApplication.CreateBuilder(args.Where(argument => argument != "--initialize").ToArray());
    builder.Logging.ClearProviders(); // Never send bodies, tokens, keys or remote exceptions to default logs.
    var config = builder.Configuration;
    string Required(string name) => config["Bot:" + name] ?? throw new InvalidOperationException("Required bot configuration is missing.");
    var scope = new DeviceIdentityScope(Required("ServiceId"), new UserId(Guid.Parse(Required("UserId"))), Guid.Parse(Required("InstallationId")));
    var profile = new ChatBotProfile(scope.UserId, Required("Name"), Required("OperatorId"), Required("OperatorName"),
        Enum.Parse<ChatBotHosting>(Required("Hosting")), Guid.Parse(Required("Revision")));
    var data = Path.GetFullPath(Required("DataDirectory"));
    Directory.CreateDirectory(data);
    using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Required("CertificateFile"),
        await SecretFiles.ReadAsync(Required("CertificatePasswordFile"), CancellationToken.None), X509KeyStorageFlags.EphemeralKeySet);
    if (!certificate.HasPrivateKey) { throw new InvalidOperationException("A protected key ring certificate is required."); }
    var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(data, "keyring")), options =>
        options.SetApplicationName("Skopka.Chat.BotGateway").ProtectKeysWithCertificate(certificate));
    var keys = new ProtectedFileBotIdentityStore(Path.Combine(data, "identity"), scope, protection);
    var identities = new PersistentDeviceIdentityService(keys, keys, TimeProvider.System);
    var loaded = initialize ? await identities.CreateAsync(scope) : await identities.LoadAsync(scope);
    if (loaded.State != PersistentDeviceIdentityState.Ready || loaded.Metadata?.PublicDevice is not { } device)
    {
        throw new InvalidOperationException("The identity needs explicit initialization or recovery.");
    }
    if (initialize)
    {
        // Public ID only. Initialization never registers a device or starts the HTTP listener.
        Console.WriteLine($"Initialized device: {device.DeviceId}");
        return;
    }

    using var chatHttp = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(Required("ChatBaseAddress")) };
    var transport = new SkopkaChatHttpClient(chatHttp, new FileTokenProvider(Required("ChatTokenFile")),
        Options.Create(new SkopkaChatHttpClientOptions { AuthenticatedUserId = scope.UserId.Value, AuthenticatedDeviceId = device.DeviceId.Value }), TimeProvider.System);
    // Explicit binding-v1 context belongs to the authenticated host, never inferred from a server challenge.
    var context = new DeviceAuthorizationContext(scope.ServiceId, scope.UserId, Required("SessionReference"),
        DateTimeOffset.Parse(Required("SessionExpiresAt"), System.Globalization.CultureInfo.InvariantCulture));
    var operation = Enum.Parse<DeviceBindingOperation>(Required("BindingOperation"));
    _ = await new DeviceBindingCoordinator(identities, new DeviceBindingProofService(keys, TimeProvider.System), transport)
        .BindAsync(scope, context, operation);
    using var consent = new HostConsentProvider(new Uri(Required("ConsentBaseAddress")), Required("ConsentTokenFile"));
    var partition = Path.Combine(data, scope.StoragePartition);
    Directory.CreateDirectory(partition);
    var inbox = new SqliteChatBotInbox($"Data Source={Path.Combine(partition, "bot-inbox.db")};Pooling=False", profile, device.DeviceId);
    using var outbox = new SqliteChatOutboxStore($"Data Source={Path.Combine(partition, "bot-outbox.db")};Pooling=False");
    using var runtime = new ChatBotRuntime(profile, device.DeviceId, transport, new ChatCryptoService(keys), transport, outbox, inbox, consent);
    builder.Services.AddSingleton(runtime);
    builder.Services.AddSingleton(new GatewayCredentials(Required("GatewayTokenFile"), scope.UserId));
    builder.Services.AddAuthentication("BotBearer").AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, GatewayAuthentication>("BotBearer", _ => { });
    builder.Services.AddAuthorization(options => options.AddPolicy("BotOwner", policy => policy.RequireAuthenticatedUser()));
    builder.Services.AddHostedService<BotPollingWorker>();
    await using var app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapSkopkaChatBotApi("BotOwner");
    await app.RunAsync();
}
catch (Exception)
{
    Console.Error.WriteLine("Bot gateway stopped. Check protected configuration, host authorization and storage; no identity was automatically replaced.");
    Environment.ExitCode = 1;
}
