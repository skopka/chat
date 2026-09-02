# Adaptable UI

The 0.15.0 browser target is exercised as a published standalone WASM
application, not inferred from component availability. UI.Blazor references the
Components.Web package instead of the ASP.NET Core shared framework. Use
[Client.Browser and the runnable sample](browser.md) for actual browser E2EE;
never prerender decrypted components or use Blazor Server for this integration.

`Skopka.Chat.UI.Core`, `Skopka.Chat.UI.Blazor` and `Skopka.Chat.UI.Maui` are optional client-side packages. They never run on the server and do not change the protocol-v1 envelope. Their timeline projects content-v1 text/reactions, content-v2 attachment manifests and author-validated content-v3 edits already authenticated by Client.

## Package boundary

```mermaid
flowchart LR
    Blazor[Skopka.Chat.UI.Blazor] --> Core[Skopka.Chat.UI.Core]
    Maui[Skopka.Chat.UI.Maui] --> Core
    Core --> Client[Skopka.Chat.Client]
    Client --> Protocol[Skopka.Chat.Protocol]
    Host[Host application] --> Blazor
    Host --> Maui
    Host --> Sender[IChatContentSender implementation]
    Sender --> Client
    Sender --> Transport[IChatTransport / host transport]
```

`ChatViewModel` owns an in-memory `Timeline` containing text and attachment items, a legacy text-only `Messages` view, bounded composer draft, reply/edit target and generic command-failure state. It accepts only `ReceivedChatContent`, so a host should call `ChatReceiver.ReceiveContentAsync` before `Apply`. It does not decrypt envelopes/files, upload ciphertext, enumerate devices, choose a transport, store history or log failures.

`IChatContentSender` is the replaceable host boundary. The repository's standard `MultiDeviceChatContentSender` composes `ChatMultiDeviceSender` and a local-echo committer. For one logical event that path:

1. keep the supplied `ChatContentId` stable;
2. enumerates current active peer and sibling devices through the authenticated directory;
3. create a distinct `MessageId` and encrypted envelope for each device;
4. stores the exact plan before network I/O and submits the envelopes with normal idempotency handling;
5. return `ChatContentSendResult.Success` with a matching authenticated local echo, or `ChatContentSendResult.Failed` for an expected bounded failure.

Unexpected programming failures and caller cancellation may still throw. Do not copy remote response bodies, plaintext or access tokens into exception messages or telemetry.

## Blazor component

Add the two coordinated packages and place the component in a Blazor page:

```razor
@using Skopka.Chat.UI
@using Skopka.Chat.UI.Blazor
@using Skopka.Chat.Client
@using Skopka.Chat.Protocol

<SkopkaChat ViewModel="Chat"
            CssClass="my-chat"
            Strings="RussianStrings"
            ReactionChoices="MyReactions"
            ForwardRequested="ChooseForwardTarget"
            AttachmentSender="SendBrowserAttachmentAsync"
            AttachmentDownloadRequested="DownloadAndDecrypt" />

@code {
    [Parameter, EditorRequired]
    public ChatViewModel Chat { get; set; } = null!;

    private static readonly SkopkaChatStrings RussianStrings = SkopkaChatStrings.Default with
    {
        EmptyConversation = "Сообщений пока нет",
        ComposerPlaceholder = "Напишите сообщение",
        Send = "Отправить",
        Reply = "Ответить",
        Forward = "Переслать",
        Edit = "Изменить",
        Edited = "изменено",
        EditingMessage = "Редактирование сообщения",
        EditingCaption = "Редактирование подписи",
        Save = "Сохранить",
    };

    private static readonly IReadOnlyList<string> MyReactions = ["👍", "🔥", "❤️"];

    private async Task ChooseForwardTarget(ProjectedChatMessage message)
    {
        ConversationId? target = await ShowConversationPickerAsync();
        if (target is not null)
        {
            await Chat.ForwardAsync(message.ContentId, target.Value);
        }
    }
}
```

The default Edit action is visible only for projected items whose authenticated `SenderUserId` equals `ChatViewModel.CurrentUserId`. `BeginEdit` reuses the composer for a text body or attachment caption, while preserving the prior unsent draft/reply state until save or cancel:

```csharp
chat.BeginEdit(ownItem.ContentId);
chat.SetDraftText("corrected text");
bool saved = await chat.TrySendDraftAsync();
```

Saving emits a new `ChatEditContent`; it never mutates the original server row. `IsEdited`/`EditedAt` identify the selected edit in projection. Empty attachment-caption input is encoded as a canonical clear operation. Blank text edits, another user's items, unchanged values and wrong target types are rejected or not sent. See [ADR 0013](adr/0013-encrypted-message-edits.md).

The package uses CSS isolation. A normal Blazor application includes referenced-library isolated styles through its generated application stylesheet. Scope brand overrides with `CssClass` or set custom properties through `Style`:

```css
.my-chat {
    --skopka-chat-background: #101418;
    --skopka-chat-surface: #182028;
    --skopka-chat-own-surface: #153c32;
    --skopka-chat-text: #f7fafc;
    --skopka-chat-muted: #a0aec0;
    --skopka-chat-accent: #52b788;
    --skopka-chat-border: #2d3748;
    --skopka-chat-radius: 1.1rem;
    --skopka-chat-font: Inter, system-ui, sans-serif;
    --skopka-chat-max-width: 64rem;
}
```

The standard composer shows a photo/video picker only when `AttachmentSender` is supplied. Its unchecked default uses media `Auto` mode; the user can select “Send as file” for byte-exact `File` mode. The callback owns browser stream limits and maps the selection to `Skopka.Chat.Media`; see [media.md](media.md) for the prepare → encrypt → upload example. Applications can omit the callback or replace `ComposerTemplate` to provide a different picker and policy.

## MAUI control

`SkopkaChatView` is the native equivalent over the same `ChatViewModel`:

```xml
<chat:SkopkaChatView
    ViewModel="{Binding Chat}"
    Strings="{Binding ChatStrings}"
    ReactionChoices="{Binding Reactions}"
    ForwardRequested="{Binding ForwardRequested}"
    AttachmentSendRequested="{Binding AttachmentSendRequested}"
    AttachmentDownloadRequested="{Binding AttachmentDownloadRequested}"
    LoadOlderRequested="{Binding LoadOlderRequested}" />
```

The default timeline is a virtualized `CollectionView`. `MauiChatPresentation` keeps wrapper identities stable by `ChatContentId`, updates them on the MAUI dispatcher and reports prepend/change information so the view can preserve the scroll anchor or auto-scroll only when the user is already near the end. XAML uses compiled bindings; custom data templates should declare their own `x:DataType` to keep trimming/AOT behavior predictable.

The control exposes `MessageTemplate`, `AttachmentTemplate`, `ComposerTemplate` and `EmptyTemplate`, localized `MauiChatStrings`, reaction commands and overridable light/dark resources. Default bubbles include reply, forward, reaction, edit and authenticated-download actions. All file, forward and page-loading operations are host callbacks with generic failure state. A callback never causes an attachment URI/path to be opened automatically.

History remains outside UI. A MAUI host normally connects `LoadOlderRequested` to `ChatHistoryPager.LoadPreviousAsync` and gives `ChatSyncCoordinator` the same idempotent projection registry. See [maui.md](maui.md) for lifecycle, SecureStorage and file handling.

## Replacement levels

- `MessageTemplate` replaces every message bubble while retaining the standard timeline and composer.
- `AttachmentTemplate` replaces every attachment card; `AttachmentDownloadRequested` delegates authenticated retrieval/decryption to the host.
- `ComposerTemplate` replaces the composer, including the optional media picker, while retaining the timeline.
- `EmptyTemplate` replaces the empty state.
- `SenderLabel` and `TimeFormatter` control identity and timestamp presentation.
- `ReactionChoices`, edit labels/markers in `SkopkaChatStrings`, CSS variables and `AdditionalAttributes` customize the standard components.
- An application can ignore both framework adapters and bind Avalonia, another native toolkit or custom web controls directly to `ChatViewModel`.

Templates receive decrypted managed strings. Razor text expressions HTML-encode by default; using `MarkupString`, raw DOM APIs or third-party renderers makes escaping the host's responsibility.

## Deliberate limits

The components are a conversation surface, not a product shell. They do not provide routing, authentication, contacts, conversation selection, device verification dialogs, protected history, notifications, push/background execution, automatic attachment previews or cross-device history synchronization. MAUI provides timeline virtualization; Blazor intentionally remains a simpler adaptable surface. The default attachment card never embeds remote media; the host retrieves and authenticates it. The default forward action raises a callback so the host can choose a target conversation; it never invents one, and attachment forwarding is not implemented.

Drafts, edit buffers, pre-edit composer state, reply previews and projected messages are plaintext managed strings and cannot be reliably zeroed. Keep component lifetime and browser/server logs bounded, use protected local persistence where needed, review Blazor Server circuit exposure, and never treat E2EE as local-at-rest protection.
