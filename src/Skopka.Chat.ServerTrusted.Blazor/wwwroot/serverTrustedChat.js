// Skopka.Chat.ServerTrusted.Blazor: document visibility only. No message content crosses this module.

export function isVisible() {
    return document.visibilityState === 'visible';
}

export function observeVisibility(dotNetRef) {
    let disposed = false;
    const handler = () => {
        if (disposed) {
            return;
        }

        dotNetRef.invokeMethodAsync('OnVisibilityChanged', document.visibilityState === 'visible').catch(() => { });
    };

    document.addEventListener('visibilitychange', handler);
    return {
        dispose() {
            disposed = true;
            document.removeEventListener('visibilitychange', handler);
        },
    };
}
