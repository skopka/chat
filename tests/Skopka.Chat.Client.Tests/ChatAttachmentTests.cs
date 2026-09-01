using Skopka.Chat.Attachments;
using Skopka.Chat.Protocol;

namespace Skopka.Chat.Client.Tests;

public sealed class ChatAttachmentTests
{
    [Fact]
    public void Attachment_manifest_v2_has_stable_golden_bytes_and_round_trips()
    {
        var manifest = new ChatAttachmentContent(
            new ChatContentId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")),
            new AttachmentId(Guid.Parse("11112222-3333-4444-5555-666677778888")),
            "résumé.pdf",
            "application/pdf",
            3,
            23,
            4096,
            Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray(),
            Enumerable.Range(32, 32).Select(static value => (byte)value).ToArray(),
            Enumerable.Range(64, 16).Select(static value => (byte)value).ToArray(),
            "doc",
            new ChatContentId(Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100")));

        var encoded = ChatContentEncoding.Encode(manifest);
        var decoded = Assert.IsType<ChatAttachmentContent>(ChatContentEncoding.Decode(encoded));

        Assert.Equal(
            "736B6F706B612E636861742E636F6E74656E74324100112233445566778899AABBCCDDEEFF1111222233334444555566667777888803FFEEDDCCBBAA998877665544332211000000000000000003000000000000001700001000000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F404142434445464748494A4B4C4D4E4F000C72C3A973756DC3A92E706466000F6170706C69636174696F6E2F7064660003646F63",
            Convert.ToHexString(encoded));
        Assert.Equal(manifest.AttachmentId, decoded.AttachmentId);
        Assert.Equal(manifest.FileName, decoded.FileName);
        Assert.Equal(manifest.MediaType, decoded.MediaType);
        Assert.Equal(manifest.Caption, decoded.Caption);
        Assert.Equal(manifest.ReplyToContentId, decoded.ReplyToContentId);
        Assert.True(manifest.FileKey.Span.SequenceEqual(decoded.FileKey.Span));
        Assert.DoesNotContain(manifest.FileName, decoded.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chunked_attachment_crypto_round_trips_multiple_chunks_and_empty_files()
    {
        var bytes = Enumerable.Range(0, 10_000).Select(static value => (byte)(value % 251)).ToArray();
        await using var source = new MemoryStream(bytes, writable: false);
        await using var encrypted = new MemoryStream();
        var manifest = await ChatAttachmentCryptoService.EncryptAsync(
            source,
            bytes.Length,
            encrypted,
            AttachmentId.New(),
            ChatContentId.New(),
            "photo.bin",
            "application/octet-stream",
            chunkPlaintextBytes: ChatAttachmentCryptoService.MinChunkPlaintextBytes);

        Assert.Equal(bytes.Length + (3 * 20), encrypted.Length);
        encrypted.Position = 0;
        await using var decrypted = new MemoryStream();
        await ChatAttachmentCryptoService.DecryptAsync(manifest, encrypted, decrypted);
        Assert.Equal(bytes, decrypted.ToArray());

        await using var emptySource = new MemoryStream();
        await using var emptyEncrypted = new MemoryStream();
        var emptyManifest = await ChatAttachmentCryptoService.EncryptAsync(
            emptySource,
            0,
            emptyEncrypted,
            AttachmentId.New(),
            ChatContentId.New(),
            "empty.txt",
            "text/plain");
        Assert.Equal(20, emptyEncrypted.Length);
        emptyEncrypted.Position = 0;
        await using var emptyDestination = new MemoryStream();
        await ChatAttachmentCryptoService.DecryptAsync(emptyManifest, emptyEncrypted, emptyDestination);
        Assert.Empty(emptyDestination.ToArray());
    }

    [Fact]
    public async Task Attachment_crypto_rejects_tampering_and_declared_length_mismatch()
    {
        var bytes = "classified attachment"u8.ToArray();
        await using var source = new MemoryStream(bytes, writable: false);
        await using var encrypted = new MemoryStream();
        var manifest = await ChatAttachmentCryptoService.EncryptAsync(
            source,
            bytes.Length,
            encrypted,
            AttachmentId.New(),
            ChatContentId.New(),
            "note.txt",
            "text/plain");
        var tampered = encrypted.ToArray();
        tampered[^1] ^= 1;

        await using var tamperedSource = new MemoryStream(tampered, writable: false);
        await using var rejectedDestination = new MemoryStream();
        await Assert.ThrowsAsync<ChatCryptographicException>(async () =>
            await ChatAttachmentCryptoService.DecryptAsync(manifest, tamperedSource, rejectedDestination));

        await using var shortSource = new MemoryStream(bytes, writable: false);
        await using var unused = new MemoryStream();
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await ChatAttachmentCryptoService.EncryptAsync(
                shortSource,
                bytes.Length + 1,
                unused,
                AttachmentId.New(),
                ChatContentId.New(),
                "note.txt",
                "text/plain"));
    }

    [Fact]
    public void Projection_includes_attachments_and_folds_reactions_onto_them()
    {
        var conversationId = ConversationId.New();
        var sender = UserId.New();
        var targetId = ChatContentId.New();
        var manifest = Manifest(targetId);
        var projection = new ChatConversationProjection(conversationId);
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(ChatProjectionApplyResult.Applied, projection.Apply(new ReceivedChatContent(
            MessageId.New(), conversationId, sender, DeviceId.New(), now, manifest)));
        Assert.Equal(ChatProjectionApplyResult.Applied, projection.Apply(new ReceivedChatContent(
            MessageId.New(),
            conversationId,
            sender,
            DeviceId.New(),
            now.AddSeconds(1),
            new ChatReactionContent(ChatContentId.New(), targetId, "👍", ChatReactionOperation.Add))));

        Assert.Empty(projection.Snapshot());
        var attachment = Assert.IsType<ProjectedChatAttachment>(Assert.Single(projection.SnapshotTimeline()));
        Assert.Equal("image.jpg", attachment.FileName);
        Assert.Equal("👍", Assert.Single(attachment.Reactions).Reaction);
    }

    [Fact]
    public void Projection_applies_and_clears_an_author_attachment_caption_edit()
    {
        var conversationId = ConversationId.New();
        var sender = UserId.New();
        var targetId = ChatContentId.New();
        var manifest = Manifest(targetId, "original caption");
        var projection = new ChatConversationProjection(conversationId);
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        projection.Apply(new ReceivedChatContent(
            MessageId.New(), conversationId, sender, DeviceId.New(), now, manifest));
        projection.Apply(new ReceivedChatContent(
            MessageId.New(),
            conversationId,
            sender,
            DeviceId.New(),
            now.AddSeconds(1),
            new ChatEditContent(
                ChatContentId.New(),
                targetId,
                ChatEditField.AttachmentCaption,
                null)));

        var attachment = Assert.IsType<ProjectedChatAttachment>(Assert.Single(projection.SnapshotTimeline()));
        Assert.Null(attachment.Caption);
        Assert.True(attachment.IsEdited);
        Assert.Equal(now.AddSeconds(1), attachment.EditedAt);
        Assert.Equal("original caption", attachment.Manifest.Caption);
    }

    private static ChatAttachmentContent Manifest(ChatContentId contentId, string? caption = null) => new(
        contentId,
        AttachmentId.New(),
        "image.jpg",
        "image/jpeg",
        1,
        21,
        ChatAttachmentCryptoService.DefaultChunkPlaintextBytes,
        new byte[32],
        Enumerable.Repeat((byte)1, 32).ToArray(),
        Enumerable.Repeat((byte)2, 16).ToArray(),
        caption);
}
