using System.Security.Cryptography;
using Microsoft.JSInterop;

namespace Skopka.Chat.Client.Browser;

/// <summary>Browser-only libsodium.js primitives and .NET HKDF-SHA256; no protocol encoding in JavaScript.</summary>
public sealed class BrowserChatCryptography : IChatCryptographyProvider, IAsyncDisposable
{
    private readonly IJSInProcessObjectReference _module;
    private BrowserChatCryptography(IJSInProcessObjectReference module) => _module = module;

    /// <summary>Loads pinned same-origin resources. Only Blazor WebAssembly is supported, never a server circuit.</summary>
    public static async ValueTask<BrowserChatCryptography> CreateAsync(IJSRuntime runtime, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsBrowser() || runtime is not IJSInProcessRuntime)
        {
            throw new PlatformNotSupportedException("Browser cryptography requires WebAssembly.");
        }
        try
        {
            var module = await runtime.InvokeAsync<IJSInProcessObjectReference>("import", cancellationToken,
                "./_content/Skopka.Chat.Client.Browser/crypto.mjs").ConfigureAwait(false);
            try
            {
                await module.InvokeAsync<bool>("ready", cancellationToken).ConfigureAwait(false);
                return new BrowserChatCryptography(module);
            }
            catch { await module.DisposeAsync().ConfigureAwait(false); throw; }
        }
        catch (JSException) { throw Failure(); }
    }

    /// <inheritdoc />
    public byte[] CreatePrivateKey(ChatKeyAlgorithm algorithm)
    {
        var raw = Invoke("randomKey");
        try { return PortableChatPrivateKey.Encode(algorithm, raw); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    /// <inheritdoc />
    public byte[] GetPublicKey(ChatKeyAlgorithm algorithm, ReadOnlySpan<byte> privateKey)
    {
        var raw = PortableChatPrivateKey.Decode(algorithm, privateKey);
        try { return Invoke("publicKey", (int)algorithm, raw); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    /// <inheritdoc />
    public byte[] DeriveEnvelopeKey(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info)
    {
        var raw = PortableChatPrivateKey.Decode(ChatKeyAlgorithm.X25519, privateKey);
        byte[]? shared = null;
        try
        {
            shared = Invoke("agreement", raw, publicKey.ToArray());
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt.ToArray(), info.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
            if (shared is not null) { CryptographicOperations.ZeroMemory(shared); }
        }
    }

    /// <inheritdoc />
    public byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext) =>
        Invoke("encrypt", key.ToArray(), nonce.ToArray(), associatedData.ToArray(), plaintext.ToArray());

    /// <inheritdoc />
    public byte[]? Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> ciphertext)
    {
        return InvokeOptional("decrypt", key.ToArray(), nonce.ToArray(), associatedData.ToArray(), ciphertext.ToArray());
    }

    /// <inheritdoc />
    public byte[] Sign(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message)
    {
        var raw = PortableChatPrivateKey.Decode(ChatKeyAlgorithm.Ed25519, privateKey);
        try { return Invoke("sign", raw, message.ToArray()); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    /// <inheritdoc />
    public bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        try { return _module.Invoke<bool>("verify", publicKey.ToArray(), message.ToArray(), signature.ToArray()); }
        catch (JSException) { throw Failure(); }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _module.DisposeAsync();

    private byte[] Invoke(string operation, params object?[] arguments)
    {
        return InvokeOptional(operation, arguments) ?? throw Failure();
    }
    private byte[]? InvokeOptional(string operation, params object?[] arguments)
    {
        try { return _module.Invoke<byte[]?>(operation, arguments); }
        catch (JSException) { throw Failure(); }
        finally
        {
            foreach (var argument in arguments)
            {
                if (argument is byte[] bytes) { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
    }
    private static ChatCryptographicException Failure() => new("Browser cryptographic operation failed.");
}
