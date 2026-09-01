# Encrypted attachments

Skopka.Chat `0.11.x` transfers media as an independently encrypted blob plus a small content-v2 manifest carried inside the existing protocol-v1 envelope. The server, PostgreSQL and S3-compatible provider receive ciphertext, an opaque attachment ID, conversation/uploader IDs, ciphertext length/hash and retention timestamps. File name, media type, caption, plaintext length, file key and nonce prefix remain inside E2EE content.

## Package choice

- `Skopka.Chat.Attachments` contains `IAttachmentStore`, `AttachmentStorageService`, immutable storage metadata and the host authorization boundary. It has no EF Core, AWS SDK, ASP.NET Core or Client dependency.
- `Skopka.Chat.Attachments.PostgreSql` stores bounded ciphertext in an isolated `AttachmentDbContext` and `chat_attachments` table. Its default limit is 16 MiB per `bytea` row. It is suitable for small deployments and small files, not large media.
- `Skopka.Chat.Attachments.S3` streams validated ciphertext into any compatible `IAmazonS3` client. It uses conditional `If-None-Match: *`, never overwrites an attachment ID and keeps metadata on the object.
- `Skopka.Chat.Client` owns chunk encryption/decryption and the encrypted manifest.
- `Skopka.Chat.Client.Http` and `Skopka.Chat.Server.AspNetCore` provide optional authenticated streaming upload/download/delete endpoints.

Message envelopes may stay in `Skopka.Chat.Persistence.PostgreSql` while files use S3. The two database contexts and migrations are deliberately independent.

For photo/video compression, `Skopka.Chat.Media` prepares plaintext locally before the encryption call below. Its optional FFmpeg adapter and `Auto`/`Media`/`File` semantics do not change attachment content v2 or expose media to the server. See [media.md](media.md).

## Client flow

Encrypt first, upload the resulting ciphertext, then send the manifest to every recipient device. If envelope fan-out fails after upload, the blob is an orphan until the host retention/cleanup policy removes it.

```csharp
await using var input = File.OpenRead(localPath);
await using var ciphertext = File.Create(encryptedTemporaryPath);

ChatAttachmentContent manifest = await ChatAttachmentCryptoService.EncryptAsync(
    input,
    input.Length,
    ciphertext,
    AttachmentId.New(),
    ChatContentId.New(),
    Path.GetFileName(localPath),
    "application/octet-stream");

ciphertext.Position = 0;
AttachmentStoreResult upload = await api.UploadAttachmentAsync(
    conversationId,
    manifest,
    ciphertext,
    expiresAt);

if (upload is AttachmentStoreResult.Stored or AttachmentStoreResult.Duplicate)
{
    // IChatContentSender creates one recipient-specific MessageId per device.
    await chatViewModel.SendAttachmentAsync(manifest);
}
```

On receive, only use a `ChatAttachmentContent` returned by `ReceiveContentAsync`. The typed HTTP client checks response length, conversation and ciphertext SHA-256 before it streams each authenticated chunk to the plaintext destination:

```csharp
await using var output = File.Create(downloadPath);
try
{
    await api.DownloadAndDecryptAttachmentAsync(conversationId, manifest, output);
}
catch
{
    // Required: discard the partial destination on every error.
    throw;
}
```

Do not derive a local path directly from `FileName`; it is sender-controlled plaintext after decryption. Normalize or replace it, prevent traversal/collisions and scan untrusted content before opening it with another application. `MediaType` is a rendering hint, not proof of file type.

## Server registration

The host selects one store and implements conversation authorization. `IAttachmentAccessAuthorizer` must consult authoritative membership/state and must not trust request headers as identity.

PostgreSQL for small blobs:

```csharp
builder.Services.AddDbContext<AttachmentDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddScoped<IAttachmentStore, PostgreSqlAttachmentStore>();
builder.Services.AddScoped<IAttachmentAccessAuthorizer, MyConversationAttachmentAuthorizer>();
builder.Services.AddSkopkaChatAttachmentStorage();
```

S3-compatible storage for larger media:

```csharp
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client());
builder.Services.AddScoped<IAttachmentStore>(services =>
    new S3AttachmentStore(
        services.GetRequiredService<IAmazonS3>(),
        new S3AttachmentStoreOptions
        {
            BucketName = "chat-ciphertext",
            KeyPrefix = "attachments/"
        }));
builder.Services.AddScoped<IAttachmentAccessAuthorizer, MyConversationAttachmentAuthorizer>();
builder.Services.AddSkopkaChatAttachmentStorage();
```

`MapSkopkaChatApi` adds attachment routes only when `AddSkopkaChatAttachmentStorage` was called. The host remains responsible for TLS, token validation, proxy/request limits, rate limits, quotas, S3 credentials/bucket policy, database/object encryption at rest, lifecycle cleanup, backups and monitoring. The common contract caps one ciphertext at 5 GiB; S3 multipart/resume and HTTP range requests are not implemented.

## UI boundary

`ChatConversationProjection.SnapshotTimeline()` and `ChatViewModel.Timeline` contain text and `ProjectedChatAttachment` items. The default Blazor card raises `AttachmentDownloadRequested`; it never places storage URLs in `<img>`, `<audio>` or `<video>`. A host may replace it through `AttachmentTemplate`, but must render only locally authenticated/decrypted media and must control browser/object-URL lifetime.

Attachments can receive replies and reactions. Forwarding an attachment is intentionally not implemented: safe forwarding requires a new upload/key/manifest policy and must not silently grant another conversation access to the original blob.

The canonical format and security rationale are recorded in [ADR 0011](adr/0011-encrypted-attachments-and-storage.md). Operational limitations remain in [mvp-limitations.md](mvp-limitations.md).
