using Skopka.Chat.Protocol;
using Skopka.Chat.Transport.Http;

namespace Skopka.Chat.Client.Http;

public sealed partial class SkopkaChatHttpClient : IDeviceBindingTransport
{
    /// <summary>Account-authenticated bootstrap, before this persistent DeviceId has a server binding.</summary>
    public async ValueTask<DeviceBindingChallenge> IssueAsync(DeviceBindingOperation operation, PublicDevice device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ProtocolValidator.Validate(device);
        if (device.UserId != _authenticatedUserId || device.DeviceId != _authenticatedDeviceId || device.IsRevoked ||
            operation is not (DeviceBindingOperation.Enrollment or DeviceBindingOperation.Rebind))
        {
            throw new ArgumentException("Bootstrap identity does not match the configured client.");
        }
        try
        {
            // Issuance is not idempotent. A lost response leaves an expiring unused challenge.
            using var request = CreateJsonRequest(HttpMethod.Post, DeviceBindingHttpRoutes.Challenges,
                new DeviceBindingIssueRequest((int)operation, RegisterDeviceRequest.FromDomain(device)),
                SkopkaChatHttpJsonContext.Default.DeviceBindingIssueRequest);
            using var response = await SendOnceAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            var dto = await ReadJsonAsync(response, SkopkaChatHttpJsonContext.Default.DeviceBindingChallengeResponse,
                DeviceBindingHttpRoutes.MaximumBodyBytes, cancellationToken).ConfigureAwait(false);
            var challenge = dto.ToDomain();
            if (challenge.Operation != operation || !DeviceBindingEncoding.SameKeys(device, challenge.Device)) { throw InvalidResponse(); }
            return challenge;
        }
        catch (ChatHttpTransportException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Gone) { throw new DeviceBindingRevokedException(); }
        catch (ChatHttpTransportException exception) { throw new ChatHttpTransportException("Device bootstrap request failed.", exception.StatusCode); }
        catch (Exception exception) when (exception is ArgumentException or IOException or HttpRequestException)
        {
            throw new ChatHttpTransportException("Device bootstrap response was invalid.");
        }
    }

    /// <summary>Completes binding with bounded retries of the exact same challenge/signature.</summary>
    public async ValueTask<DeviceSessionBinding> CompleteAsync(DeviceBindingProof proof, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var request = new DeviceBindingCompleteRequest(proof.ChallengeId, proof.Signature.ToArray());
        try
        {
            using var response = await SendWithRetryAsync(() => CreateJsonRequest(HttpMethod.Post, DeviceBindingHttpRoutes.Completions,
                request, SkopkaChatHttpJsonContext.Default.DeviceBindingCompleteRequest), cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            var dto = await ReadJsonAsync(response, SkopkaChatHttpJsonContext.Default.DeviceBindingResultResponse,
                DeviceBindingHttpRoutes.MaximumBodyBytes, cancellationToken).ConfigureAwait(false);
            var binding = dto.ToDomain();
            if (binding.Context.UserId != _authenticatedUserId || binding.Device.DeviceId != _authenticatedDeviceId) { throw InvalidResponse(); }
            return binding;
        }
        catch (ChatHttpTransportException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Gone) { throw new DeviceBindingRevokedException(); }
        catch (ChatHttpTransportException exception) { throw new ChatHttpTransportException("Device bootstrap request failed.", exception.StatusCode); }
        catch (Exception exception) when (exception is ArgumentException or IOException or HttpRequestException)
        {
            throw new ChatHttpTransportException("Device bootstrap response was invalid.");
        }
    }
}
