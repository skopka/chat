// "Ask in chat" buttons outside the island forward the task to the chat wrapper (MentorChat.razor).
window.sampleChatAsk = {
  target: null,
  register(target) { this.target = target; },
  unregister(target) { if (this.target === target) { this.target = null; } },
};

document.addEventListener("click", (event) => {
  const button = event.target instanceof Element ? event.target.closest("[data-ask-type]") : null;
  if (button && window.sampleChatAsk.target) {
    window.sampleChatAsk.target.invokeMethodAsync("AskAboutAsync", button.dataset.askType, button.dataset.askId);
  }
});
