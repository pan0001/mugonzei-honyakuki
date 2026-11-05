// Real Chromium document + a deterministic stand-in for the native WebView2 message port.
// Run with Node 22+ and Edge/Chrome installed. CHROMIUM_PATH selects a different binary.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");
const http = require("node:http");
const { spawn } = require("node:child_process");
const { once } = require("node:events");
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const root = path.resolve(__dirname, "..");
const assetDir = path.join(root, "assets", "webui");
const types = {".html": "text/html", ".css": "text/css", ".js": "application/javascript"};
const allowedFiles = new Set(["workspace.html", "workspace.js", "workspace.css"]);
const executable = [process.env.CHROMIUM_PATH, ...["ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA"]
  .filter(key => process.env[key]).map(key => path.join(process.env[key], "Microsoft", "Edge", "Application", "msedge.exe"))]
  .find(file => file && fs.existsSync(file));
if (!executable) throw new Error("Set CHROMIUM_PATH to an installed Edge or Chrome executable.");

const fixture = {
  type: "state", mode: "Auto", effectiveMode: "Desktop", status: "就绪", source: "", translation: "", accountLoggedIn:false, accountUsername:"",
  appVersion: "0.2.5-preview", updateState: "idle", updateMessage: "", updateVersion: "",
  settings: {
    translationBackend: "OpenAI", processingMode: "Handwriting", targetLanguage: "简体中文",
    creditServerUrl: "https://test.invalid", officialCreditServerUrl: "https://api.mugonzei.top", apiBaseUrl: "https://model.invalid/v1",
    apiModel: "vision", apiStream: false, apiImageDetail: "Default", hasApiKey: true,
    codexExecutablePath: "", codexModel: "", codexReasoningEffort: "", ocrLanguages: "jpn+eng", hotkey: "Ctrl+Alt+T",
    cropX: 0, cropY: 0, cropWidth: 1, cropHeight: 1, captureMode: "VR 左眼",
    controllerEnabled: true, spatialSelectionEnabled: false, selectionDistance: 1.2,
    overlayWidth: .9, overlayDistance: 1.2, overlayVerticalOffset: -.35, displaySeconds: 20, showSource: false
  },
  presets: [{name: "本机 Ollama", baseUrl: "http://localhost:11434/v1", note: "本机"}]
};

// A Japanese station sign drawn from scratch. No screen pixels, third-party artwork, or account data.
function exampleScreenshot() {
  const canvas = document.createElement("canvas");
  canvas.width = 960;
  canvas.height = 600;
  const g = canvas.getContext("2d");
  const wall = g.createLinearGradient(0, 0, 960, 600);
  wall.addColorStop(0, "#b0bdbb");
  wall.addColorStop(1, "#778f8c");
  g.fillStyle = wall;
  g.fillRect(0, 0, 960, 600);
  g.strokeStyle = "rgba(49,76,76,.26)";
  g.lineWidth = 2;
  for (let x = 0; x < 960; x += 160) {
    g.beginPath(); g.moveTo(x, 0); g.lineTo(x, 600); g.stroke();
  }
  for (let y = 48; y < 600; y += 90) {
    g.beginPath(); g.moveTo(0, y); g.lineTo(960, y); g.stroke();
  }
  // Suspended sign and a simple front-facing train pictogram.
  g.fillStyle = "#405350";
  g.fillRect(172, 0, 10, 171);
  g.fillRect(778, 0, 10, 171);
  const sign = (x, y, width, height, radius, color) => {
    g.fillStyle = color; g.beginPath(); g.roundRect(x, y, width, height, radius); g.fill();
  };
  g.shadowColor = "rgba(18,38,34,.32)"; g.shadowBlur = 19; g.shadowOffsetY = 13;
  sign(46, 150, 868, 250, 14, "#31403d");
  g.shadowColor = "transparent";
  sign(52, 156, 856, 238, 10, "#eef0df");
  g.fillStyle = "#287665"; g.fillRect(52, 342, 856, 27);
  sign(86, 196, 67, 85, 12, "#283d37");
  sign(94, 208, 51, 37, 4, "#eef0df");
  for (const x of [103,136]) { g.fillStyle = "#eef0df"; g.beginPath(); g.arc(x, 261, 5, 0, Math.PI*2); g.fill(); }
  g.strokeStyle = "#283d37"; g.lineWidth = 6;
  for (const x of [101,138]) { g.beginPath(); g.moveTo(x, 278); g.lineTo(x + (x<110 ? -12 : 12), 297); g.stroke(); }
  g.fillStyle="#203431"; g.textAlign="left"; g.textBaseline="middle";
  g.font='600 38px "Yu Gothic UI","Meiryo","Microsoft YaHei UI",sans-serif';
  g.fillText("次の電車は 10 時に到着します。", 186, 247, 693);
  return canvas.toDataURL("image/png");
}

async function main() {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), "mugonzei-webui-qa-"));
  const requests = [];
  const server = http.createServer((request, response) => {
    const name = new URL(request.url, "http://localhost").pathname.slice(1);
    requests.push(name);
    if (!allowedFiles.has(name)) { response.writeHead(404).end(); return; }
    response.setHeader("Content-Type", types[path.extname(name)] + "; charset=UTF-8");
    fs.createReadStream(path.join(assetDir, name)).pipe(response);
  });
  let browser;
  let socket;
  try {
    server.listen(0, "127.0.0.1");
    await once(server, "listening");
    browser = spawn(executable, ["--headless=new", "--disable-gpu", "--no-first-run",
      "--no-default-browser-check", "--disable-background-networking", "--disable-component-update",
      "--remote-debugging-port=0", "--user-data-dir=" + profile, "about:blank"], {windowsHide: true, stdio: "ignore"});
    let port;
    for (let attempt = 0; attempt < 240; attempt++) {
      if (browser.exitCode !== null) throw new Error("The headless browser exited during startup.");
      try { port = Number(fs.readFileSync(path.join(profile, "DevToolsActivePort"), "utf8").split(/\r?\n/)[0]); } catch { }
      if (port) break;
      await delay(100);
    }
    assert.ok(port, "Browser exposes a debugging port in the isolated profile");
    const tabs = await (await fetch("http://127.0.0.1:" + port + "/json/list")).json();
    socket = new WebSocket(tabs.find(tab => tab.type === "page").webSocketDebuggerUrl);
    await once(socket, "open");
    const pending = new Map();
    let id = 0;
    socket.onmessage = event => {
      const response = JSON.parse(event.data);
      if (!pending.has(response.id)) return;
      const {resolve, reject, timer} = pending.get(response.id);
      clearTimeout(timer);
      pending.delete(response.id);
      if (response.error) reject(new Error(response.error.message));
      else resolve(response.result);
    };
    function call(method, params = {}) {
      const sequence = ++id;
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => { pending.delete(sequence); reject(new Error(method + " timed out")); }, 15000);
        pending.set(sequence, {resolve, reject, timer});
        socket.send(JSON.stringify({method, params, id: sequence}));
      });
    }
    async function run(expression) {
      const result = await call("Runtime.evaluate", {expression, returnByValue: true, awaitPromise:true});
      if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
      return result.result.value;
    }
    async function viewport(width, height) {
      await call("Emulation.setDeviceMetricsOverride", {width, height, deviceScaleFactor: 1, mobile: false});
    }
    const init = [
      "window.sent=[]; window.appErrors=[]; const port=new EventTarget();",
      "port.postMessage=m=>sent.push(structuredClone(m));",
      "window.chrome??={}; Object.defineProperty(window.chrome,'webview',{value:port});",
      "window.send=m=>port.dispatchEvent(new MessageEvent('message',{data:m}));",
      "window.q=s=>document.querySelector(s);window.el=id=>document.getElementById(id);",
      "window.field=name=>q('[data-field='+name+']');",
      "window.edit=(name,value)=>{const f=field(name);f.value=value;f.dispatchEvent(new Event(f.tagName==='SELECT'?'change':'input',{bubbles:true}));};",
      "window.showPage=name=>q('[data-page='+name+']').click();",
      "window.addEventListener('error', e=>appErrors.push(e.message));"
    ].join("");
    await call("Page.enable");
    await call("Page.addScriptToEvaluateOnNewDocument", {source: init});
    await viewport(1150, 750);
    await call("Page.navigate", {url: "http://127.0.0.1:" + server.address().port + "/workspace.html"});
    let ready = false;
    for (let i = 0; i < 100 && !ready; i++) {
      try { ready = await run("window.sent?.some(m=>m.type==='ready')"); } catch { }
      if (!ready) await delay(60);
    }
    if (!ready) throw new Error("Workspace did not connect: " + JSON.stringify(await run("[location.href,document.title,typeof window.sent,typeof window.chrome?.webview,window.appErrors,document.querySelector('script')?.src]")) + " files=" + requests.join(","));
    await run("send(" + JSON.stringify(fixture) + ")");
    assert.equal(await run("el('activeMode').textContent"), "当前 · 桌面");
    assert.deepEqual(await run("[...q('#workspace').querySelectorAll('button')].filter(e=>e.offsetWidth).map(e=>e.textContent.trim())"), ["截图并翻译","取消","保留当前截图","截图来源 · 主显示器全屏"]);
    assert.deepEqual(await run("[...q('#workspace').querySelectorAll('h2')].map(e=>e.textContent)"), ["截图预览","翻译结果","识别原文"]);
    assert.deepEqual(await run("[el('cancelTask').disabled,el('saveScreenshot').disabled,el('pageTools').hidden]"), [true,true,true]);
    assert.deepEqual(await run("[el('previewEmpty').offsetWidth>0,el('triggerKey').textContent,el('triggerHint').textContent,el('source').textContent]"), [true,"Ctrl+Alt+T","截取主显示器全屏","这里显示从截图中读到的文字。"]);
    assert.ok(await run("el('cancelTask').title && el('saveScreenshot').title"), "Disabled capture actions explain when they become available");
    assert.equal(await run("sent.some(m=>['checkUpdate','downloadUpdate'].includes(m.action))"), false, "Opening the workspace does not check or download updates");
    await run("el('updateEntry').click()");
    assert.deepEqual(await run("sent.at(-1)"), {type:"action",action:"checkUpdate"});
    assert.deepEqual(await run("[el('updatePanel').matches(':popover-open'),el('updateCurrentVersion').textContent,el('updateCheck').disabled,el('updateDownload').hidden]"),
      [true,"当前版本 0.2.5-preview",true,true]);
    await run("send({type:'state',updateState:'checking',updateMessage:'正在获取发布信息'});el('updateCheck').click();el('updateDownload').click()");
    assert.equal(await run("sent.filter(m=>m.action==='checkUpdate').length"), 1);
    await run("send({type:'state',updateState:'current',updateMessage:''})");
    assert.deepEqual(await run("[el('updateStatus').textContent,el('updateCheck').disabled,el('updateDownload').hidden]"), ["已是最新版本。",false,true]);
    await run("el('updateCheck').click();send({type:'state',updateState:'error',updateMessage:'网络错误 <img src=x>'})");
    assert.deepEqual(await run("[el('updateCheck').textContent,el('updateDetail').textContent,el('updateDetail').children.length,el('updateDownload').hidden]"),
      ["重试","网络错误 <img src=x>",0,true]);
    await run("el('updateCheck').click();send({type:'state',updateState:'available',updateVersion:'0.2.6-preview',updateMessage:'',updateHasInstaller:true})");
    assert.deepEqual(await run("[el('updateStatus').textContent,el('updateDownload').hidden,el('updateDownload').disabled,el('updateDownload').textContent]"),
      ["发现新版本 0.2.6-preview",false,false,"下载新版"]);
    assert.equal(await run("sent.filter(m=>m.action==='downloadUpdate').length"), 0, "Announcing an update cannot launch a download");
    await run("el('updateDownload').click()");
    assert.deepEqual(await run("sent.at(-1)"), {type:"action",action:"downloadUpdate"});
    await run("send({type:'state',updateHasInstaller:false})");
    assert.equal(await run("el('updateDownload').textContent"), "查看发布页");
    assert.equal(await run("sent.filter(m=>m.action==='downloadUpdate').length"), 1);
    await run("el('updateDownload').click();el('updateClose').click()");
    assert.deepEqual(await run("sent.at(-1)"), {type:"action",action:"downloadUpdate"});
    assert.equal(await run("el('updatePanel').matches(':popover-open')"), false);
    await run("send({type:'state',effectiveMode:'SteamVR',settings:{controllerEnabled:true,spatialSelectionEnabled:false}})");
    assert.equal(await run("el('triggerKey').textContent"), "左 X");
    await run("send({type:'state',effectiveMode:'Desktop',settings:{hotkey:'F8'}})");
    assert.equal(await run("el('triggerKey').textContent"), "F8");
    assert.equal(await run("sent.filter(m=>m.action==='listDesktopWindows').length"), 0, "Startup does not enumerate local windows");
    await run("el('desktopCaptureEntry').click()");
    assert.deepEqual(await run("[el('quickDesktopCapture').open,el('workspace').hidden,el('desktopWindowPicker').hidden,field('desktopCaptureSource').value]"),
      [true,false,true,"PrimaryMonitor"]);
    await run("edit('desktopCaptureSource','Window')");
    assert.ok(await run("sent.some(m=>m.action==='listDesktopWindows') && el('refreshDesktopWindows').disabled"));
    await run("send({type:'desktopWindows',windows:[]});el('applyDesktopSource').click()");
    assert.ok(await run("el('desktopWindowStatus').textContent.includes('没有可选窗口') && el('quickDesktopError').textContent.includes('选择可用')"));
    await viewport(375, 630);
    assert.ok(await run("['quickDesktopCapture','refreshDesktopWindows','applyDesktopSource'].every(id=>{const r=el(id).getBoundingClientRect();return r.width>0&&r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight})"),
      "The direct workspace source picker fits the narrow viewport");
    await viewport(1150, 790);
    await run("el('refreshDesktopWindows').click();send({type:'desktopWindows',error:true})");
    assert.ok(await run("el('desktopWindowStatus').textContent.includes('重试') && !el('refreshDesktopWindows').disabled"));
    await run("window.exampleWindow={id:'1'.repeat(32),title:'<img> 合成窗口',processName:'sample'};el('refreshDesktopWindows').click();send({type:'desktopWindows',windows:[exampleWindow]}); edit('desktopWindowId',exampleWindow.id);el('applyDesktopSource').click()");
    assert.deepEqual(await run("sent.at(-1)"), {type:"save",settings:{desktopCaptureSource:"Window",desktopWindowId:"1".repeat(32)}});
    assert.equal(await run("field('desktopWindowId').querySelector('img')"), null, "Window titles are rendered as plain text");
    await run("send({type:'saved'})");
    assert.ok(await run("!el('quickDesktopCapture').open && !el('workspace').hidden && el('triggerHint').textContent.includes('指定窗口')"));
    await run("el('desktopCaptureEntry').click();send({type:'desktopWindows',windows:[]})");
    assert.equal(await run("field('desktopWindowId').value"), "1".repeat(32), "A closed window cannot silently select an alternative");
    await run("edit('desktopCaptureSource','PrimaryMonitor');el('cancelDesktopSource').click()");
    assert.ok(await run("!el('quickDesktopCapture').open && el('triggerHint').textContent.includes('指定窗口') && el('saveBar').hidden"));
    await run("showPage('capture');send({type:'desktopWindows',windows:[]});edit('desktopCaptureSource','PrimaryMonitor');el('save').click();send({type:'saved'});send({type:'state',mode:'SteamVR',effectiveMode:'SteamVR'})");
    assert.ok(await run("el('desktopSourceField').hidden && !el('vrSourceField').hidden && el('desktopWindowPicker').hidden"));
    await run("send({type:'state',mode:'Auto',effectiveMode:'Desktop'})");
    await run("showPage('services');");
    assert.equal(await run("el('pageTitle').textContent"), "账号与 AI 服务");
    assert.deepEqual(await run("[...document.querySelectorAll('[data-service-tab]')].map(e=>e.textContent)"), ["账号登录","偏好","积分服务"]);
    assert.equal(await run("el('loginPanel').hidden"), false);
    await run("el('loginButton').click()");
    assert.deepEqual(await run("sent.at(-1)"), {type:"action",action:"login"});
    await run("send({type:'state',accountLoggedIn:true,accountUsername:'test-user'});el('creditsTab').click();q('[data-action=account]').click();");
    assert.deepEqual(await run("sent.at(-1)"), {type:"action",action:"account"});
    assert.equal(await run("el('accountName').textContent"), "test-user");
    assert.equal(await run("el('creditDestination').textContent"), "自定义积分服务");
    await run("send({type:'state',settings:{creditServerUrl:'https://api.mugonzei.top/'}})");
    assert.deepEqual(await run("[el('creditDestination').textContent,el('restoreOfficialCredit').hidden]"), ["官方积分服务",true]);
    await run("el('prefsTab').click();window.dirtyAtPrefs=sent.filter(m=>m.type==='dirty'&&m.dirty).length;window.originalSelect=field('translationBackend'); originalSelect.focus();");
    const selectBox = await run("(()=>{const r=originalSelect.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()");
    await call("Input.dispatchMouseEvent", {type:"mousePressed",button:"left",clickCount:1,...selectBox});
    await call("Input.dispatchMouseEvent", {type:"mouseReleased",button:"left",clickCount:1,...selectBox});
    assert.equal(await run("originalSelect.matches(':open')"), true, "The selector popup is open");
    for (let i = 0; i < 25; i++) {
      await run("send({type:'state',busy:true,status:'处理中',effectiveMode:'SteamVR',settings:" + JSON.stringify(fixture.settings) + "})");
    }
    assert.equal(await run("document.activeElement===originalSelect && field('translationBackend')===originalSelect"), true, "State updates retain the focused select node");
    assert.equal(await run("originalSelect.matches(':open')"), true, "Live status and settings snapshots leave the popup open");
    await call("Input.dispatchKeyEvent", {type:"keyDown", key:"Escape",code:"Escape",windowsVirtualKeyCode:27});
    await call("Input.dispatchKeyEvent", {type:"keyUp", key:"Escape",code:"Escape",windowsVirtualKeyCode:27});
    await run("edit('apiModel','draft'); edit('apiKey','synthetic-placeholder'); send({type:'state',settings:" + JSON.stringify(fixture.settings) + "})");
    assert.deepEqual(await run("[field('apiModel').value,field('apiKey').value,sent.filter(m=>m.type==='dirty'&&m.dirty).length-dirtyAtPrefs]"), ["draft","synthetic-placeholder",1]);
    await run("el('updateEntry').click();el('updateCheck').click();el('updateClose').click()");
    assert.deepEqual(await run("sent.at(-1)"), {type:"action",action:"checkUpdate"}, "Checking an update never sends the settings draft");
    await run("send({type:'state',updateState:'current',updateVersion:'',updateMessage:''})");
    await run("edit('apiBaseUrl','https://different.invalid/v1'); el('testApi').click()");
    assert.deepEqual(await run("sent.at(-1)"), {type:"action",action:"testApi",settings:{apiModel:"draft",apiBaseUrl:"https://different.invalid/v1"}});
    assert.equal(await run("field('apiKey').value"), "");
    await run("el('save').click();");
    assert.deepEqual(await run("sent.at(-1)"), {type:"save",settings:{apiModel:"draft",apiBaseUrl:"https://different.invalid/v1"}});
    assert.equal(await run("el('editArea').disabled"), true);
    await run("send({type:'error',message:'保存失败'});");
    assert.deepEqual(await run("[el('editArea').disabled,field('apiModel').value,el('saveBar').hidden]"), [false,"draft",false]);
    await run("el('discard').click();edit('translationBackend','GoogleTranslate');el('save').click();");
    assert.deepEqual(await run("sent.at(-1)"), {type:"save",settings:{translationBackend:"GoogleTranslate",processingMode:"Ocr"}});
    await run("send({type:'saved',settings:{translationBackend:'GoogleTranslate',processingMode:'Ocr'}})");
    assert.deepEqual(await run("[el('saveBar').hidden,field('apiModel').value,field('processingMode').value]"), [true,"vision","Ocr"]);
    await run("showPage('capture');edit('cropX','20'); el('save').click();");
    assert.equal(await run("el('status').textContent.includes('超出画面')"), true);
    await run("edit('cropWidth','80'); el('save').click();");
    assert.deepEqual(await run("sent.at(-1)"), {type:"save",settings:{cropX:.2,cropWidth:.8}});
    await run("send({type:'saved'}); showPage('workspace'); q('input[value=Desktop]').click();");
    assert.deepEqual(await run("sent.at(-1)"), {type:"mode",mode:"Desktop"});
    await run("send({type:'state',mode:'Desktop',effectiveMode:'Desktop',busy:false,status:'就绪',source:'<img src=x onerror=alert(1)>',translation:'译文',page:1,pages:2}); q('[data-action=translate]').click();");
    assert.deepEqual(await run("sent.at(-1)"), {type:"action",action:"translate"});
    assert.equal(await run("el('source').children.length"), 0, "Submitted text cannot introduce HTML");
    assert.equal(await run("el('next').disabled"), false);
    assert.equal(await run("el('pageTools').hidden"), false);
    await run("send({type:'state',preview:'https://untrusted.invalid/image.jpg'})");
    assert.equal(await run("el('previewImage').hasAttribute('src')"), false);
    for (const width of [1150, 740, 390, 320]) {
      await viewport(width, 700);
      await run("el('updateEntry').click()");
      assert.ok(await run("['updateEntry','updatePanel','updateCheck'].every(id=>{const r=el(id).getBoundingClientRect();return r.width>0&&r.height>0&&r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight;})"),
        "The update entry and panel fit at " + width + "px");
      assert.ok(await run("el('updateEntry').innerText.trim().includes('更新')"));
      await call("Input.dispatchKeyEvent", {type:"keyDown",key:"Escape",code:"Escape",windowsVirtualKeyCode:27});
      await call("Input.dispatchKeyEvent", {type:"keyUp",key:"Escape",code:"Escape",windowsVirtualKeyCode:27});
      assert.equal(await run("el('updatePanel').matches(':popover-open')"), false, "Escape closes the update panel");
    }
    for (const width of [1280, 820, 520, 375]) {
      await viewport(width, 730);
      for (const page of ["workspace","services","capture","subtitles"]) {
        await run("showPage('" + page + "')");
        assert.ok(await run("el('pageContent').scrollWidth<=el('pageContent').clientWidth+1 && document.documentElement.scrollWidth<=innerWidth+1"), page + " fits at " + width + "px");
        if (page === "workspace") {
          const [image, translation, source] = await run("['.media-card','.translation-card','.source-card'].map(s=>{const r=q(s).getBoundingClientRect();return {x:r.x,y:r.y,right:r.right,bottom:r.bottom};})");
          assert.ok(source.y >= translation.bottom && source.x === translation.x, "The transcribed source is immediately below the translation");
          if (width >= 820) assert.ok(translation.x >= image.right && translation.y === image.y, "Translation and source sit alongside the wide preview");
          else assert.ok(translation.y >= image.bottom, "Small screens read capture, translation and source in order");
        }
      }
    }
    assert.deepEqual(await run("appErrors"), []);
    console.log("PASS embedded workspace: responsive navigation, drafts, protected API keys, translation controls and manual update states/actions");
    if (process.env.MUGONZEI_SCREENSHOT_DIR) {
      await viewport(1150, 750);
      const out = path.resolve(process.env.MUGONZEI_SCREENSHOT_DIR);
      fs.mkdirSync(out, {recursive:true});
      async function take(name) {
        const captured = await call("Page.captureScreenshot", {format:"png"});
        fs.writeFileSync(path.join(out, name + ".png"), Buffer.from(captured.data, "base64"));
      }
      await run("showPage('workspace');send({type:'state',preview:null,source:'',translation:'',captureLabel:'',page:0,pages:0,canCancel:false,mode:'Auto',effectiveMode:'Desktop',status:'就绪',settings:{hotkey:'Ctrl+Alt+T'}})");
      await take("workspace-empty");
      await run("send({type:'state',preview:(" + exampleScreenshot.toString() + ")(),source:'次の電車は 10 時に到着します。',translation:'下一班列车将于 10 点到达。',captureLabel:'截图 #1 · 09:55:12 · 主显示器 · 960 × 600',status:'翻译完成',page:1,pages:1}); el('previewImage').decode()");
      assert.deepEqual(await run("[el('previewImage').naturalWidth,el('previewEmpty').hidden,el('saveScreenshot').disabled,el('source').textContent,el('translation').textContent]"),
        [960,true,false,"次の電車は 10 時に到着します。","下一班列车将于 10 点到达。"]);
      await take("workspace");
      await run("showPage('services');el('loginTab').click();send({type:'state',accountLoggedIn:false,accountUsername:'',status:'就绪'})");
      await take("services");
      await run("showPage('workspace');send({type:'state',updateState:'available',updateVersion:'0.2.6-preview',updateHasInstaller:true,updateMessage:''});el('updateEntry').click()");
      await take("update-available");
      await viewport(320, 700);
      await take("update-available-narrow");
    }
    await call("Browser.close").catch(() => {});
  } finally {
    socket?.close();
    if (browser && browser.exitCode === null) {
      await Promise.race([once(browser, "exit"), delay(2500)]);
      if (browser.exitCode === null) browser.kill();
    }
    await new Promise(resolve => server.close(resolve));
    if (path.dirname(profile) === os.tmpdir() && path.basename(profile).startsWith("mugonzei-webui-qa-")) {
      fs.rmSync(profile, {recursive:true, force:true, maxRetries:8, retryDelay:150});
    }
  }
}
main().catch(error => { console.error(error); process.exitCode=1; });
