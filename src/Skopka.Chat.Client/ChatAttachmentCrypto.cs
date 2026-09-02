using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Skopka.Chat.Attachments;
using BclIncrementalHash = System.Security.Cryptography.IncrementalHash;

namespace Skopka.Chat.Client;

/// <summary>Streams encrypted attachment chunks without exposing plaintext to storage providers.</summary>
public static class ChatAttachmentCryptoService
{
    private const int TagBytes = 16;
    private const int NonceBytes = 24;
    private static readonly byte[] ChunkDomain = Encoding.ASCII.GetBytes("skopka.chat.attachment.chunk.v1");
    private const int FileKeyBytes = 32;
    private const int NoncePrefixBytes = 16;
    private const int FrameLengthBytes = 4;

    /// <summary>Default independently authenticated plaintext chunk size.</summary>
    public const int DefaultChunkPlaintextBytes = 64 * 1024;

    /// <summary>Smallest supported plaintext chunk size.</summary>
    public const int MinChunkPlaintextBytes = 4 * 1024;

    /// <summary>Largest supported plaintext chunk size.</summary>
    public const int MaxChunkPlaintextBytes = 1024 * 1024;

    /// <summary>
    /// Encrypts exactly <paramref name="plaintextLength"/> bytes and returns the E2EE manifest to send in an envelope.
    /// The caller must discard the ciphertext destination if this method fails.
    /// </summary>
    public static ValueTask<ChatAttachmentContent> EncryptAsync(
        Stream plaintext,
        long plaintextLength,
        Stream ciphertext,
        AttachmentId attachmentId,
        ChatContentId contentId,
        string fileName,
        string mediaType,
        string? caption = null,
        ChatContentId? replyToContentId = null,
        int chunkPlaintextBytes = DefaultChunkPlaintextBytes,
        CancellationToken cancellationToken = default) =>
        EncryptAsync(ChatCryptographyDefaults.Create(), plaintext, plaintextLength, ciphertext, attachmentId, contentId,
            fileName, mediaType, caption, replyToContentId, chunkPlaintextBytes, cancellationToken);

    /// <summary>Encrypts attachment chunks with an explicit endpoint provider and unchanged chunk-v1 framing.</summary>
    public static async ValueTask<ChatAttachmentContent> EncryptAsync(
        IChatCryptographyProvider cryptography, Stream plaintext, long plaintextLength, Stream ciphertext,
        AttachmentId attachmentId, ChatContentId contentId, string fileName, string mediaType,
        string? caption = null, ChatContentId? replyToContentId = null, int chunkPlaintextBytes = DefaultChunkPlaintextBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cryptography);
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!plaintext.CanRead)
        {
            throw new ArgumentException("Plaintext stream must be readable.", nameof(plaintext));
        }

        if (!ciphertext.CanWrite)
        {
            throw new ArgumentException("Ciphertext stream must be writable.", nameof(ciphertext));
        }

        if (!TryGetCiphertextLength(plaintextLength, chunkPlaintextBytes, out var ciphertextLength))
        {
            throw new ArgumentOutOfRangeException(nameof(plaintextLength), "Attachment length is outside the supported range.");
        }

        var fileKey = RandomNumberGenerator.GetBytes(FileKeyBytes);
        var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixBytes);
        var plaintextBuffer = ArrayPool<byte>.Shared.Rent(chunkPlaintextBytes);
        var encryptedBuffer = ArrayPool<byte>.Shared.Rent(chunkPlaintextBytes + TagBytes);
        try
        {
            using var hash = BclIncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var chunkCount = GetChunkCount(plaintextLength, chunkPlaintextBytes);
            var remaining = plaintextLength;
            for (long chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunkLength = plaintextLength == 0
                    ? 0
                    : checked((int)Math.Min(remaining, chunkPlaintextBytes));
                await ReadExactlyAsync(
                    plaintext,
                    plaintextBuffer.AsMemory(0, chunkLength),
                    cancellationToken).ConfigureAwait(false);

                var final = chunkIndex == chunkCount - 1;
                var frameLength = new byte[FrameLengthBytes];
                BinaryPrimitives.WriteInt32BigEndian(frameLength, chunkLength);
                var nonce = CreateNonce(noncePrefix, chunkIndex);
                var associatedData = CreateAssociatedData(
                    attachmentId,
                    chunkIndex,
                    plaintextLength,
                    chunkLength,
                    final);
                var encryptedLength = chunkLength + TagBytes;
                var encrypted = cryptography.Encrypt(
                    fileKey,
                    nonce,
                    associatedData,
                    plaintextBuffer.AsSpan(0, chunkLength));
                encrypted.CopyTo(encryptedBuffer, 0);
                CryptographicOperations.ZeroMemory(encrypted);

                await ciphertext.WriteAsync(frameLength, cancellationToken).ConfigureAwait(false);
                await ciphertext.WriteAsync(encryptedBuffer.AsMemory(0, encryptedLength), cancellationToken)
                    .ConfigureAwait(false);
                hash.AppendData(frameLength);
                hash.AppendData(encryptedBuffer.AsSpan(0, encryptedLength));
                remaining -= chunkLength;
            }

            var extra = new byte[1];
            if (await plaintext.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new InvalidDataException("Plaintext stream exceeds its declared length.");
            }

            var digest = hash.GetHashAndReset();
            return new ChatAttachmentContent(
                contentId,
                attachmentId,
                fileName,
                mediaType,
                plaintextLength,
                ciphertextLength,
                chunkPlaintextBytes,
                digest,
                fileKey,
                noncePrefix,
                caption,
                replyToContentId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
            CryptographicOperations.ZeroMemory(plaintextBuffer.AsSpan(0, chunkPlaintextBytes));
            CryptographicOperations.ZeroMemory(encryptedBuffer.AsSpan(0, chunkPlaintextBytes + TagBytes));
            ArrayPool<byte>.Shared.Return(plaintextBuffer);
            ArrayPool<byte>.Shared.Return(encryptedBuffer);
        }
    }

    /// <summary>
    /// Authenticates and decrypts a complete stored blob. The caller must discard the destination if this method fails.
    /// </summary>
    public static ValueTask DecryptAsync(
        ChatAttachmentContent manifest,
        Stream ciphertext,
        Stream plaintext,
        CancellationToken cancellationToken = default) =>
        DecryptAsync(ChatCryptographyDefaults.Create(), manifest, ciphertext, plaintext, cancellationToken);

    /// <summary>Decrypts unchanged chunk-v1 framing with an explicit provider; discard partial output on failure.</summary>
    public static async ValueTask DecryptAsync(IChatCryptographyProvider cryptography, ChatAttachmentContent manifest,
        Stream ciphertext, Stream plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cryptography);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!ciphertext.CanRead)
        {
            throw new ArgumentException("Ciphertext stream must be readable.", nameof(ciphertext));
        }

        if (!plaintext.CanWrite)
        {
            throw new ArgumentException("Plaintext stream must be writable.", nameof(plaintext));
        }

        var encryptedBuffer = ArrayPool<byte>.Shared.Rent(manifest.ChunkPlaintextBytes + TagBytes);
        var plaintextBuffer = ArrayPool<byte>.Shared.Rent(manifest.ChunkPlaintextBytes);
        try
        {
            using var hash = BclIncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var chunkCount = GetChunkCount(manifest.PlaintextLength, manifest.ChunkPlaintextBytes);
            var remaining = manifest.PlaintextLength;
            var frameLength = new byte[FrameLengthBytes];
            for (long chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadExactlyAsync(ciphertext, frameLength, cancellationToken).ConfigureAwait(false);
                var chunkLength = BinaryPrimitives.ReadInt32BigEndian(frameLength);
                var expectedLength = manifest.PlaintextLength == 0
                    ? 0
                    : checked((int)Math.Min(remaining, manifest.ChunkPlaintextBytes));
                if (chunkLength != expectedLength)
                {
                    throw new ChatCryptographicException("Attachment authentication failed.");
                }

                var encryptedLength = chunkLength + TagBytes;
                await ReadExactlyAsync(
                    ciphertext,
                    encryptedBuffer.AsMemory(0, encryptedLength),
                    cancellationToken).ConfigureAwait(false);
                hash.AppendData(frameLength);
                hash.AppendData(encryptedBuffer.AsSpan(0, encryptedLength));

                var final = chunkIndex == chunkCount - 1;
                var nonce = CreateNonce(manifest.NoncePrefix.Span, chunkIndex);
                var associatedData = CreateAssociatedData(
                    manifest.AttachmentId,
                    chunkIndex,
                    manifest.PlaintextLength,
                    chunkLength,
                    final);
                var decrypted = cryptography.Decrypt(
                        manifest.FileKey.Span,
                        nonce,
                        associatedData,
                        encryptedBuffer.AsSpan(0, encryptedLength));
                if (decrypted is null)
                {
                    throw new ChatCryptographicException("Attachment authentication failed.");
                }
                decrypted.CopyTo(plaintextBuffer, 0);
                CryptographicOperations.ZeroMemory(decrypted);

                await plaintext.WriteAsync(plaintextBuffer.AsMemory(0, chunkLength), cancellationToken)
                    .ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(plaintextBuffer.AsSpan(0, chunkLength));
                remaining -= chunkLength;
            }

            var extra = new byte[1];
            if (await ciphertext.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0 ||
                !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), manifest.CiphertextSha256.Span))
            {
                throw new ChatCryptographicException("Attachment authentication failed.");
            }
        }
        catch (EndOfStreamException)
        {
            throw new ChatCryptographicException("Attachment authentication failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedBuffer.AsSpan(0, manifest.ChunkPlaintextBytes + TagBytes));
            CryptographicOperations.ZeroMemory(plaintextBuffer.AsSpan(0, manifest.ChunkPlaintextBytes));
            ArrayPool<byte>.Shared.Return(encryptedBuffer);
            ArrayPool<byte>.Shared.Return(plaintextBuffer);
        }
    }

    internal static bool TryGetCiphertextLength(long plaintextLength, int chunkPlaintextBytes, out long ciphertextLength)
    {
        ciphertextLength = 0;
        if (plaintextLength < 0 ||
            chunkPlaintextBytes < MinChunkPlaintextBytes ||
            chunkPlaintextBytes > MaxChunkPlaintextBytes)
        {
            return false;
        }

        try
        {
            var chunkCount = GetChunkCount(plaintextLength, chunkPlaintextBytes);
            ciphertextLength = checked(plaintextLength + (chunkCount * (FrameLengthBytes + TagBytes)));
            return ciphertextLength <= AttachmentStorageLimits.MaxCiphertextBytes;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static long GetChunkCount(long plaintextLength, int chunkPlaintextBytes) =>
        plaintextLength == 0 ? 1 : checked(((plaintextLength - 1) / chunkPlaintextBytes) + 1);

    private static byte[] CreateNonce(ReadOnlySpan<byte> noncePrefix, long chunkIndex)
    {
        var nonce = new byte[NonceBytes];
        noncePrefix.CopyTo(nonce);
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(NoncePrefixBytes), chunkIndex);
        return nonce;
    }

    private static byte[] CreateAssociatedData(
        AttachmentId attachmentId,
        long chunkIndex,
        long plaintextLength,
        int chunkLength,
        bool final)
    {
        var result = new byte[ChunkDomain.Length + 16 + sizeof(long) + sizeof(long) + sizeof(int) + 1];
        var remaining = result.AsSpan();
        ChunkDomain.CopyTo(remaining);
        remaining = remaining[ChunkDomain.Length..];
        if (!attachmentId.Value.TryWriteBytes(remaining[..16], bigEndian: true, out var written) || written != 16)
        {
            throw new InvalidOperationException("Could not encode an attachment UUID.");
        }

        remaining = remaining[16..];
        BinaryPrimitives.WriteInt64BigEndian(remaining, chunkIndex);
        remaining = remaining[sizeof(long)..];
        BinaryPrimitives.WriteInt64BigEndian(remaining, plaintextLength);
        remaining = remaining[sizeof(long)..];
        BinaryPrimitives.WriteInt32BigEndian(remaining, chunkLength);
        remaining[sizeof(int)] = final ? (byte)1 : (byte)0;
        return result;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream source,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var current = await source.ReadAsync(destination[read..], cancellationToken).ConfigureAwait(false);
            if (current == 0)
            {
                throw new EndOfStreamException();
            }

            read += current;
        }
    }
}
