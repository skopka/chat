# Skopka.Chat

Skopka.Chat — переиспользуемый транспорт-независимый движок личного чата для .NET 10. Сервер регистрирует публичные данные устройств, хранит и доставляет зашифрованные конверты, но не получает закрытые ключи и не имеет API расшифровки.

> **Security status:** это ограниченный E2EE MVP, а не реализация Signal и не прошедший аудит production-протокол. В v1 нет Double Ratchet, forward secrecy относительно компрометации долгосрочного ключа получателя и post-compromise security. Перед использованием прочитайте [threat model](docs/threat-model.md), [ADR по криптографии](docs/adr/0001-e2ee-cryptography.md) и [ограничения MVP](docs/mvp-limitations.md).

## Пакеты

- `Skopka.Chat.Protocol` — идентификаторы, лимиты, публичные контракты и каноническое бинарное представление v1; без ASP.NET Core, EF Core и криптографии клиента.
- `Skopka.Chat.Client` — идентичность устройства, `IDeviceKeyStore`, X25519/HKDF/XChaCha20-Poly1305/Ed25519 через NSec, fingerprints/security codes, `IChatTransport` и локальная дедупликация.
- `Skopka.Chat.Transport.Http` — общие HTTP routes, JSON DTO, protocol mappings, лимиты и source-generated `System.Text.Json` metadata; зависит только от Protocol.
- `Skopka.Chat.Client.Http` — typed `HttpClient`, `IAccessTokenProvider`, HTTPS-by-default, bounded responses и ограниченные retries идемпотентных операций; без ссылки на Server.
- `Skopka.Chat.Server` — личные диалоги, жизненный цикл устройств, идемпотентный приём, очередь доставки, acknowledgements и repository-интерфейсы; без ссылки на Client.
- `Skopka.Chat.Server.AspNetCore` — необязательные Minimal API endpoints с обязательной авторизацией и строгой привязкой user/device claims к серверным операциям; без выбора формата токена или identity provider.
- `Skopka.Chat.Persistence.PostgreSql` — EF Core 10/Npgsql, PostgreSQL migration, ограничения `bytea`, внешние ключи, индексы доставки и TTL cleanup.

Версия пакетов `0.3.0` по-прежнему реализует protocol v1; minor-релиз завершает общий HTTP client/server vertical slice, не меняя канонический wire format конверта. Правила совместимости описаны в [protocol-compatibility.md](docs/protocol-compatibility.md).

## Быстрый старт клиента

Реальное приложение должно реализовать `IDeviceKeyStore` поверх защищённого хранилища платформы. `InMemoryDeviceKeyStore` предназначен только для тестов и sample.

```csharp
var keyStore = new MyPlatformDeviceKeyStore();
var identityService = new DeviceIdentityService(keyStore);
var alice = await identityService.CreateAsync(userId, DeviceId.New(), DateTimeOffset.UtcNow);

// PublicDevice получателя приходит из аутентифицированного каталога сервера.
var crypto = new ChatCryptoService(keyStore);
EncryptedEnvelope envelope = await crypto.EncryptTextAsync(
    "hello",
    conversationId,
    MessageId.New(),
    alice.DeviceId,
    bobPublicDevice,
    DateTimeOffset.UtcNow);

await transport.SendAsync(envelope); // transport реализует IChatTransport
```

Получатель обязан получить текущий `PublicDevice` отправителя, проверить подпись при расшифровке и сравнить security code по доверенному внешнему каналу при первом контакте или смене ключа:

```csharp
var receiver = new ChatReceiver(
    new ChatCryptoService(keyStore),
    new MyTransactionalReceivedMessageStore());

ReceiveResult result = await receiver.ReceiveAsync(delivery.Envelope, senderPublicDevice);
string code = SecurityCodes.Between(myPublicDevice, senderPublicDevice);
```

## HTTP-клиент

Host реализует получение access token и регистрирует отдельный typed client для текущей пары user/device:

```csharp
builder.Services.AddScoped<IAccessTokenProvider, MyAccessTokenProvider>();
builder.Services.AddSkopkaChatHttpClient(
    new Uri("https://chat.example.com/"),
    options =>
    {
        options.AuthenticatedUserId = myPublicDevice.UserId.Value;
        options.AuthenticatedDeviceId = myPublicDevice.DeviceId.Value;
    });

var api = serviceProvider.GetRequiredService<SkopkaChatHttpClient>();
await api.RegisterDeviceAsync(myPublicDevice);
await api.CreateConversationAsync(peerUserId, conversationId);

PublicDevice peer = await api.GetDeviceAsync(peerDeviceId)
    ?? throw new InvalidOperationException("Peer device was not found.");
EncryptedEnvelope envelope = await crypto.EncryptTextAsync(
    "hello",
    conversationId,
    MessageId.New(),
    myPublicDevice.DeviceId,
    peer,
    DateTimeOffset.UtcNow);
await api.SendAsync(envelope);
```

`SkopkaChatHttpClient` также регистрируется как `IChatTransport`. Он предназначен для transient/scoped использования, требует HTTPS, отключает redirects в предоставленном DI handler, получает новый токен перед каждой попыткой и не копирует error body в исключения. `RequireHttps = false` допустим только для доверенного локального TestServer. Подробнее: [ADR 0003](docs/adr/0003-http-contract-and-client.md).

## Подключение transport-neutral сервера

Ядро не навязывает HTTP, WebSocket или SignalR. Host проверяет пользователя/access token, затем вызывает движок:

```csharp
var options = new DbContextOptionsBuilder<ChatDbContext>()
    .UseNpgsql(connectionString)
    .Options;

await using var db = new ChatDbContext(options);
await db.Database.MigrateAsync();
var store = new PostgreSqlChatStore(db);
var engine = new ChatServerEngine(store, store, store);

await engine.RegisterDeviceAsync(publicDevice);
await engine.CreateConversationAsync(aliceUserId, bobUserId, conversationId, now);
await engine.SubmitAsync(encryptedEnvelope, now);
IReadOnlyList<StoredEnvelope> pending = await engine.ReceiveAsync(recipientDeviceId, 50, now);
```

`ChatServerEngine` проверяет структуру, лимиты, участников, актуальность key ID, revocation и идемпотентность. Он намеренно не проверяет криптографическую подпись: получатель делает это в Client; транспортный host обязан аутентифицировать право устройства отправлять от своего имени.

## Подключение ASP.NET Core API

Необязательный пакет `Skopka.Chat.Server.AspNetCore` не выпускает и не валидирует токены. Сначала host настраивает доверенный authentication handler, затем регистрирует движок/repositories и transport:

```csharp
builder.Services
    .AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", jwt =>
    {
        jwt.Authority = identityAuthority;
        jwt.Audience = chatAudience;
    });
builder.Services.AddAuthorization();

builder.Services.AddDbContext<ChatDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<PostgreSqlChatStore>();
builder.Services.AddScoped<IDeviceRepository>(sp => sp.GetRequiredService<PostgreSqlChatStore>());
builder.Services.AddScoped<IConversationRepository>(sp => sp.GetRequiredService<PostgreSqlChatStore>());
builder.Services.AddScoped<IEnvelopeRepository>(sp => sp.GetRequiredService<PostgreSqlChatStore>());
builder.Services.AddScoped<ChatServerEngine>();
builder.Services.AddSkopkaChatAspNetCore(options =>
{
    options.UserIdClaimType = ClaimTypes.NameIdentifier;
    options.DeviceIdClaimType = "skopka_chat_device_id";
});

app.UseAuthentication();
app.UseAuthorization();
app.MapSkopkaChatApi();
```

По умолчанию обе claims должны встречаться ровно один раз и содержать GUID в формате `D`. Регистрация получает user ID из principal, отправка сверяет claim устройства с `SenderDeviceId`, а polling/acknowledgement вообще не принимают recipient device ID от клиента. Общие DTO находятся в `Skopka.Chat.Transport.Http`, поэтому Client.Http не ссылается на серверную сборку. Для cookie-authentication host дополнительно обязан настроить CSRF-защиту; для любой схемы обязательны TLS, rate limits и внешний request-size limit. Полное решение зафиксировано в [ADR 0002](docs/adr/0002-aspnet-core-transport-authorization.md).

## Сборка и проверка

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --solution Skopka.Chat.sln --no-build --no-restore
dotnet pack Skopka.Chat.sln --no-restore
dotnet run --project samples/Skopka.Chat.Sample
```

NuGet-пакеты создаются в `artifacts/packages`.

PostgreSQL integration test запускается только против явно предоставленной одноразовой базы:

```powershell
$env:SKOPKA_CHAT_POSTGRES = 'Host=localhost;Database=skopka_chat_tests;Username=postgres;Password=...'
dotnet test --project tests/Skopka.Chat.Persistence.PostgreSql.Tests
```

Без переменной тест корректно пропускается; остальные unit и in-memory integration tests не требуют инфраструктуры.

## Что не входит в v1

UI, production-инфраструктура, интеграция со SkopiClub, группы, вложения, push, backup/recovery ключей и автоматический multi-device fan-out не входят в этот репозиторий. Контракты различают user и device, поэтому один пользователь может иметь несколько устройств, а отправитель создаёт отдельный конверт для каждого устройства-получателя.
