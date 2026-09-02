export function install(reference) { globalThis.runChatTest = action => reference.invokeMethodAsync('Run', action); }
globalThis.pauseChatCreation = device => { globalThis.chatExpectedDevice = device; globalThis.chatCreationReserved = true; return new Promise(() => {}); };
