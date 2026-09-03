# Постоянная identity и привязка авторизованной сессии

Opt-in [backup истории](backups.md) в 0.16.0 не восстанавливает и не
переносит DeviceId/private keys. На новом устройстве используется обычное явное
создание собственной identity и enrollment; recovery key открывает только архив.
Logout блокирует backup coordinator вместе с session. Отзыв старого устройства не
стирает уже скопированный recovery key: host отдельно отзывает его Auth-сессии.

Браузерная реализация использует тот же lifecycle: [Client.Browser](browser.md)
хранит ключи/metadata в зашифрованном IndexedDB и сериализует создание через Web Locks.
Portable key container v1 вводится явно; старые native NSec records остаются читаемыми.
Ожидаемый контекст приходит от account endpoint, не из challenge. Logout блокирует
vault без удаления identity; полная очистка origin не позволяет восстановить ключи.
См. [ADR 0019](adr/0019-browser-client-cryptography-and-vault.md).

Bot gateway из 0.15.0 использует тот же lifecycle через `ProtectedFileBotIdentityStore`: отдельные scope/InstallationId, create-only keys, leased metadata и явное восстановление. Key ring ASP.NET Data Protection необходимо независимо защищать и сохранять; пример требует внешний сертификат. Это не автоматическая замена OS SecureStorage для MAUI. См. [ботов](bots.md).

Начиная с версии `0.14.0`, `DeviceId` можно сохранять независимо от logout/re-login. Это отдельный opt-in механизм: старый claims-based HTTP-режим не меняется сам собой. Ничего не требуется менять в `SkopiClub.Auth`; изменения ниже выполняет потребитель пакетов в chat host и своём клиентском composition root. Приложения продукта не входят в этот репозиторий.

## Модель и API

`DeviceIdentityScope(ServiceId, UserId, InstallationId)` — постоянная область устройства. `InstallationId` — случайный UUID, который приложение сохраняет один раз в защищённых installation metadata, без аппаратных идентификаторов. Не создавайте его при каждом запуске, login или token refresh. Исключите совместное копирование installation identity и ключей в другую установку из обычного backup. `ServiceId` — точная конфигурационная строка, одинаковая на сервере и клиенте, а не HTTP Host или значение из тела запроса.

`DeviceAuthorizationContext(ServiceId, UserId, SessionReference, ExpiresAt)` — временный контекст, независимо подтверждённый host. Непрозрачный `SessionReference` не является access/refresh token, не обязан быть GUID или называться `sid`. Одна сессия связывается только с одним устройством; несколько новых сессий могут связываться с прежним устройством. Несколько установок одного аккаунта получают разные DeviceId.

| Слой | Основные API |
| --- | --- |
| Client | `PersistentDeviceIdentityService`, `DeviceBindingProofService`, `DeviceBindingCoordinator`, `IDeviceBindingTransport` |
| Client.Maui | `SecureStorageDeviceIdentityStore`, scoped `SecureStorageDeviceKeyStore`, `IIdentityStorageLock`, `FileIdentityStorageLock` |
| Protocol | `DeviceAuthorizationContext`, `DeviceBindingChallenge`, `DeviceBindingProof`, `DeviceSessionBinding`, `DeviceBindingEncoding` |
| Server | `DeviceBindingService`, `IDeviceBindingRepository`, `IDeviceProofVerifier` |
| Server.NSec | `NSecDeviceProofVerifier`: только Ed25519 verification; нет private keys/decryption API |
| PostgreSql | `PostgreSqlDeviceBindingStore`, migration `202609020005_DeviceSessionBindings` |
| HTTP | `SkopkaChatHttpClient.IssueAsync/CompleteAsync`, `AddSkopkaChatDeviceBinding`, `IChatAuthorizationContextProvider` |

```mermaid
sequenceDiagram
    participant Host as Authenticated host context
    participant Client as Client + protected identity
    participant Chat as Chat bootstrap API
    participant DB as PostgreSQL
    Host->>Client: trusted account/session context
    Client->>Client: Load identity; explicit Create only on first use
    Client->>Chat: account-authenticated enrollment/rebind challenge
    Chat->>DB: store random, short-lived canonical challenge
    Chat-->>Client: binding-v1 challenge
    Client->>Client: compare expected context + both keys; sign typed bytes
    Client->>Chat: challenge ID + signature
    Chat->>DB: atomic enroll/consume/bind; reject revoked/conflicting state
    Chat-->>Client: permanent device + binding
    Client->>Chat: normal authenticated device-bound send/sync
```

## Подключение в SkopiClub.Chat

1. Согласованно обновите используемые пакеты до `0.14.0`. Для сервера добавьте `Skopka.Chat.Server.NSec` вместе с существующими `Server.AspNetCore` и `Persistence.PostgreSql`. NSec остаётся optional: собственный reviewed verifier может реализовать `IDeviceProofVerifier`, но Server не должен ссылаться на Client.
2. Примените новую migration обычным контролируемым механизмом развёртывания chat database. Старые миграции не переписываются, существующие devices/conversations/envelopes не переименовываются. Проверяйте миграцию на disposable DB до production; не запускайте тесты на рабочей БД.
3. Сохраните существующую нормальную проверку внешнего Auth: issuer, audience, подпись, срок токена, разрешённую схему и нужные account policies. Не регистрируйте sample authentication handler и не выпускайте собственные access tokens.
4. Реализуйте `IChatAuthorizationContextProvider`. Берите `sub` и `sid` только из аутентифицированного principal разрешённой схемы, отвергайте missing/duplicate значения. Отображайте `sub` в стабильный chat UserId через существующее trusted mapping. Нельзя получать устройство из `sid`, `X-Device-Id` или произвольной device claim.
5. Контекст должен включать стабильный абсолютный deadline сессии/окна привязки, одинаковый при refresh access token. Не подставляйте каждый новый JWT `exp`: это изменит контекст и закроет доступ старому binding. Host может хранить неизменяемое ограниченное окно для `(sub, sid)` в собственном session catalog, без изменений Auth. Нужный клиенту контекст передаёт доверенный account/session provider chat-приложения; не считывайте «ожидаемый» контекст из самого challenge. Новое login/новое окно после окончания старого получает новую уникальную session reference. Все четыре поля сравниваются точно, время нормализуется к UTC milliseconds.
6. Зарегистрируйте repositories, verifier, provider и opt-in режим. Выполняемый compile-checked пример с validated `sub`/`sid`, host-owned session catalog и named rate limits: [`DeviceBindingHostExample.cs`](../samples/Skopka.Chat.Sample/DeviceBindingHostExample.cs). Его `IValidatedChatSessionCatalog` — точка подключения существующей host session policy, а не готовая реализация Auth. Если в principal несколько authenticated schemes, выберите допустимую схему до этой точки; пример отклоняет неоднозначные duplicate claims.

Минимальная дополнительная регистрация поверх существующих `ChatDbContext`, `IDeviceRepository`, `IConversationRepository`, `IEnvelopeRepository` и `ChatServerEngine`:

```csharp
services.AddScoped<IDeviceBindingRepository, PostgreSqlDeviceBindingStore>();
services.AddSingleton<IDeviceProofVerifier, NSecDeviceProofVerifier>();
services.AddScoped<IChatAuthorizationContextProvider, YourValidatedSessionContextProvider>();
services.AddSkopkaChatDeviceBinding(options =>
{
    options.ServiceId = configuredChatServiceId;
    // Optional: host account/step-up policy, never a policy requiring an existing binding.
    options.AccountAuthorizationPolicy = "YourAuthenticatedChatAccountPolicy";
});
```

Обязательно зарегистрируйте две named rate-limit policies: `skopka-chat-challenges` и `skopka-chat-proofs` (имена заменяемы через options). Пример ограничивает выдачу и проверки по аутентифицированному аккаунту. В multi-instance production используйте общие лимиты/quotas и периодический bounded cleanup; встроенный ASP.NET limiter из примера локален процессу. Также ограничивайте частоту enrollment и общий объём активных устройств своей host policy.

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapSkopkaChatApi();
```

`DeviceBindingPolicies.Account` защищает только bootstrap. `DeviceBindingPolicies.Device` защищает обычный chat API: разрешение асинхронно получает binding из БД и проверяет текущее revocation-состояние. Оно кэшируется лишь внутри одного HTTP request. Без binding нельзя отправлять, polling/ack или отзывать устройства. В bound mode старый POST регистрации устройств не отображается; enrollment проходит только с proof. `IChatPrincipalMapper` сохраняет прежний режим, если opt-in не включён; в bound mode он не служит fallback. Дополнительная legacy chat policy из `SkopkaChatHttpOptions` может продолжать ограничивать обычные endpoints, но не переносится автоматически на bootstrap.

API под стандартным prefix `/skopka-chat/v1`:

- `POST /device-binding/challenges`: operation `1` (Enrollment) или `2` (Rebind), регистрационные public keys; server сам задаёт UserId и authoritative timestamps.
- `POST /device-binding/completions`: только challenge ID и signature.
- Body/response максимум 4096 bytes, строгий source-generated JSON без unknown/duplicate members; canonical challenge максимум 1024 bytes. Tokens передаются только существующим механизмом Authorization, не в payload.

Host обязан отключить HTTP body/header logging и EF sensitive-data logging; не записывать tokens, private keys, plaintext history или proof request bodies в telemetry/crash reports. TLS, CSRF/CORS, proxy limits, anti-abuse, authorization/session revocation и защита БД остаются обязанностью host.

## Клиент: первый запуск и повторный вход

На MAUI создайте scoped key store и metadata store над тем же injected `ISecureStorage` и общей защищённой lock directory. Все процессы/адаптеры, меняющие эти записи, обязаны использовать один lock contract; не удаляйте lock files во время работы. При нескольких сервисах использованный для ключей/trust namespace должен быть раздельным.

```csharp
var scope = new DeviceIdentityScope(configuredChatServiceId, authenticatedUserId, persistedInstallationId);
var storageLock = new FileIdentityStorageLock(protectedInstallationLockDirectory);
var keys = new SecureStorageDeviceKeyStore(secureStorage, scope, storageLock);
var metadata = new SecureStorageDeviceIdentityStore(secureStorage, storageLock);
var identities = new PersistentDeviceIdentityService(keys, metadata, timeProvider);

var identity = await identities.LoadAsync(scope, cancellationToken);
// Only after an explicit first-use user choice, and only for Absent:
if (identity.State == PersistentDeviceIdentityState.Absent && userApprovedNewDevice)
    identity = await identities.CreateAsync(scope, cancellationToken);
// Any non-Ready state stops bootstrap. Never fall back to Create on storage/key errors.
```

Состояния: `Absent`, `Ready`, `RecoveryRequired` (metadata есть, ключей нет), `Corrupt`, `Unavailable`, `Revoked`. `CreateAsync` повторно возвращает имеющуюся identity/state, не перезаписывает ключи. `IDeviceKeyStore.TryCreateAsync` — новая обязательная capability для создания: custom adapters должны реализовать atomic create-if-absent. Default implementation бросает `NotSupportedException`; старые adapters остаются пригодны для чтения, но не для нового создания. Обычный `SaveAsync` остаётся явной операцией замены для обратной совместимости; не используйте её при login.

Metadata содержит reservation до создания ключей. Сбой после сохранения ключей и до финальных metadata восстанавливает прежний DeviceId/KeyId из reservation. Сбой до появления ключей возвращает RecoveryRequired; даже повторный Create не генерирует замену. SecureStorage write не имеет cancellation API: адаптер дожидается фактического завершения уже начатой записи до снятия lock, затем передаёт cancellation. Срок acquisition lock ограничен; вызов platform storage сам может задержаться. Для строгих platform deadlines нужен host adapter, который гарантированно останавливает запись, а не оставляет её писать после освобождения lock.

После Ready:

```csharp
var device = identity.Metadata!.PublicDevice!;
// http.BaseAddress is the configured HTTPS chat endpoint.
var api = new SkopkaChatHttpClient(http, hostAccessTokenProvider,
    Options.Create(new SkopkaChatHttpClientOptions
    {
        AuthenticatedUserId = device.UserId.Value,
        AuthenticatedDeviceId = device.DeviceId.Value // NOT sid
    }), timeProvider);
var bootstrap = new DeviceBindingCoordinator(identities,
    new DeviceBindingProofService(keys, timeProvider), api);
await bootstrap.BindAsync(scope, trustedAccountSessionContext,
    identity.Metadata.Registered ? DeviceBindingOperation.Rebind : DeviceBindingOperation.Enrollment,
    cancellationToken);
// Only now compose/start sender, sync, history and MauiChatSession.
```

`IAccessTokenProvider` по-прежнему принадлежит host и может обновлять токен на каждом HTTP attempt. Прерванный login/account switch должен отменять bootstrap и старые sender/sync services. `DeviceBindingCoordinator` не создаёт identity автоматически, проверяет ответ и сохраняет authoritative registration metadata; `DeviceBindingRevokedException` от HTTP 410 запоминает локальный Revoked state. Если server commit состоялся, но запись локального `Registered` не удалась, не создавайте устройство: после загрузки прежних ключей выполните явный Rebind. Для неоднозначного Enrollment нельзя автоматически подменять операцию на новую регистрацию.

Путь SQLite history/outbox строится из постоянной scope/device identity, например `scope.StoragePartition + "." + device.DeviceId.Value.ToString("N")`, а не SessionReference. После нового login переоткройте те же БД, используйте прежние ключи, выполните Rebind и запустите sync/outbox. Не переносите данные из-за смены `sid` и не создавайте новые envelopes для сохранённого outbox plan. При миграции сохраните уже использованный правильный account/device path; смена схемы имён не должна потерять старые файлы.

## Logout, удаление и migration

- Logout: остановить/dispose session services, очистить credentials по политике приложения. Ключи, identity и разрешённая retention policy история остаются.
- Серверный отзыв: выполнить authenticated `RevokeDeviceAsync` и подтвердить результат, затем отметить локально `RememberRevokedAsync`. Все bindings устройства перестают разрешать новые запросы; уже начавшийся request имеет обычную race с отзывом.
- «Забыть локально»: явный `ForgetLocalAsync` удаляет metadata/keys, но не отзывает remote device и не стирает SQLite/history/backups. Эти действия приложение выполняет отдельно. Если remote revoke не прошёл, UI не должен сообщать об успешном серверном отзыве.
- Старый `DeviceId = sid` допустимо оставить навсегда. Если ключи уже находятся в новом scoped store, вызовите `AdoptAsync(scope, oldPublicDevice)`, затем Rebind. Для старого MAUI user-only namespace используйте `ImportLegacyAsync(scope, oldPublicDevice, legacyKeyStore)`: копирование create-only в новый namespace, проверка обоих public keys, сохранение прежних IDs, без удаления источника. Interrupted import может явно продолжиться только с теми же retained keys. Удаление старой копии — отдельная политика после успешного proof и проверки локальной истории.
- Не используйте user-only constructor key store для новой multi-service identity. Он оставлен для legacy load/import и имеет только process-local serialization; новая перегрузка принимает полный scope и cross-process lock.
- При отсутствии старых private keys возвращается RecoveryRequired. Account login, старый DeviceId и запись в directory не восстанавливают владение/историю. Несколько прежних устройств не объединяются автоматически.

## Идемпотентность, cleanup и границы безопасности

Pending challenge выдаётся на две минуты, не дольше session deadline (canonical format допускает максимум пять минут). Невалидная подпись не потребляет challenge. Completion фиксирует enrollment, binding и consumed state одной транзакцией. HTTP issuance не retry-ится автоматически; потерянный challenge истекает. Completion retry ограничен настройками существующего HTTP client и сохраняет точные proof bytes. Точный повтор уже успешного proof возвращает исходный binding даже после короткого challenge expiry, но только до session deadline и без последующего device revocation. Другой context/signature, попытка смены устройства/продления сессии или pending expiry отвергаются.

Планируйте `IDeviceBindingRepository.CleanupAsync(timeProvider.GetUtcNow(), maximumCount, cancellationToken)` с `maximumCount` от 1 до 1000; лимит общий на challenges и bindings за вызов. Consumed proofs хранятся до session deadline ради retry; expires bound и quotas задаёт host. Cleanup не заменяет проверки времени при каждом запросе.

Proof подтверждает владение ключом только в момент привязки. Это не OAuth/JWT validation, мгновенный отзыв Auth-сессии, DPoP/mTLS, key backup/recovery, forward secrecy или ratchet. Обладатель украденной действующей account session может зарегистрировать новое своё устройство без отдельного step-up. Украденный bearer token уже связанной сессии остаётся bearer credential. Требования step-up enrollment, live revocation, sender-constrained tokens и identity verification решаются отдельно. Protocol-v1/content-v1/v2/v3 bytes не изменились. Binding-v1 имеет отдельный domain/golden vector; см. [ADR 0017](adr/0017-persistent-device-session-binding.md).

## Проверки перед интеграцией

Запустите [общие gates](development.md), оба MAUI test projects и новый обязательный disposable PostgreSQL gate:

```powershell
$env:SKOPKA_CHAT_POSTGRES_TESTCONTAINERS = 'true'
$env:SKOPKA_CHAT_POSTGRES_REQUIRED = 'true'
dotnet test --project tests/Skopka.Chat.Binding.Tests --configuration Release --no-build --no-restore
```

`SKOPKA_CHAT_POSTGRES` имеет приоритет: убедитесь, что он отсутствует или указывает только на явно disposable DB. Тест actual container restart выполняется именно на owned Testcontainer; внешний DB-run не сертифицирует restart. Тесты включают atomic rollback/races, bounded cleanup, re-login E2EE, прежние SQLite history/outbox, hostile JSON, canonical mutation, missing/corrupt keys и отсутствие чувствительных значений в логах/ошибках. Управляемые MAUI tests с injected fake SecureStorage не сертифицируют платформенные SecureStorage/Keychain, backup, uninstall/restore или file-lock semantics: отдельно пройдите эти сценарии на целевых устройствах, включая физический iOS ARM64.
