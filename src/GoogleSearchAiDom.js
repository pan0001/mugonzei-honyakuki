function googleSearchAiDom(action, id, value) {
  const result = (state, text = "") => ({state, text});
  const url = new URL(location.href);
  const exact = (key, expected) => url.searchParams.getAll(key).length === 1 && url.searchParams.get(key) === expected;
  if (window !== window.top || url.origin !== "https://www.google.com" || url.username || url.password)
    return result("unsupported");
  if (url.pathname.startsWith("/sorry/")) return result("blocked");
  if (!(url.pathname === "/aimode" || (url.pathname === "/search" && exact("udm", "50"))
      || (url.pathname === "/webhp" && exact("aep", "11")))) return result("unsupported");
  if (action !== "inspect" && !/^[a-f0-9]{32}$/.test(id)) return result("uncertain");

  const all = (root, selector) => Array.from(root.querySelectorAll(selector));
  const visible = e => e && e.getBoundingClientRect().width > 0 && e.getBoundingClientRect().height > 0
    && getComputedStyle(e).visibility !== "hidden" && !e.closest('[hidden],[aria-hidden="true"]');
  const one = (root, selector) => {
    const matches = all(root, selector).filter(visible);
    return matches.length === 1 ? matches[0] : null;
  };
  const refusalFrame = all(document, "iframe[src]").some(frame => frame.src.startsWith("https://www.google.com/sorry/"));
  if (refusalFrame || all(document, 'iframe[title="Captcha" i],iframe[src*="/recaptcha/"],#captcha-form').some(visible))
    return result("blocked");
  for (const notice of all(document, '[role="dialog"],[role="alert"]').filter(visible)) {
    const text = notice.innerText || "";
    if (/verify (that )?you( are|'re) human|unusual traffic|automated queries/i.test(text)) return result("blocked");
    if (/sign in to (?:use|continue|upload)|you (?:must|need to) sign in/i.test(text)) return result("login");
    if (/upload failed|couldn.t upload|unable to upload|couldn.t generate|something went wrong/i.test(text)) return result("uncertain");
  }
  const main = one(document, 'main,[role="main"]');
  // Google temporarily removes the old main while it builds the conversation document.
  if (!main) return result("waiting");
  const input = one(main, 'textarea[aria-label="Ask anything"],textarea[placeholder="Ask anything"]');
  const mimeInput = () => {
    const files = all(main, 'input[type="file"]').filter(e => !e.disabled
      && e.accept.toLowerCase().split(",").some(type => ["image/png","image/*",".png"].includes(type.trim())));
    return files.length === 1 ? files[0] : null;
  };
  const chips = () => all(main,'[role="button"][aria-label]').filter(n => visible(n)
    && (n.querySelector('[role="progressbar"][aria-label="Uploading file"]') || /\.(?:png|jpe?g|gif|webp|heic|heif|avif|pdf|bmp|tiff?|docx?|txt|xlsx?|csv)$/i.test(n.getAttribute("aria-label"))));
  const state = window.__mugonzeiSearchAiTurn ??= {};
  if (action === "inspect") {
    if (!input) return result("waiting");
    if (input.value.trim() || chips().length) return result("uncertain");
    if (mimeInput()) return result("ready");
    const add = one(main, 'button[aria-label="Add files and tools"]');
    if (add && !state.menuOpened) { state.menuOpened = true; add.click(); }
    return result(mimeInput() ? "ready" : "waiting");
  }

  const filename = "mugonzei-" + id + ".png";
  const chipSelector = '[role="button"][aria-label="' + filename + '"]';
  const uploaded = () => {
    const chip = one(main, chipSelector);
    if (!chip || chips().length !== 1 || all(chip, '[role="progressbar"]').some(visible)) return false;
    const image = chip.querySelector("img");
    return !!image && image.complete && image.naturalWidth > 0
      && !all(chip, '[aria-label],[title]').some(n => /failed|error|retry/i.test(n.getAttribute("aria-label") || n.title || ""));
  };
  if (action === "attach" || action === "attachBuffered") {
    if (!input || input.value.trim() || state.id || chips().length) return result("uncertain");
    const files = mimeInput();
    if (!files) return result("unsupported");
    let encoded = value;
    if (action === "attachBuffered") {
      const buffer = window.__mugonzeiSearchAiUpload;
      delete window.__mugonzeiSearchAiUpload;
      if (!buffer || buffer.id !== id || !Array.isArray(buffer.parts)) return result("uncertain");
      encoded = buffer.parts.join("");
    }
    if (typeof encoded !== "string" || !encoded.length || encoded.length > 16 * 1024 * 1024) return result("uncertain");
    let data;
    try {
      data = typeof Uint8Array.fromBase64 === "function" ? Uint8Array.fromBase64(encoded)
        : Uint8Array.from(atob(encoded), c => c.charCodeAt(0));
    } catch { return result("uncertain"); }
    const signature = [137,80,78,71,13,10,26,10];
    if (!signature.every((v,i) => data[i] === v)) return result("uncertain");
    const transfer = new DataTransfer();
    transfer.items.add(new File([data], filename, {type:"image/png"}));
    state.id = id;
    state.phase = "attaching";
    files.files = transfer.files;
    files.dispatchEvent(new Event("change", {bubbles:true}));
    return result("attached");
  }

  const begin = "MUBEGIN" + id.slice(0,12);
  const end = "MUEND" + id.slice(-12);
  if (action === "submit") {
    if (state.id !== id || state.phase !== "attaching" || !uploaded() || !input
        || input.value.trim() || typeof value !== "string" || !value.trim()) return result("uncertain");
    const prompt = value + "\nFor this interface wrap the complete answer in plain text, beginning with " + begin
      + " alone on its own line and ending with " + end + " alone on its own line. No Markdown fences.";
    if (input.maxLength >= 0 && prompt.length > input.maxLength) return result("uncertain");
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,"value").set.call(input, prompt);
    input.dispatchEvent(new Event("input", {bubbles:true}));
    const send = one(main, 'button[aria-label="Send"]');
    if (!send || send.disabled || send.getAttribute("aria-disabled") === "true" || input.value !== prompt || !uploaded())
      return result("uncertain");
    state.phase = "submitted";
    send.click();
    return result("submitted");
  }
  if (action !== "read" || (state.id && state.id !== id)) return result("uncertain");
  if (state.phase === "attaching") return result(uploaded() ? "uploadReady" : "waiting");

  // A submission may replace the entire document. The user's question echoes both markers
  // inline. Only a separate, visible answer bounded by both markers can finish it.
  const echo = all(main, 'h1,h2,[role="heading"],[data-message-author-role="user"]')
    .find(e => e.textContent.includes(begin + " alone on its own line") && e.textContent.includes(end + " alone on its own line"));
  if (!echo && state.phase !== "submitted") return result("waiting");
  const generating = all(main, '[aria-busy="true"],button[aria-label^="Stop generating"],button[aria-label="Stop response"]').some(visible);
  if (generating) { state.lastText = null; return result("waiting"); }
  const excluded = 'nav,aside,a,textarea,form,script,style,button,[role="button"],[role="navigation"],'
    + '[role="dialog"],h1,h2,h3,[role="heading"],[data-message-author-role="user"],[contenteditable="true"]';
  const walker = document.createTreeWalker(main, NodeFilter.SHOW_TEXT);
  const answers = new Set();
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    if (!node.nodeValue.includes(begin)) continue;
    let item = node.parentElement;
    if (!item || item.closest(excluded) || !visible(item)) continue;
    if (echo && !(echo.compareDocumentPosition(item) & Node.DOCUMENT_POSITION_FOLLOWING)) continue;
    while (item && item !== main) {
      const text = (item.innerText || "").trim();
      // Google may collapse soft line breaks into spaces during Markdown rendering.
      if (text.startsWith(begin) && text.endsWith(end)
          && /\s/.test(text.charAt(begin.length)) && /\s/.test(text.charAt(text.length-end.length-1))) {
        const inner = text.slice(begin.length, -end.length).trim();
        if (!inner || inner.includes(begin) || inner.includes(end) || inner.length > 32000) return result("uncertain");
        answers.add(inner);
        break;
      }
      item = item.parentElement;
    }
  }
  if (!answers.size) { state.lastText = null; return result("waiting"); }
  if (answers.size !== 1) return result("uncertain");
  const text = answers.values().next().value;
  if (state.lastText !== text) {
    state.lastText = text;
    state.observed = Date.now();
    return result("waiting");
  }
  return Date.now() - state.observed >= 700 ? result("complete", text) : result("waiting");
}
