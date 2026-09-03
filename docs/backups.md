# Сквозное резервное копирование истории (backup-v1)

Opt-in возможность **0.16.0**. Архив содержит данные
истории, а не ключи для уже удалённых конвертов. Для восстановления не нужны старое
устройство или очередь доставки. Новое устройство создаёт/сохраняет собственную
identity обычным lifecycle. Протокол сообщений, binding-v1, native key containers,
BrowserVault v1 и существующие таблицы журналов не переинтерпретируются.

## Четыре разных секрета/идентичности

| Назначение | Что даёт | Чего не делает |
| --- | --- | --- |
| Пароль аккаунта / нормальная Auth-сессия | Доступ к аккаунту и ciphertext API | Не расшифровывает историю |
| Локальная фраза BrowserVault | Открывает конкретный локальный vault | Не восстанавливает удалённый origin |
| Случайный ключ восстановления | Открывает предварительно созданный архив аккаунта | Не является паролем или seed устройства |
| DeviceId + device private keys | Новые сообщения и привязка установки | Не клонируются из архива |

`BeginEnableAsync` явно выдаёт новый ключ только при отсутствии архива и локальной
резервации. Повторная выдача возвращает тот же защищённый ключ. До
`ConfirmRecoveryKeyAsync` копирование запрещено. Конфликтующий архив/ключ нельзя
перезаписать. При существующем архиве новое устройство использует `UnlockAsync`.
На сервер отправляется только зашифрованный архив, **никогда recovery code**.

## Архитектура и модель доверия

- `Client`: `ChatBackupRecoveryKey`, `ChatBackupCryptography`, `ChatBackupEventEncoding`,
  `RestoredChatContent`, `IChatBackupTransport` поверх существующего native/browser
  XChaCha20-Poly1305 provider; параллельной реализации шифрования сообщений нет.
- `Client.Storage`: `ChatBackupCoordinator`, `IChatBackupKeyStore`,
  `IChatBackupWorkspace`, `ChatBackupStatus`, `ChatBackupClientOptions`.
- `Client.Browser`: `BrowserBackupKeyStore` и `BrowserBackupWorkspace`: новые
  изолированные типы записей `backupkeys`/`backup` в прежнем зашифрованном vault,
  отдельный Web Lock. Plaintext IndexedDB/localStorage не используется.
- `Client.Maui`: `SecureStorageBackupKeyStore` (create-only под межпроцессной
  блокировкой). `Client.Storage.Sqlite`: отдельная version-1 backup workspace БД.
  Восстановленные строки здесь **plaintext**, как native live history: защищённый
  каталог, OS encryption, исключение из незащищённого OS/cloud backup — обязанности host.
- `Server`: `ChatBackupService`, `IChatBackupStorage`, `IChatBackupTransaction`,
  `ChatBackupServerOptions`. Нет зависимости от Client, private keys или decrypt API.
- `Persistence.PostgreSql`: отдельный `ChatBackupDbContext`, таблица
  `chat_backup_records`, migration `202609030001_EncryptedHistoryBackups`, отдельная
  `__SkopkaChatBackupMigrations`. Не зависит от `envelopes`/delivery TTL.
- `Transport.Http` + `Client.Http` + `Server.AspNetCore`: бинарные opt-in endpoints;
  `SkopkaChatHttpClient` реализует `IChatBackupTransport`.
- UI.Core/Blazor/MAUI: явный `ApplyRestored`, признак `ContainsBackupHistory` и
  локализуемое предупреждение. UI не занимается криптографией.

Журнал не хранит исходные подписанные конверты и доказательства доверия к старым
ключам. Поэтому архив доказывает **владение recovery key**, а не оригинальную подпись
отправителя. Владелец этого ключа может подделать историческую атрибуцию. Нельзя
использовать импорт как доказательство авторства, разрешение доступа или webhook.
`RestoredChatContent` намеренно не является `ReceivedChatContent`.

Проекция сохраняет ContentId, conversation/sender/device assertions, время и
исходные content-v1/v2/v3 bytes. Правки, реакции, ответы и forwards складываются тем
же reducer, включая события до своей цели. Разные варианты одного ContentId
конфликтуют и исключаются; локальные sender-verified события имеют приоритет над
восстановленными. Предупреждение консервативно относится ко всей проекции, включая
восстановленные правки/реакции. Display-only DeliveryMessageId восстановленного
элемента — placeholder из ContentId, **не идентификатор для ACK**.

Удаления сообщений в текущем content-протоколе отсутствуют: этот этап их не
изобретает. Неизвестный будущий content format отвергается. Включены зашифрованные
attachment manifests (в том числе file keys), captions и ссылки; **бинарные файлы
не копируются**. Их отдельный TTL/удаление может сделать скачивание невозможным.
Восстановление само ничего не скачивает, не открывает и не вызывает host callbacks.
Pending outbox/jobs не экспортируются и не импортируются.

## Версионированный формат

Все числа — big-endian signed Int32/Int64 с проверкой неотрицательных границ;
GUID — 16 байт RFC/network order (`Guid.TryWriteBytes(bigEndian: true)`).
Строка service — строгий UTF-8 с Int32 byte length (1–256), точное сравнение.
Timestamp — Int64 UTC ticks .NET, не доказательство доверенного времени.
JSON не подписывается и не используется как AEAD associated data.

`D = UTF8("Skopka.Chat.Backup") || 00 || 01` (19 bytes).
`A = length(service) || service || UserId || ArchiveId || KeyGeneration`.
Первые два ID архива/поколения случайные UUID; версия задаётся доменом D.

| Запись | Канонические поля по порядку |
| --- | --- |
| Archive | `D || 'A' || A` |
| Part | `D || 'P' || UploadId || Index:i32 || PreviousPartSHA256:32 || Nonce:24 || CiphertextLength:i32 || CiphertextWithTag` |
| Part AAD | `D || 'D' || A || UploadId || Index:i32 || PreviousPartSHA256:32` |
| Seal AAD | `D || 'S' || A || VersionId || ParentId || ParentSealSHA256:32 || PartCount:i32 || TotalEncodedPartBytes:i64 || FinalPartSHA256:32 || CreatedAt:i64` |
| Seal | `SealAAD || Nonce:24 || Tag:16` (AEAD над пустым plaintext) |
| Event plaintext | `UTF8("Skopka.Chat.Backup.Event") || 00 || 01 || ConversationId || SenderUserId || SenderDeviceId || SentAt:i64 || ContentLength:i32 || canonical content bytes` |

UploadId становится VersionId. Root ParentId/ParentHash — нули. Index 0 имеет
нулевой PreviousHash. Пустая contribution имеет count/bytes/final hash = 0.
SHA256 считается над **полным encoded part/seal**, включая домен, nonce и tag.
Unknown domain/version, неверные длины, отрицательные значения, truncation и trailing
data отвергаются. Жёсткие границы: control 1024 bytes, part 66000 bytes, 100000 parts
в contribution, 4096 версий, страницы до 100 ключей.

Recovery key: 32 случайных CSPRNG bytes (256 bits). Формат:
`SCB1-<8 групп по 8 hex>-<8 hex checksum>`. При вводе игнорируются только ASCII
пробелы/дефисы и регистр hex. Checksum — первые 4 bytes SHA256 от 64 bytes:
ASCII `Skopka.Chat.Backup.Key.v1`, нулевое дополнение до 32 bytes, затем key32.
Checksum обнаруживает ошибки ввода, не аутентифицирует архив.

HKDF-SHA256: IKM = recovery key, salt = ASCII `Skopka.Chat.Backup.Kdf.v1`,
info = `EncodeArchive(archive) || purpose`, output 32 bytes. Purpose `'P'` для
частей, `'S'` для seals. AEAD — существующий XChaCha20-Poly1305 provider с новым
случайным 192-bit nonce на каждое шифрование. Вероятность случайного повторения
пренебрежимо мала; повтор upload использует **те же ciphertext bytes**, не новое
шифрование. Rebase меняет только seal, с новым nonce. Rotations/compaction не реализованы.

## Конкуренция, надёжность и ограничения ресурсов

Устройства **добавляют** неизменяемые contributions. Каждая новая seal ссылается на
текущую завершённую голову. Сервер под account-wide транзакционной блокировкой
проверяет количество, индексы, SHA-chain, byte totals и CAS головы. Проигравший
writer аутентифицирует новую голову и пересоздаёт только seal. Устройство с половиной
истории не заменяет историю другого устройства.

Local export checkpoint хранит длину и hash-chain префикса live journal. Повторный
проход потоковый; неизменившийся префикс не загружается снова. При переписанном или
сокращённом локальном журнале безопасно экспортируется весь имеющийся журнал.
Подготовленные encrypted parts и точная последняя seal сохраняются до network I/O.
После неоднозначного commit клиент сверяет завершённую VersionId и exact seal.

Restore сначала проверяет полную цепочку seals и локальный freshness pin. Затем
читает по одной части, проверяет AEAD/chain, пишет невидимые staging rows. Ключ строки
— SHA256 canonical event bytes **без recipient-specific delivery ID**: overlapping
fan-out истории дедуплицируются, конфликтующие варианты не затирают друг друга.
Защищённый checkpoint фиксируется после durable row; после перезапуска скачивание
продолжается с первого незафиксированного индекса. Crash между row/cursor повторяет
точный duplicate. Новая remote head начинает новый staging snapshot. Durable ссылки
part→event позволяют перед active commit повторно прочитать и проверить все staging
строки и их количество ограниченными порциями: один cursor не маскирует пропавшие
или повреждённые локальные данные после прерывания.

Только после полной проверки атомарно меняется active pointer. Предыдущая видимая
история сохраняется при отмене, ошибке записи, quota/full disk и повреждении архива.
Garbage старых staging groups удаляется после pointer commit, ограниченными страницами.
Импорт не вызывает `IChatEventStore.StoreAsync`, `IChatEventApplier`, transport ACK,
local echo, ботов и внешних обработчиков. Live journal остаётся неизменным.

Defaults: client update/restore ≤1 GiB encoded bytes; server account ≤1 GiB включая
pending parts; ≤4 concurrent uploads; pending expiry 7 дней с первого Begin (retry
не продлевает). Limits настраиваются независимо на клиенте/сервере. Native workspace
default ≤4 GiB записей; SQLite file/WAL overhead и OS disk quotas контролирует host.
Нужно место для текущей истории, подготовленных частей, старого active и нового staging
одновременно. Browser quota задаётся также origin/браузером. Один проход не держит
весь архив в RAM; UI host тоже должен читать/показывать ограниченными порциями.

Completed ancestors не удаляются по TTL. При достижении bytes/version quota новые
записи отклоняются, а не обрезают зависимости. `CleanupAsync(scope)` удаляет только
expired pending uploads; также вызывается при Begin. Host планирует cleanup по своему
реестру аккаунтов и ограничивает частоту/параллельность запросов. Это не автоматический
планировщик. Библиотека не предоставляет небезопасный «удалить часть версии» endpoint.

## Подключение сервера

Compile-checked пример: [BackupHostExample.cs](../samples/Skopka.Chat.Sample/BackupHostExample.cs).

```csharp
var storage = new PostgreSqlBackupStorage(hostConnectionString);
services.AddSingleton<IChatBackupStorage>(storage);
services.AddSingleton(sp => new ChatBackupService(
    sp.GetRequiredService<IChatBackupStorage>(), TimeProvider.System,
    new ChatBackupServerOptions { MaximumBytes = 1L << 30 }));
// IChatAuthorizationContextProvider + нормальная Auth и named policies — host-owned.
// В контролируемой deployment-команде: await storage.MigrateAsync(token);
app.MapSkopkaChatBackups(configuredServiceId,
    "YourAuthenticatedChatAccountPolicy", "YourBackupConcurrencyPolicy");
```

Другой backend реализует `IChatBackupStorage`: сериализация account-wide для всех
процессов, rollback без commit, атомарный durable commit, exact scoped keys, bounded
read/page. PostgreSQL использует advisory transaction lock; хеш lock не является
авторизацией, SQL всегда сравнивает точные service/account. Sensitive SQL/body logging
нельзя включать. Независимая миграция не запускается автоматически на HTTP-запросе.

Под `/skopka-chat/v1/backups`: GET/PUT archive; `/{archive}/head` GET;
`/{archive}/versions/{version}` PUT begin, POST commit, GET completed seal;
`.../parts/{index}` PUT/GET immutable part. GET частей доступен только completed
версии. Bodies строго `application/octet-stream`, без content encoding/query fields.
PUT archive возвращает byte 0/1; commit byte 1/2/3 = committed/duplicate/conflict.
204 = отсутствие archive/head/version либо успешная пустая операция. Errors не содержат
provider body; числовой `X-Skopka-Backup-Failure` — только bounded enum. Ответы no-store.

Account определяется только `IChatAuthorizationContextProvider` после нормальной
аутентификации, с configured exact ServiceId и live expiry. UserId из encoded archive
лишь сравнивается, не даёт прав. Backup policy намеренно не требует старого device
binding. Host должен выбрать допустимую Auth-схему, отклонять missing/duplicate/cross-user
claims и проверять revocation/step-up. Для cookie/BFF обязательны CSRF, same-origin,
TLS и защитная CORS policy. Настройте account-wide concurrency, request deadlines,
proxy body limits и распределённые rate limits. Старый `MapSkopkaChatApi` не включает
backup endpoints автоматически.

## Browser, MAUI и UI

Compile-checked factories: [Browser BackupExample](../samples/Skopka.Chat.Browser.Sample/BackupExample.cs),
[MAUI BackupExample](../samples/Skopka.Chat.Maui.Sample/BackupExample.cs).

```csharp
var backup = new ChatBackupCoordinator(
    new BrowserBackupKeyStore(vault), new BrowserBackupWorkspace(vault),
    session.Events, authenticatedHttpClient,
    new ChatBackupCryptography(browserCryptography), TimeProvider.System);
session.AttachBackup(backup); // await session.DisposeAsync() перед закрытием vault
```

MAUI заменяет key store на `SecureStorageBackupKeyStore(scope, secureStorage, identityLock)`
и workspace на `SqliteBackupWorkspace(scope, dedicatedProtectedConnectionString)`.
Передайте coordinator в `MauiChatSession(..., resources: null, asyncResources: [backup])`.
При logout/account switch **await** закрытие session, затем vault/host resources.
Не сохраняйте coordinator в глобальном singleton между аккаунтами. Persisted recovery
key остаётся защищённым для следующего явного login/unlock; закрытый handle не читается.

```csharp
string code = await backup.BeginEnableAsync(token); // показать локально, не логировать
// Пользователь сохраняет ключ вне устройства и повторно вводит его в UI.
await backup.ConfirmRecoveryKeyAsync(userRetypedCode, token);
await backup.BackupAsync(token);

// На новом устройстве, после обычного login и собственной явной identity/vault setup:
await backup.UnlockAsync(userRecoveryCode, token);
await backup.RestoreAsync(token);
await foreach (var item in backup.ReadRestoredAsync(conversationId, token))
    viewModel.ApplyRestored(item); // display-only, не Apply/ACK/live-event handler
```

`RefreshAsync`, `Status`, `Progress` дают Phase, ProcessedParts/Bytes, LastBackupAt,
generic Failure; ключ возвращается только явной операцией выдачи. UI стирает свои
поля/ссылки при logout; immutable managed strings физически гарантированно не стираются.
LastBackupAt аутентифицировано ключом, но задано клиентом, не trusted clock.
Шаблоны UI заменяемы; сохраняйте смысл `BackupTrustWarning` и ограничения attachments.
Factory-примеры не включают автоматическую фоновую синхронизацию/расписание.

## Интеграция в SkopiClub.Chat и веб-кабинет

1. После отдельного согласованного релиза обновить нужные пакеты вместе. Сейчас код
   не опубликован; production/SkopiClub конфигурация этой задачей не менялась.
2. В SkopiClub.Chat подключить storage/миграцию и отдельные authenticated backup
   endpoints, переиспользовать trusted account mapping, не менять Auth-сервис.
3. В BFF разрешить только перечисленные backup routes/методы/размеры; использовать
   существующую cookie+CSRF авторизацию, не передавать recovery key через BFF.
4. В кабинете добавить opt-in экран выдачи/подтверждения ключа, кнопки backup/retry,
   статус/дату и экран recovery. Криптография остаётся в пакете, не в Razor.
5. После login нового устройства явно создать его vault и identity, вводом recovery
   key разблокировать архив; импортировать через ApplyRestored с предупреждением.
6. Закрывать backup вместе с session; проверить удаление browser data, отсутствие
   старого телефона, rate/quota/full-disk/cancel и отсутствие секретов в host telemetry.

## Угрозы и проверка

Независимого криптографического аудита **не было**. Это всё ещё constrained E2EE MVP.
Сервер видит service/account/archive/version IDs, размеры, число событий и время,
может отказать в выдаче/удалить ciphertext. Retained local head pin обнаруживает
откат для этого клиента; **новое устройство без внешнего anchor не распознает
криптографически корректный старый head**. Сервер проверяет структуру/полноту/CAS,
но не может проверить секретный AEAD tag: авторизованная account-сессия без recovery
key может добавить некорректную голову и вызвать отказ восстановления. Клиент её
отклонит; текст не раскрывается, старые immutable версии не перезаписываются.
Ограничивайте publish policy/step-up и поддерживайте отзыв Auth-сессий. Отдельное публично проверяемое
доказательство владения archive key и управляемое восстановление головы — будущая
граница безопасности, не заявленная функция v1. Нет key transparency/forward secrecy
архива: утечка recovery key раскрывает всё сохранённое поколение. Потерянный ключ
не восстанавливается сервером. Device revocation не стирает уже скачанные данные
или секрет; host отдельно закрывает account sessions. XSS/разблокированный
скомпрометированный клиент обходит E2EE. QR/передача секрета доверенным устройством,
медиа-backup, перенос outbox, rotation и compaction — следующие этапы, не готовые функции.

Автотесты: [BackupTests](../tests/Skopka.Chat.Client.Storage.Tests/BackupTests.cs),
[PostgreSQL/HTTP](../tests/Skopka.Chat.Http.IntegrationTests/BackupHttpTests.cs),
[Browser](../tests/Skopka.Chat.Browser.Tests/BrowserTests.cs),
[MAUI identity/key store](../tests/Skopka.Chat.Client.Maui.Tests/PersistentIdentityTests.cs).
Проверяются union/retry/commit ambiguity, incomplete/corrupt/wrong-key/cross-account,
protected staging/paging/quota, cancellation/write errors, logout, native↔browser
AEAD и отсутствие plaintext/secret в серверных строках. Полный gate —
[development](development.md) и [AGENTS](../AGENTS.md); браузерный
`node eng/browser/run-gate.mjs` запускает реальные Chromium/Firefox под CSP.
Физические iOS/Android Keychain/Keystore и Safari требуют отдельной host/device проверки.

### Проверка реализации перед релизом 0.16.0 (2026-09-03)

- Release solution build и format verification — успешно.
- 286 solution tests, включая обязательные owned Testcontainers PostgreSQL и
  установленный FFmpeg: 0 ошибок, 0 пропусков.
- MAUI Client: 20 passed; MAUI UI на Windows: 4 passed. Библиотеки собраны для всех
  объявленных TFM на Windows, Android sample с backup factory также собран.
- Реальные Chromium и Firefox: backup/staging/reopen, новая собственная identity,
  native↔browser crypto, существующие crash/race/retry и cookie/BFF/CSP сценарии прошли.
- Fuzz corpus replay прошёл, включая отдельные backup parsers. AFL++ не установлен
  в Windows-среде; coverage-guided Linux smoke локально не запускался.
- Локальный тестовый комплект `0.15.0-backup-local2`: 23 `.nupkg` + 23 `.snupkg`,
  согласованные зависимости; core/browser/MAUI package consumers прошли.
- Физические iOS/Android/macOS, Safari, OS uninstall/cloud restore и независимый
  криптографический аудит не выполнялись. Это результаты локальной проверки;
  публикация выполняется отдельно через [release workflow](releasing.md).
