using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SharpFuzz;
using Skopka.Chat.Client;
using Skopka.Chat.Protocol;
using Skopka.Chat.Transport.Http;

if (args is ["--replay", var corpusPath])
{
    ChatFuzzTarget.Replay(corpusPath);
    return;
}

if (args.Length != 0)
{
    throw new ArgumentException("Usage: Skopka.Chat.FuzzTests [--replay <corpus-directory>].", nameof(args));
}

Fuzzer.OutOfProcess.Run(ChatFuzzTarget.Run);

internal static class ChatFuzzTarget
{
    private const int MaximumInputBytes = (int)SkopkaChatHttpLimits.MaxRequestBodyBytes + 1;

    internal static void Run(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bytes = ReadBounded(input);
        if (bytes is not null)
        {
            Run(bytes);
        }
    }

    internal static void Replay(string corpusPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusPath);
        if (!Directory.Exists(corpusPath))
        {
            throw new DirectoryNotFoundException($"Fuzz corpus directory was not found: {corpusPath}");
        }

        foreach (var path in Directory.EnumerateFiles(corpusPath, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            Run(File.ReadAllBytes(path));
        }
    }

    private static void Run(ReadOnlySpan<byte> input)
    {
        var separator = input.IndexOf((byte)'\n');
        var selector = SelectTarget(input, separator);
        var json = separator is >= 1 and <= 32 ? input[(separator + 1)..] : input;

        try
        {
            switch (selector)
            {
                case 0:
                    RoundTrip(json, SkopkaChatHttpJsonContext.Default.RegisterDeviceRequest);
                    break;
                case 1:
                    RoundTrip(json, SkopkaChatHttpJsonContext.Default.CreateConversationRequest);
                    break;
                case 2:
                    var device = RoundTrip(json, SkopkaChatHttpJsonContext.Default.PublicDeviceResponse);
                    _ = device?.ToDomain();
                    break;
                case 3:
                    RoundTrip(json, SkopkaChatHttpJsonContext.Default.PersonalConversationResponse);
                    break;
                case 4:
                    var envelope = RoundTrip(json, SkopkaChatHttpJsonContext.Default.EncryptedEnvelopeDto);
                    _ = envelope?.ToDomain();
                    break;
                case 5:
                    var deliveries = RoundTrip(
                        json,
                        SkopkaChatHttpJsonContext.Default.PendingDeliveryResponseArray);
                    if (deliveries is not null)
                    {
                        foreach (var delivery in deliveries)
                        {
                            _ = delivery?.Envelope?.ToDomain();
                        }
                    }

                    break;
                case 6:
                    RoundTrip(json, SkopkaChatHttpJsonContext.Default.SubmitEnvelopeResponse);
                    break;
                case 7:
                    var contentBytes = DecodeContentSeed(json);
                    var content = ChatContentEncoding.Decode(contentBytes);
                    _ = ChatContentEncoding.Decode(ChatContentEncoding.Encode(content));
                    break;
                case 8:
                    RoundTrip(json, SkopkaChatHttpJsonContext.Default.GetOrCreateConversationRequest);
                    break;
                case 9:
                    RoundTrip(json, SkopkaChatHttpJsonContext.Default.ConversationDirectoryResponse);
                    break;
                default:
                    var devices = RoundTrip(json, SkopkaChatHttpJsonContext.Default.DeviceDirectoryResponse);
                    if (devices is not null)
                    {
                        foreach (var item in devices.Items ?? [])
                        {
                            _ = item?.ToDomain();
                        }
                    }

                    break;
            }
        }
        catch (JsonException)
        {
            // Malformed or contract-invalid JSON is an expected fuzz outcome.
        }
        catch (ProtocolValidationException)
        {
            // Structurally invalid protocol values are an expected fuzz outcome.
        }
        catch (ChatContentFormatException)
        {
            // Malformed or unsupported authenticated content is an expected fuzz outcome.
        }
    }

    private static T? RoundTrip<T>(ReadOnlySpan<byte> json, JsonTypeInfo<T> typeInfo)
    {
        var value = JsonSerializer.Deserialize(json, typeInfo);
        if (value is not null)
        {
            var canonicalJson = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
            _ = JsonSerializer.Deserialize(canonicalJson, typeInfo) ??
                throw new InvalidOperationException("A serialized HTTP contract did not deserialize again.");
        }

        return value;
    }

    private static byte SelectTarget(ReadOnlySpan<byte> input, int separator)
    {
        if (separator is 1 or 2)
        {
            var value = 0;
            for (var index = 0; index < separator; index++)
            {
                if (input[index] is < (byte)'0' or > (byte)'9')
                {
                    value = -1;
                    break;
                }

                value = (value * 10) + input[index] - (byte)'0';
            }

            if (value is >= 0 and <= 10)
            {
                return (byte)value;
            }
        }

        return input.IsEmpty ? (byte)0 : (byte)(input[0] % 11);
    }

    private static byte[] DecodeContentSeed(ReadOnlySpan<byte> value)
    {
        if (!value.StartsWith("hex:"u8))
        {
            return value.ToArray();
        }

        try
        {
            return Convert.FromHexString(Encoding.ASCII.GetString(value[4..]).Trim());
        }
        catch (FormatException)
        {
            return value.ToArray();
        }
    }

    private static byte[]? ReadBounded(Stream input)
    {
        var buffer = new byte[MaximumInputBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = input.Read(buffer.AsSpan(total));
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total > MaximumInputBytes ? null : buffer.AsSpan(0, total).ToArray();
    }
}
