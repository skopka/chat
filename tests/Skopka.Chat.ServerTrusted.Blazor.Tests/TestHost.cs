// The interactive test renderer derives from the framework Renderer on purpose (no bUnit dependency).
#pragma warning disable BL0006

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Skopka.Chat.Attachments;
using Skopka.Chat.Protocol;
using Skopka.Chat.ServerTrusted;
using Skopka.Chat.ServerTrusted.Blazor;

namespace Skopka.Chat.ServerTrusted.Blazor.Tests;

internal sealed class TestHost : IAsyncDisposable
{
    public static readonly Guid Learner = Guid.Parse("20000000-0000-0000-0000-000000000001");
    public static readonly Guid Teacher = Guid.Parse("20000000-0000-0000-0000-000000000002");
    public static readonly Guid Parent = Guid.Parse("20000000-0000-0000-0000-000000000003");
    public static readonly Guid Stranger = Guid.Parse("20000000-0000-0000-0000-000000000009");
    public static readonly Guid Moderator = Guid.Parse("20000000-0000-0000-0000-00000000000a");

    private TestHost(ServiceProvider provider)
    {
        Provider = provider;
    }

    public ServiceProvider Provider { get; }

    public TestAccessPolicy Policy => Provider.GetRequiredService<TestAccessPolicy>();

    public InProcessServerTrustedChatSignalHub Hub => Provider.GetRequiredService<InProcessServerTrustedChatSignalHub>();

    public static TestHost Create(Action<IServiceCollection>? configure = null, bool interactiveJs = false, bool attachments = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<InMemoryServerTrustedChatStore>();
        services.AddSingleton<IServerTrustedChatStore>(provider => provider.GetRequiredService<InMemoryServerTrustedChatStore>());
        services.AddSingleton<TestAccessPolicy>();
        services.AddSingleton<IServerTrustedChatAccessPolicy>(provider => provider.GetRequiredService<TestAccessPolicy>());
        services.AddSingleton<IServerTrustedChatRateLimiter, UnlimitedServerTrustedChatRateLimiter>();
        services.AddSingleton(new ServerTrustedChatOptions());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<InProcessServerTrustedChatSignalHub>();
        services.AddSingleton<IServerTrustedChatSignalSink>(provider => provider.GetRequiredService<InProcessServerTrustedChatSignalHub>());
        services.AddSingleton<IServerTrustedChatLiveSignals>(provider => provider.GetRequiredService<InProcessServerTrustedChatSignalHub>());
        services.AddScoped(provider => new ServerTrustedChatService(
            provider.GetRequiredService<IServerTrustedChatStore>(),
            provider.GetRequiredService<IServerTrustedChatAccessPolicy>(),
            provider.GetRequiredService<IServerTrustedChatRateLimiter>(),
            provider.GetRequiredService<ServerTrustedChatOptions>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IServerTrustedChatSignalSink>(),
            provider.GetService<IAttachmentStore>(),
            provider.GetService<IServerTrustedAttachmentValidator>()));
        if (attachments)
        {
            services.AddSingleton<IAttachmentStore, InMemoryBlobStore>();
            services.AddSingleton<IServerTrustedAttachmentValidator, AcceptingValidator>();
        }

        if (interactiveJs)
        {
            services.AddSingleton<VisibleDocumentJsRuntime>();
            services.AddSingleton<IJSRuntime>(provider => provider.GetRequiredService<VisibleDocumentJsRuntime>());
        }
        else
        {
            services.AddSingleton<IJSRuntime, ThrowingJsRuntime>();
        }

        configure?.Invoke(services);
        return new TestHost(services.BuildServiceProvider());
    }

    public IServerTrustedChatOperationRunner CreateRunner() =>
        new ScopedServerTrustedChatOperationRunner(Provider.GetRequiredService<IServiceScopeFactory>());

    public async Task<T> WithServiceAsync<T>(Func<ServerTrustedChatService, ValueTask<T>> action)
    {
        var scope = Provider.CreateAsyncScope();
        await using (scope)
        {
            return await action(scope.ServiceProvider.GetRequiredService<ServerTrustedChatService>());
        }
    }

    public async Task<TrustedConversationId> CreateConversationAsync(params TrustedParticipantSpec[] participants)
    {
        var writers = participants.Count(item => item.Role == TrustedParticipantRole.Writer);
        var result = await WithServiceAsync(service => service.GetOrCreateConversationAsync(
            $"test:{Guid.NewGuid():N}",
            writers == 2 ? TrustedConversationKind.Direct : TrustedConversationKind.Group,
            participants,
            TestContext.Current.CancellationToken));
        Assert.True(result.IsSuccess);
        return result.Value!.Id;
    }

    public Task<TrustedConversationId> CreateLearnerTeacherAsync(bool withParent = false) =>
        withParent
            ? CreateConversationAsync(
                new TrustedParticipantSpec(Learner, TrustedParticipantRole.Writer, "learner"),
                new TrustedParticipantSpec(Teacher, TrustedParticipantRole.Writer, "teacher"),
                new TrustedParticipantSpec(Parent, TrustedParticipantRole.Observer, "parent"))
            : CreateConversationAsync(
                new TrustedParticipantSpec(Learner, TrustedParticipantRole.Writer, "learner"),
                new TrustedParticipantSpec(Teacher, TrustedParticipantRole.Writer, "teacher"));

    public async Task<TrustedMessage> SendAsync(
        TrustedConversationId conversationId,
        Guid author,
        string? text,
        TrustedMessageId? replyTo = null,
        IReadOnlyList<HostReference>? references = null,
        IReadOnlyList<TrustedAttachmentId>? attachments = null)
    {
        var result = await WithServiceAsync(service => service.SendAsync(author, conversationId, new TrustedSendRequest
        {
            ClientMessageId = Guid.NewGuid(),
            Text = text,
            ReplyToMessageId = replyTo,
            References = references ?? [],
            AttachmentIds = attachments ?? [],
        }, TestContext.Current.CancellationToken));
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    public async Task<TrustedAttachmentInfo> UploadAsync(TrustedConversationId conversationId, Guid uploader, string fileName)
    {
        using var content = new MemoryStream([1, 2, 3, 4]);
        var result = await WithServiceAsync(service => service.UploadAttachmentAsync(uploader, conversationId, fileName, "image/png", content, TestContext.Current.CancellationToken));
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    public async Task<IReadOnlyList<TrustedMessage>> HistoryAsync(TrustedConversationId conversationId, Guid reader)
    {
        var result = await WithServiceAsync(service => service.GetHistoryAsync(reader, conversationId, null, 100, TestContext.Current.CancellationToken));
        Assert.True(result.IsSuccess);
        return result.Value!.Messages;
    }

    public async Task<string> RenderAsync<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        var loggerFactory = Provider.GetRequiredService<ILoggerFactory>();
        await using var renderer = new HtmlRenderer(Provider, loggerFactory);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<TComponent>(ParameterView.FromDictionary(parameters));
            return root.ToHtmlString();
        });
    }

    public async Task<IReadOnlyDictionary<string, byte[]>> RenderStaticAndPersistAsync<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        var manager = Provider.GetRequiredService<ComponentStatePersistenceManager>();
        var store = new CapturingStateStore();
        await using var renderer = new InteractiveTestRenderer(Provider, Provider.GetRequiredService<ILoggerFactory>(), isInteractive: false);
        await renderer.RenderAsync<TComponent>(parameters);
        await renderer.Dispatcher.InvokeAsync(() => manager.PersistStateAsync(store, renderer));
        Assert.Empty(renderer.Exceptions);
        return store.State;
    }

    public ValueTask DisposeAsync() => Provider.DisposeAsync();

    public static Task WaitUntilAsync(Func<Task<bool>> condition) => WaitUntilAsync(condition, TimeSpan.FromSeconds(10));

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The condition was not reached in time.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}

internal static class ServiceCollectionPersistenceExtensions
{
    public static IServiceCollection AddTestComponentStatePersistence(this IServiceCollection services)
    {
        services.AddSingleton<ComponentStatePersistenceManager>();
        services.AddSingleton(provider => provider.GetRequiredService<ComponentStatePersistenceManager>().State);
        return services;
    }
}

internal sealed class TestAccessPolicy : IServerTrustedChatAccessPolicy
{
    private volatile Func<ServerTrustedAccessContext, bool> _allow = static _ => true;

    public Func<ServerTrustedAccessContext, bool> Allow
    {
        get => _allow;
        set => _allow = value;
    }

    public ValueTask<bool> AuthorizeAsync(ServerTrustedAccessContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_allow(context));
}

internal sealed class CapturingStateStore : IPersistentComponentStateStore
{
    public Dictionary<string, byte[]> State { get; } = new(StringComparer.Ordinal);

    public Task<IDictionary<string, byte[]>> GetPersistedStateAsync() => Task.FromResult<IDictionary<string, byte[]>>(State);

    public Task PersistStateAsync(IReadOnlyDictionary<string, byte[]> state)
    {
        foreach (var (key, value) in state)
        {
            State[key] = value;
        }

        return Task.CompletedTask;
    }
}

/// <summary>Proves that prerender and static rendering never touch JavaScript.</summary>
internal sealed class ThrowingJsRuntime : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        throw new InvalidOperationException("JavaScript must not run during static rendering.");

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        throw new InvalidOperationException("JavaScript must not run during static rendering.");
}

/// <summary>Interactive fake: the visibility module reports a visible document.</summary>
internal sealed class VisibleDocumentJsRuntime : IJSRuntime
{
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        Record(identifier == "import" ? $"import:{args?[0]}" : identifier);
        return ValueTask.FromResult((TValue)(object)new FakeObject(this));
    }

    private void Record(string call)
    {
        lock (_calls)
        {
            _calls.Add(call);
        }
    }

    private sealed class FakeObject(VisibleDocumentJsRuntime owner) : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            owner.Record(identifier);
            object result = identifier switch
            {
                "isVisible" => true,
                "observeVisibility" => new FakeObject(owner),
                _ => null!,
            };
            return ValueTask.FromResult((TValue)result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A minimal renderer; interactive by default, so the component runs its live path (signals, timer, JS, mark-read).</summary>
internal sealed class InteractiveTestRenderer : Renderer
{
    public InteractiveTestRenderer(IServiceProvider services, ILoggerFactory loggerFactory, bool isInteractive = true)
        : base(services, loggerFactory)
    {
        RendererInfo = new RendererInfo(isInteractive ? "Test" : "Static", isInteractive);
    }

    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    public List<Exception> Exceptions { get; } = [];

    protected override RendererInfo RendererInfo { get; }

    public Task<int> RenderAsync<TComponent>(Dictionary<string, object?> parameters)
        where TComponent : IComponent =>
        Dispatcher.InvokeAsync(async () =>
        {
            var component = InstantiateComponent(typeof(TComponent));
            var id = AssignRootComponentId(component);
            await RenderRootComponentAsync(id, ParameterView.FromDictionary(parameters));
            return id;
        });

    protected override void HandleException(Exception exception)
    {
        lock (Exceptions)
        {
            Exceptions.Add(exception);
        }
    }

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
}

/// <summary>Test-only blob store holding synthetic bytes in memory.</summary>
internal sealed class InMemoryBlobStore : IAttachmentStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<AttachmentId, (StoredAttachment Metadata, byte[] Bytes)> _blobs = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _blobs.Count;
            }
        }
    }

    public ValueTask<StoredAttachment?> GetMetadataAsync(AttachmentId attachmentId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_blobs.TryGetValue(attachmentId, out var item) ? item.Metadata : null);
        }
    }

    public async ValueTask<AttachmentStoreResult> TryPutAsync(StoredAttachment attachment, Stream ciphertext, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await ciphertext.CopyToAsync(buffer, cancellationToken);
        lock (_gate)
        {
            if (_blobs.ContainsKey(attachment.AttachmentId))
            {
                return AttachmentStoreResult.Conflict;
            }

            _blobs[attachment.AttachmentId] = (attachment, buffer.ToArray());
            return AttachmentStoreResult.Stored;
        }
    }

    public async ValueTask CopyToAsync(AttachmentId attachmentId, Stream destination, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        lock (_gate)
        {
            bytes = _blobs[attachmentId].Bytes;
        }

        await destination.WriteAsync(bytes, cancellationToken);
    }

    public ValueTask<bool> DeleteAsync(AttachmentId attachmentId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_blobs.Remove(attachmentId));
        }
    }

    public ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
}

/// <summary>Accepts every upload with its declared type, except file names starting with "reject".</summary>
internal sealed class AcceptingValidator : IServerTrustedAttachmentValidator
{
    public ValueTask<ServerTrustedAttachmentVerdict> ValidateAsync(ServerTrustedAttachmentCandidate candidate, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(candidate.FileName.StartsWith("reject", StringComparison.Ordinal)
            ? ServerTrustedAttachmentVerdict.Rejected
            : ServerTrustedAttachmentVerdict.Accept(candidate.DeclaredContentType));
}
