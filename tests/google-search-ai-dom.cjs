// Runs the production adapter against a local, synthetic HTML document in Chromium.
// The fixture supplies location.href to exercise the same-origin gates without a Google request.
const assert = require("node:assert/strict");
const fs = require("node:fs"), path = require("node:path"), os = require("node:os");
const {spawnSync} = require("node:child_process");
const browser = [process.env.CHROMIUM_PATH, ...["ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA"]
  .filter(key => process.env[key]).map(key => path.join(process.env[key],"Microsoft","Edge","Application","msedge.exe"))]
  .find(exe => exe && fs.existsSync(exe));
assert.ok(browser,"Set CHROMIUM_PATH to Edge or Chromium.");
const dir = fs.mkdtempSync(path.join(os.tmpdir(),"mu-google-ai-dom-"));
const source = fs.readFileSync(path.join(__dirname,"../src/GoogleSearchAiDom.js"),"utf8");

async function exercise(googleSearchAiDom, location) {
  const id = "0123456789abcdeffedcba9876543210", wrong = "1".repeat(32);
  const check = (ok, msg) => { if(!ok) throw new Error(msg); };
  const call = (action, arg = null, key = id) => googleSearchAiDom(action, key, arg);
  const state = (action, arg, expected) => check(call(action,arg).state === expected, action+" should be "+expected);
  const validUrl = "https://www.google.com/search?hl=en&udm=50";
  const main = document.querySelector("main"), textarea = main.querySelector("textarea");
  const add = main.querySelector("#add"), send = main.querySelector("#send");
  let uploads = 0, submits = 0, menus = 0, chip, image;
  add.onclick = () => {
    menus++;
    const file = document.createElement("input");
    file.type = "file"; file.accept = "image/jpeg,image/png"; main.append(file);
    file.onchange = () => {
      uploads++; chip = document.createElement("div"); chip.setAttribute("role","button");
      chip.setAttribute("aria-label",file.files[0].name);
      image = document.createElement("img"); image.src = URL.createObjectURL(file.files[0]); chip.append(image);
      const progress = document.createElement("span"); progress.setAttribute("role","progressbar"); progress.textContent = "Uploading"; chip.append(progress);
      main.prepend(chip);
    };
  };
  send.onclick = () => { submits++; };
  for (const bad of ["https://example.test/search?udm=50","https://www.google.com.evil.test/search?udm=50",
    "https://www.google.com/search?q=x","https://www.google.com/search?udm=50&udm=14","http://www.google.com/search?udm=50"]) {
    location.href = bad; state("inspect",null,"unsupported"); state("attach","untrusted","unsupported");
  }
  check(!menus && !uploads,"unexpected interaction outside AI Mode");
  location.href = validUrl;
  const refusal = document.createElement("iframe"); refusal.hidden = true;
  refusal.src = "https://www.google.com/sorry/index"; main.append(refusal);
  state("inspect",null,"blocked"); refusal.remove();
  const dialog = document.createElement("section"); dialog.setAttribute("role","dialog"); main.prepend(dialog);
  dialog.textContent = "Sign in to upload"; state("inspect",null,"login");
  dialog.textContent = "Verify you are human"; state("inspect",null,"blocked");
  dialog.remove();
  const foreign = document.createElement("div"); foreign.textContent = "Previous attachment";
  foreign.setAttribute("role","button"); foreign.setAttribute("aria-label","previous.png");main.prepend(foreign);
  state("inspect",null,"uncertain"); foreign.remove(); state("inspect",null,"ready");
  state("inspect",null,"ready"); check(menus===1,"repeated menu open");
  const canvas = document.createElement("canvas"); canvas.width=100; canvas.height=30;
  const png = canvas.toDataURL("image/png").split(",")[1];
  state("attach","bm90IHBuZw==","uncertain");
  window.__mugonzeiSearchAiUpload = {id:wrong,parts:[png]};
  state("attachBuffered",null,"uncertain");
  check(!uploads && !window.__mugonzeiSearchAiUpload,"wrong or partial buffer survived");
  window.__mugonzeiSearchAiUpload = {id,parts:[png.slice(0,16),png.slice(16)]};
  state("attachBuffered",null,"attached"); state("attach",png,"uncertain");
  check(uploads===1 && !window.__mugonzeiSearchAiUpload,"attachment should be used once");
  await image.decode(); state("read",null,"waiting"); state("submit","Translate.","uncertain");
  check(!submits && !textarea.value,"prompt was written before upload completed");
  chip.querySelector("[role=progressbar]").remove(); state("read",null,"uploadReady");
  chip.setAttribute("aria-label","some-other.png"); state("read",null,"waiting");
  chip.setAttribute("aria-label","mugonzei-"+id+".png"); state("submit","Translate.","submitted");
  state("submit","Translate.","uncertain"); check(submits===1,"duplicate submission");
  check(call("read",null,wrong).state==="uncertain","cross-request answer");
  const prompt = textarea.value; textarea.value="";
  // Simulate a full-document navigation which discards all state from the composer.
  delete window.__mugonzeiSearchAiTurn;
  const header=document.createElement("h1");header.textContent="AI Mode conversation: "+prompt;main.prepend(header);
  const begin=prompt.match(/MUBEGIN[a-f0-9]{12}/)[0], end=prompt.match(/MUEND[a-f0-9]{12}/)[0];
  const section = (tag, text) => { const e=document.createElement(tag); e.innerText=text;main.append(e);return e; };
  const framed = text => begin+"\n"+text+"\n"+end;
  section("nav",framed("navigation")); section("aside",framed("sources"));
  const previous=section("article",framed("previous")); main.prepend(previous);
  section("article",framed("other turn").replace(begin,"MUBEGIN000000000000"));
  const partial=section("article",begin+"\npart"); state("read",null,"waiting");
  partial.innerText=framed("蓝色窗户 37"); partial.setAttribute("aria-busy","true"); state("read",null,"waiting");
  partial.removeAttribute("aria-busy"); state("read",null,"waiting");
  await new Promise(r=>setTimeout(r,850));
  const answer=call("read");
  check(answer.state==="complete" && answer.text==="蓝色窗户 37","incomplete or unrelated answer accepted: "+answer.state);
  partial.innerText=begin+"  蓝色窗户 37  "+end;
  check(call("read").text==="蓝色窗户 37","Google-rendered soft line breaks should keep the marked answer");
  partial.innerText="Excerpt: "+begin+" different "+end;
  state("read",null,"waiting");
  partial.innerText=framed("蓝色窗户 37");
  section("article",framed("different answer")); state("read",null,"uncertain");
  return "PASS";
}

try {
  const fixture = path.join(dir,"fixture.html");
  const run = `const location={href:""};(${exercise.toString()})((${source}),location)`;
  fs.writeFileSync(fixture,`<!doctype html><html><head><meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; img-src blob:; frame-src 'none'"></head><body><main><a href="#login">Sign in</a><textarea aria-label="Ask anything"></textarea>
    <button id="add" aria-label="Add files and tools">Add</button><button id="send" aria-label="Send">Send</button></main>
    <output id="result">pending</output><script>{${run}.then(x=>result.textContent=x).catch(e=>result.textContent="FAIL "+e.message)}</script></body></html>`);
  const proc=spawnSync(browser,["--headless=new","--disable-background-networking","--disable-extensions","--no-first-run",
    "--user-data-dir="+path.join(dir,"profile"),"--virtual-time-budget=4500","--dump-dom",require("node:url").pathToFileURL(fixture).href],
    {windowsHide:true,encoding:"utf8",timeout:30000});
  assert.equal(proc.status,0,proc.error?.message || "Chromium exited unexpectedly");
  assert.equal(proc.stdout.match(/<output id="result">([^<]*)<\/output>/)?.[1],"PASS");
  console.log("PASS Google AI Mode DOM: origin, one upload, ready attachment, single submit, navigation, streamed and complete marked answers");
} finally {
  if(path.dirname(dir)===os.tmpdir() && path.basename(dir).startsWith("mu-google-ai-dom-"))
    fs.rmSync(dir,{recursive:true,force:true,maxRetries:8,retryDelay:150});
}
