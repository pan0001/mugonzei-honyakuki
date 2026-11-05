(() => {
  "use strict";

  const host = window.chrome?.webview;
  const byId = id => document.getElementById(id);
  const all = selector => Array.from(document.querySelectorAll(selector));
  const fields = new Map(all("[data-field]").map(element => [element.dataset.field, element]));
  const pages = new Map([
    ["workspace", "翻译工作台"], ["services", "账号与 AI 服务"], ["capture", "截图与按键"], ["subtitles", "字幕显示"]
  ]);
  const saved = Object.create(null);
  saved.desktopCaptureSource = "PrimaryMonitor";
  saved.desktopWindowId = "";
  const edits = new Map();
  const state = {mode: "Auto", effectiveMode: "Desktop", pages: 0, page: 0, updateState: "idle"};
  const serviceTabs = all("[data-service-tab]");
  let requestedMode;
  let saving = false;
  let reportedDirty = false;
  let presetSignature = "";
  let presets = [];
  let lastPreview;
  let lastKeyOrigin = "";
  let pendingSave;
  let windowListState = "idle";
  let desktopWindows = new Map();
  const quickCapture = byId("quickDesktopCapture");
  const sourceControls = ["desktopSourceField", "desktopWindowPicker"].map(id => {
    const control = byId(id);
    const slot = document.createComment(id);
    control.before(slot);
    return {slot, control};
  });

  function post(message) { if (host) host.postMessage(message); }
  function text(element, value) {
    const next = String(value ?? "");
    if (element.textContent !== next) element.textContent = next;
  }
  function display(element, visible) {
    if (element.hidden === Boolean(visible)) element.hidden = !visible;
  }
  function disabled(element, value) {
    if (element.disabled !== value) element.disabled = value;
  }
  function disableWithReason(element, value, reason) {
    disabled(element, value);
    const title = value ? reason : "";
    if (element.title !== title) element.title = title;
  }
  function optionalText(id, value) {
    const element = byId(id);
    text(element, value);
    display(element, Boolean(value));
  }
  function current(field) { return edits.has(field) ? edits.get(field) : saved[field]; }
  function origin(address) {
    try { return new URL(address).origin; } catch { return ""; }
  }

  function go(page) {
    if (!pages.has(page)) return;
    const enteringCapture = page === "capture" && byId("capture").hidden;
    for (const id of pages.keys()) display(byId(id), id === page);
    for (const button of all("nav [data-page]")) {
      if (button.dataset.page === page) button.setAttribute("aria-current", "page");
      else button.removeAttribute("aria-current");
    }
    display(byId("settingsForm"), page !== "workspace");
    text(byId("pageTitle"), pages.get(page));
    byId("pageContent").scrollTop = 0;
    if (enteringCapture && wantsWindowPicker()) requestDesktopWindows();
  }
  function wantsWindowPicker() {
    return canConfigureDesktop() && current("desktopCaptureSource") === "Window";
  }
  function openDesktopSource() {
    if (edits.size || saving) {
      showStatus("请先保存或放弃当前设置修改，再切换截图来源。", true);
      return;
    }
    for (const {control} of sourceControls) byId("quickDesktopFields").append(control);
    optionalText("quickDesktopError", "");
    quickCapture.showModal();
    fields.get("desktopCaptureSource").focus();
    if (wantsWindowPicker()) requestDesktopWindows();
    dirtyInfo();
  }
  function closeDesktopSource() {
    quickCapture.close();
    for (const {slot, control} of sourceControls) slot.after(control);
  }
  function cancelDesktopSource() {
    if (saving) return;
    closeDesktopSource();
    clearDraft({}, false);
  }
  function canConfigureDesktop() { return state.mode !== "SteamVR" || state.effectiveMode === "Desktop"; }
  function requestDesktopWindows() {
    if (windowListState === "loading") return;
    windowListState = "loading";
    renderWindowPicker();
    post({type: "action", action: "listDesktopWindows"});
  }
  function receiveDesktopWindows(message) {
    const options = fields.get("desktopWindowId");
    options.options.length = 1;
    desktopWindows = new Map();
    windowListState = Array.isArray(message.windows) ? "ready" : "error";
    for (const item of Array.isArray(message.windows) ? message.windows : []) {
      if (!item || typeof item.id !== "string" || !item.id || typeof item.title !== "string") continue;
      desktopWindows.set(item.id, item);
      options.add(new Option(item.processName ? item.title + " · " + item.processName : item.title, item.id));
    }
    updateModes();
  }
  function renderWindowPicker() {
    display(byId("desktopSourceField"), canConfigureDesktop());
    display(byId("desktopWindowPicker"), wantsWindowPicker());
    if (!wantsWindowPicker()) return;
    const id = current("desktopWindowId");
    const select = fields.get("desktopWindowId");
    let missing = select.querySelector("[data-missing]");
    if (missing && (missing.value !== id || desktopWindows.has(id))) { missing.remove(); missing = null; }
    if (id && !desktopWindows.has(id) && !missing) {
      missing = new Option("上次选择的窗口", id);
      missing.dataset.missing = "";
      missing.disabled = true;
      select.add(missing);
    }
    write(select, id);
    disabled(select, windowListState !== "ready" || !desktopWindows.size);
    disabled(byId("refreshDesktopWindows"), windowListState === "loading");
    const hint = windowListState === "loading" ? "正在读取窗口列表…"
      : windowListState === "error" ? "未能读取窗口列表，请刷新重试。"
      : windowListState === "ready" && !desktopWindows.size ? "没有可选窗口。请打开或还原目标窗口，然后刷新列表。"
      : windowListState === "ready" && id && !desktopWindows.has(id) ? "上次选择的窗口已不可用。请还原后刷新，或选择其他窗口。"
      : "请选择要截取的窗口。重新启动翻译器后需再次选择。";
    text(byId("desktopWindowStatus"), hint);
  }
  function showServiceTab(id, focus = false) {
    for (const button of serviceTabs) {
      const selected = button.dataset.serviceTab === id;
      button.setAttribute("aria-selected", String(selected));
      button.tabIndex = selected ? 0 : -1;
      display(byId(button.dataset.serviceTab), selected);
      if (selected && focus) button.focus();
    }
  }

  function read(element) {
    if (element.type === "checkbox") return element.checked;
    if (element.type === "number") {
      if (!element.validity.valid || !element.value.trim()) return null;
      return element.valueAsNumber / (element.hasAttribute("data-ratio") ? 100 : 1);
    }
    return element.value;
  }
  function write(element, value) {
    if (element.type === "checkbox") {
      if (element.checked !== Boolean(value)) element.checked = Boolean(value);
    } else {
      const next = value == null ? "" : element.hasAttribute("data-ratio") ? String(Number((value * 100).toFixed(4))) : String(value);
      if (element.value !== next) element.value = next;
    }
  }
  function changeField(field, value) {
    if (value === saved[field]) edits.delete(field);
    else edits.set(field, value);
  }
  function dirtyInfo() {
    const dirty = edits.size > 0;
    display(byId("saveBar"), dirty);
    for (const id of ["save","discard","editArea","modes"]) disabled(byId(id), saving);
    disabled(byId("quickDesktopFields"), saving);
    disabled(byId("cancelDesktopSource"), saving);
    disabled(byId("applyDesktopSource"), saving || !dirty);
    if (reportedDirty !== dirty) {
      reportedDirty = dirty;
      post({type: "dirty", dirty});
    }
  }
  function renderKey() {
    const savedKeyAtAddress = Boolean(saved.hasApiKey && origin(current("apiBaseUrl")) === origin(saved.apiBaseUrl));
    const replacing = edits.has("apiKey");
    const element = fields.get("apiKey");
    const hint = savedKeyAtAddress && !replacing ? "已保存 · 输入以更换" : "本机服务可留空";
    if (element.placeholder !== hint) element.placeholder = hint;
    text(byId("keyHint"), replacing && !edits.get("apiKey") ? "保存后将清除旧密钥。" :
      (!savedKeyAtAddress && saved.hasApiKey ? "地址已更改，请使用对应服务的密钥。" : ""));
    display(byId("clearKey"), savedKeyAtAddress || Boolean(current("apiKey")));
  }
  function applySettings(data) {
    if (!data || typeof data !== "object") return;
    for (const [field, element] of fields) {
      if (field === "apiKey" || !Object.hasOwn(data, field)) continue;
      saved[field] = data[field];
      if (!edits.has(field)) write(element, data[field]);
    }
    if (Object.hasOwn(data, "hasApiKey")) saved.hasApiKey = data.hasApiKey === true;
    if (typeof data.officialCreditServerUrl === "string") saved.officialCreditServerUrl = data.officialCreditServerUrl;
    lastKeyOrigin = origin(current("apiBaseUrl"));
    renderForm();
  }
  function renderForm() {
    const provider = current("translationBackend");
    const recognition = current("processingMode");
    if (saved.officialCreditServerUrl) {
      const custom = String(current("creditServerUrl")).trim().replace(/\/+$/, "").toLowerCase() !== saved.officialCreditServerUrl;
      text(byId("creditDestination"), custom ? "自定义积分服务" : "官方积分服务");
      display(byId("restoreOfficialCredit"), custom);
    }
    for (const group of all("[data-provider]")) display(group, group.dataset.provider === provider);
    for (const option of fields.get("processingMode").options) {
      disabled(option, provider === "GoogleTranslate" && ["Image", "Handwriting"].includes(option.value));
    }
    const destination = {OpenAI:"你配置的 API", Credits:"积分服务器及其 AI 服务", Codex:"本机 Codex 配置的模型", GoogleTranslate:"Google 翻译"}[provider] || "所选翻译服务";
    const sendsImage = recognition === "Image" || recognition === "Handwriting";
    text(byId("privacyHint"), sendsImage ? "截图会发送给" + destination + "，一次请求直接返回译文。" : "图片在本机识别，原文会发送给" + destination + "。");
    disableWithReason(fields.get("showSource"), sendsImage, "本地 OCR 翻译可同时显示原文；截图直译只返回译文");
    optionalText("localOcrStatus", recognition === "LocalJapanese" ? state.localOcrStatus : "");
    display(byId("selectionDistanceField"), Boolean(current("spatialSelectionEnabled")));
    text(byId("controllerHelp"), current("spatialSelectionEnabled")
      ? "按住左 X，用双手柄拉出矩形。松开后截图，译文留在原位。"
      : "长按左手柄 X 1.5 秒翻译，持续按到 3 秒取消。Index 使用左 A，Vive 使用右菜单键。");
    renderWindowPicker();
    renderKey();
  }
  function edit(element) {
    if (saving) return;
    if (quickCapture.open) optionalText("quickDesktopError", "");
    const field = element.dataset.field;
    let value = read(element);
    if (field === "apiKey") {
      // Untouched or re-emptied password input preserves the existing protected key.
      if (!value) edits.delete(field);
      else edits.set(field, value);
    } else {
      changeField(field, value);
    }
    if (field === "apiBaseUrl") {
      const nextOrigin = origin(value);
      if (nextOrigin !== lastKeyOrigin && fields.get("apiKey").value) {
        fields.get("apiKey").value = "";
        edits.delete("apiKey");
      }
      lastKeyOrigin = nextOrigin;
    }
    if (field === "translationBackend" && value === "GoogleTranslate" && ["Handwriting", "Image"].includes(current("processingMode"))) {
      changeField("processingMode", "Ocr");
      write(fields.get("processingMode"), "Ocr");
    }
    if (field === "desktopCaptureSource") {
      if (value === "Window") requestDesktopWindows();
      else edits.delete("desktopWindowId");
    }
    renderForm();
    dirtyInfo();
  }
  function showStatus(value, error = false) {
    text(byId("status"), value);
    if (document.body.dataset.error !== String(error)) document.body.dataset.error = String(error);
  }

  function validate() {
    if (current("desktopCaptureSource") === "Window" && (edits.has("desktopCaptureSource") || edits.has("desktopWindowId"))
        && (windowListState !== "ready" || !desktopWindows.has(current("desktopWindowId")))) {
      const message = "请刷新列表并选择可用的截图窗口。";
      if (quickCapture.open) optionalText("quickDesktopError", message);
      else { go("capture"); showStatus(message, true); }
      fields.get("desktopWindowId").focus();
      return false;
    }
    for (const field of edits.keys()) {
      const element = fields.get(field);
      if (element.checkValidity()) continue;
      go(element.closest(".page").id);
      const panel = element.closest('[role="tabpanel"]');
      if (panel) showServiceTab(panel.id);
      for (let parent = element.parentElement; parent; parent = parent.parentElement) {
        if (parent.tagName === "DETAILS") parent.open = true;
      }
      if (element.offsetParent) element.reportValidity();
      else showStatus("请检查尚未保存的设置。", true);
      return false;
    }
    if (Number(current("cropX")) + Number(current("cropWidth")) > 1.00001 || Number(current("cropY")) + Number(current("cropHeight")) > 1.00001) {
      go("capture");
      showStatus("识别区域超出画面，请调整左侧、顶部或宽高。", true);
      return false;
    }
    return true;
  }
  function sendSave() {
    if (!edits.size || saving || !validate()) return;
    saving = true;
    pendingSave = Object.fromEntries(edits);
    dirtyInfo();
    post({type: "save", settings: pendingSave});
  }
  function clearDraft(latest, acknowledged) {
    if (acknowledged && pendingSave) {
      for (const [field, value] of Object.entries(pendingSave)) {
        if (field !== "apiKey") saved[field] = value;
        else saved.hasApiKey = Boolean(value);
      }
    }
    pendingSave = undefined;
    saving = false;
    edits.clear();
    fields.get("apiKey").value = "";
    const snapshot = {...saved, ...latest};
    applySettings(snapshot);
    dirtyInfo();
  }
  function updateModes() {
    const preference = requestedMode || state.mode;
    for (const element of all('input[name="mode"]')) {
      if (element.checked !== (element.value === preference)) element.checked = element.value === preference;
    }
    const effective = state.effectiveMode === "SteamVR" ? "SteamVR" : "桌面";
    text(byId("activeMode"), "当前 · " + effective);
    const hand = effective === "SteamVR" && saved.controllerEnabled;
    const selecting = hand && saved.spatialSelectionEnabled;
    text(byId("triggerVerb"), selecting ? "按住" : hand ? "长按" : "按");
    text(byId("triggerKey"), hand ? "左 X" : saved.hotkey || "Ctrl+Alt+T");
    const windowCapture = saved.desktopCaptureSource === "Window";
    text(byId("triggerHint"), selecting ? "拉出选区，松开截图" : hand ? "1.5 秒截取画面" : effective === "桌面" ? (windowCapture ? "截取指定窗口" : "截取主显示器全屏") : "截图");
    display(byId("desktopCaptureEntry"), effective === "桌面");
    const selectedWindow = desktopWindows.get(saved.desktopWindowId);
    const sourceName = windowCapture ? selectedWindow?.title || (saved.desktopWindowId ? "指定窗口" : "") : "主显示器全屏";
    const captureName = sourceName ? "截图来源 · " + sourceName : "选择截图窗口";
    text(byId("desktopCaptureName"), captureName);
    if (byId("desktopCaptureEntry").title !== captureName) byId("desktopCaptureEntry").title = captureName;
    if (byId("activeMode").title !== String(state.vrStatus || "")) byId("activeMode").title = String(state.vrStatus || "");
    text(byId("modeHelp"), state.mode === "Auto"
      ? "自动跟随 SteamVR。关闭时截取桌面，检测到就绪后切换到头显。"
      : state.mode === "Desktop" ? "手动使用桌面模式。选择「自动」即可恢复跟随 SteamVR。" : "优先使用 SteamVR。请先从 Steam 启动 SteamVR。");
    const canConfigureVr = state.mode !== "Desktop";
    for (const id of ["vrSourceField","vrFields","vrSubtitleFields"]) display(byId(id), canConfigureVr);
    renderWindowPicker();
    if (windowListState === "idle" && !byId("capture").hidden && wantsWindowPicker()) requestDesktopWindows();
    if (byId("modeHelp").title !== String(state.inputHelp || "")) byId("modeHelp").title = String(state.inputHelp || "");
  }
  function updatePresets(list) {
    if (!Array.isArray(list)) return;
    const signature = JSON.stringify(list);
    if (signature === presetSignature) return;
    presetSignature = signature;
    presets = list.filter(p => p && typeof p.name === "string" && typeof p.baseUrl === "string" && p.baseUrl);
    const select = byId("apiPreset");
    select.options.length = 1;
    presets.forEach((item, index) => select.add(new Option(item.name, String(index))));
    display(byId("presetField"), presets.length > 0);
  }
  function renderUpdate() {
    const available = state.updateState === "available";
    const checking = state.updateState === "checking";
    const status = {
      idle: "检查是否有新版本可用。",
      checking: "正在检查更新…",
      current: "已是最新版本。",
      available: state.updateVersion ? "发现新版本 " + state.updateVersion : "发现新版本。",
      error: "更新检查失败。"
    }[state.updateState] || "检查是否有新版本可用。";
    const entry = byId("updateEntry");
    entry.dataset.available = String(available);
    entry.setAttribute("aria-label", available ? "检查更新，有新版本可用" : "检查更新");
    text(entry.querySelector(".update-entry-full"), available ? "有新版可用" : "检查更新");
    text(entry.querySelector(".update-entry-short"), available ? "新版本" : "更新");
    optionalText("updateEntryVersion", state.appVersion);
    optionalText("updateCurrentVersion", state.appVersion ? "当前版本 " + state.appVersion : "");
    byId("updatePanel").dataset.state = state.updateState;
    text(byId("updateStatus"), status);
    optionalText("updateDetail", state.updateMessage !== status ? state.updateMessage : "");
    text(byId("updateCheck"), checking ? "检查中…" : state.updateState === "error" ? "重试" : state.updateState === "idle" ? "检查更新" : "重新检查");
    disabled(byId("updateCheck"), checking);
    const download = byId("updateDownload");
    text(download, state.updateHasInstaller === false ? "查看发布页" : "下载新版");
    display(download, available);
    disabled(download, !available);
  }
  function requestUpdateCheck() {
    if (state.updateState === "checking") return;
    state.updateState = "checking";
    state.updateMessage = "";
    renderUpdate();
    post({type: "action", action: "checkUpdate"});
  }
  function updateMessage(event) {
    const message = event.data;
    if (!message || typeof message !== "object") return;
    if (message.type === "desktopWindows") { receiveDesktopWindows(message); return; }
    if (message.type === "error") {
      saving = false;
      requestedMode = undefined;
      pendingSave = undefined;
      dirtyInfo();
      if (message.message) showStatus(message.message, true);
      if (quickCapture.open) optionalText("quickDesktopError", message.message || "截图来源未能保存。");
      updateModes();
      return;
    }
    if (message.type === "saved") {
      if (quickCapture.open) closeDesktopSource();
      clearDraft(message.settings, true);
      showStatus("设置已保存。");
      updateModes();
      return;
    }
    if (message.type !== "state") return;
    // Screenshots can be large. Retain only the current img.src instead of copying the URI into every state snapshot.
    const {preview, settings, presets: incomingPresets, ...small} = message;
    Object.assign(state, small);
    if (["appVersion", "updateState", "updateMessage", "updateVersion", "updateHasInstaller"].some(key => Object.hasOwn(small, key))) renderUpdate();
    if (small.mode === requestedMode) requestedMode = undefined;
    if (Object.hasOwn(small, "hasApiKey")) saved.hasApiKey = small.hasApiKey === true;
    if (settings) applySettings(settings);
    if (incomingPresets) updatePresets(incomingPresets);
    if (preview !== undefined && lastPreview !== preview) {
      lastPreview = preview;
      const valid = typeof preview === "string" && /^data:image\/(?:png|jpeg|webp);base64,/.test(preview);
      const image = byId("previewImage");
      if (valid) image.src = preview;
      else image.removeAttribute("src");
      display(image, valid);
      display(byId("previewEmpty"), !valid);
      disableWithReason(byId("saveScreenshot"), !valid, "截取一张画面后即可保留");
    }
    if (Object.hasOwn(small, "status")) showStatus(small.status || "就绪");
    if (Object.hasOwn(small, "source") || Object.hasOwn(small, "sourceHint")) {
      text(byId("source"), state.source || state.sourceHint || "这里显示从截图中读到的文字。");
      byId("source").classList.toggle("empty-text", !state.source);
    }
    if (Object.hasOwn(small, "translation")) {
      text(byId("translation"), small.translation || "译文会显示在这里");
      byId("translation").classList.toggle("empty-text", !small.translation);
    }
    for (const key of ["apiTestResult", "googleTestResult", "creditNote"]) {
      if (Object.hasOwn(small, key)) optionalText(key, small[key]);
    }
    if (Object.hasOwn(small, "captureLabel")) optionalText("captureLabel", small.captureLabel === "截图预览" ? "" : small.captureLabel);
    if (Object.hasOwn(small, "accountLoggedIn") || Object.hasOwn(small, "accountUsername")) {
      const signedIn = Boolean(state.accountLoggedIn);
      text(byId("accountName"), signedIn ? state.accountUsername || "当前账号" : "尚未登录");
      text(byId("accountState"), signedIn ? "已登录" : "登录后可使用账号积分翻译。");
      text(byId("loginButton"), signedIn ? "切换账号" : "登录账号");
    }
    if (Object.hasOwn(small, "localOcrStatus")) optionalText("localOcrStatus", current("processingMode") === "LocalJapanese" ? small.localOcrStatus : "");
    if (Object.hasOwn(small, "recognizeLabel")) text(byId("recognizeLabel"), small.recognizeLabel || "仅识别");
    if (Object.hasOwn(small, "testingApi")) text(byId("testApi"), small.testingApi ? "取消测试" : "测试图片识别");
    if (Object.hasOwn(small, "testingGoogle")) text(byId("testGoogle"), small.testingGoogle ? "取消测试" : "测试翻译");
    if (Object.hasOwn(small, "busy")) {
      if (document.body.dataset.busy !== String(Boolean(state.busy))) document.body.dataset.busy = String(Boolean(state.busy));
      display(byId("resultState"), Boolean(state.busy));
    }
    disableWithReason(byId("cancelTask"), !Boolean(state.canCancel ?? state.busy), "当前没有可取消的任务");
    disableWithReason(byId("recognizeButton"), state.canRecognize === false, "积分服务需要同时识别并翻译");
    const count = Number(state.pages) || 0;
    const index = count ? Math.max(1, Math.min(count, Number(state.page) || 1)) : 0;
    text(byId("pageNumber"), index + " / " + count);
    display(byId("pageTools"), count > 1);
    for (const id of ["next","previous"]) disableWithReason(byId(id), count < 2 || Boolean(state.busy),
      state.busy ? "本次翻译完成后可翻页" : "当前只有一页");
    updateModes();
  }

  document.addEventListener("click", event => {
    const button = event.target.closest("button");
    if (!button || button.disabled) return;
    if (button.id === "refreshDesktopWindows") { requestDesktopWindows(); return; }
    if (button.id === "desktopCaptureEntry") { openDesktopSource(); return; }
    if (button.id === "updateEntry") {
      if (state.updateState === "idle" && !byId("updatePanel").matches(":popover-open")) requestUpdateCheck();
      return;
    }
    if (button.dataset.page) { go(button.dataset.page); return; }
    if (button.dataset.serviceTab) { showServiceTab(button.dataset.serviceTab); return; }
    const action = button.dataset.action;
    if (!action) return;
    if (action === "checkUpdate") { requestUpdateCheck(); return; }
    const hasDraft = edits.size > 0;
    if (hasDraft && ["translate", "recognize"].includes(action)) {
      showStatus("请先保存或放弃设置修改，再截取画面。", true);
      return;
    }
    const message = {type: "action", action};
    if (hasDraft && ["checkCodex", "testApi", "testGoogle", "account", "login", "setup"].includes(action)) message.settings = Object.fromEntries(edits);
    post(message);
  });
  for (const field of fields.values()) {
    field.addEventListener(field.matches('select, [type="checkbox"]') ? "change" : "input", () => edit(field));
  }
  byId("modes").addEventListener("change", event => {
    if (event.target.name === "mode") {
      requestedMode = event.target.value;
      post({type: "mode", mode: requestedMode});
    }
  });
  for (const [index, button] of serviceTabs.entries()) {
    button.addEventListener("keydown", event => {
      const keys = {ArrowLeft:(index + serviceTabs.length - 1) % serviceTabs.length,
        ArrowRight:(index + 1) % serviceTabs.length, Home:0, End:serviceTabs.length - 1};
      if (!Object.hasOwn(keys, event.key)) return;
      event.preventDefault();
      showServiceTab(serviceTabs[keys[event.key]].dataset.serviceTab, true);
    });
  }
  byId("settingsForm").addEventListener("submit", event => { event.preventDefault(); sendSave(); });
  quickCapture.addEventListener("cancel", event => { event.preventDefault(); cancelDesktopSource(); });
  byId("cancelDesktopSource").addEventListener("click", cancelDesktopSource);
  byId("applyDesktopSource").addEventListener("click", sendSave);
  byId("save").addEventListener("click", sendSave);
  byId("discard").addEventListener("click", () => clearDraft({}, false));
  byId("clearKey").addEventListener("click", () => {
    if (saved.hasApiKey) edits.set("apiKey", "");
    else edits.delete("apiKey");
    fields.get("apiKey").value = "";
    renderKey();
    dirtyInfo();
  });
  byId("restoreOfficialCredit").addEventListener("click", () => {
    const address = fields.get("creditServerUrl");
    write(address, saved.officialCreditServerUrl);
    edit(address);
  });
  byId("apiPreset").addEventListener("change", event => {
    const item = presets[event.target.value];
    if (!item) return;
    const address = fields.get("apiBaseUrl");
    write(address, item.baseUrl);
    edit(address);
    optionalText("presetNote", item.note);
  });
  byId("translation").classList.add("empty-text");
  byId("source").classList.add("empty-text");
  go("workspace");
  if (host) {
    host.addEventListener("message", updateMessage);
    post({type: "ready"});
  }
})();
