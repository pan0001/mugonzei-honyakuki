'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const bridge = window.chrome?.webview;
  if (!bridge) {
    $('loading').textContent = '请在无言势翻译器中打开配置向导。';
    return;
  }
  const send = (type, values = {}) => bridge.postMessage({type, ...values});
  const selected = name => document.querySelector('input[name="' + name + '"]:checked')?.value;
  const choose = (name, value) => {
    for (const input of document.getElementsByName(name)) input.checked = input.value === value;
  };
  let current = -1, backend = '', initialized = false, firstRun = true, loginOnly = false, pending = false, registering = false;
  let codeAddress = '', cooldownEmail = '', resendAfter = 0, codeTimer;
  let canLogin = false, keyOrigin = '', hasKey = false, editedOrigin = '', previousMode = null, modeProvider = '';
  const setText = (el, value) => { if (el.textContent !== value) el.textContent = value; };
  function origin(address) {
    try { return new URL(address).origin; } catch { return ''; }
  }
  function keyHint() {
    const reusable = hasKey && origin($('url').value) !== '' && origin($('url').value) === origin(keyOrigin);
    $('remove-key').hidden = !reusable;
    if (!reusable) $('clear-key').checked = false;
    $('key').placeholder = reusable && !$('clear-key').checked ? '已保存，留空沿用现有密钥' : '本机服务可留空';
  }
  function addressChanged() {
    const address = $('url').value;
    if (address !== editedOrigin && (!origin(address) || origin(editedOrigin) !== origin(address))) {
      $('key').value = '';
      $('model').value = '';
    }
    editedOrigin = address;
    keyHint();
  }
  function init(data) {
    current = -1; firstRun = data.firstRun; canLogin = data.canLogin; loginOnly = data.loginOnly;
    $('origin').hidden = !data.loginOrigin && canLogin;
    if (!$('origin').hidden) setText($('origin'), canLogin ? '登录到 ' + data.loginOrigin : '服务地址无效，请检查积分服务设置。');
    if (loginOnly) { initialized = true; return; }
    choose('service', data.backend); choose('runtime', data.runtimeMode || 'Auto');
    $('capture').value = data.captureMode;
    $('controller').checked = data.controllerEnabled;
    $('hotkey').value = data.hotkey;
    $('processing').value = data.processingMode; $('target').value = data.targetLanguage;
    $('codex-model').value = data.codexModel; $('exe').value = data.codexExecutablePath; $('reason').value = data.codexReasoningEffort;
    $('url').value = editedOrigin = data.apiBaseUrl; $('model').value = data.apiModel;
    $('detail').value = data.apiImageDetail; $('stream').checked = data.apiStream;
    $('key').value = ''; $('pwd').value = '';
    $('preset').replaceChildren(...data.presets.map(p => new Option(p.name, p.baseUrl)));
    initialized = true;
  }
  function updateCodeControls() {
    clearTimeout(codeTimer);
    if (!registering) return;
    const addr = $('email').value.trim().toLowerCase();
    const wait = addr && addr === cooldownEmail ? Math.max(0, Math.ceil((resendAfter - Date.now()) / 1000)) : 0;
    $('send-code').disabled = pending || !addr || wait > 0;
    setText($('send-code'), wait > 0 ? '重新发送（' + wait + ' 秒）' : '获取验证码');
    const matches = addr && addr === codeAddress && /^\d{6}$/.test($('code').value);
    $('next').disabled = pending || !matches;
    if (wait > 0) codeTimer = setTimeout(updateCodeControls, 1000);
  }
  function updateStep(data) {
    if (!initialized) return;
    const changed = current !== data.step || backend !== data.backend || registering !== data.registering;
    if (registering !== data.registering) {
      $('pwd').value = ''; $('confirm').value = ''; $('code').value = ''; $('email').value = '';
    }
    current = data.step; backend = data.backend; pending = data.busy;
    registering = data.registering; codeAddress = (data.codeAddress || '').toLowerCase();
    cooldownEmail = (data.cooldownEmail || '').toLowerCase(); resendAfter = data.resendAfterMs;
    const login = current === 1 && backend === 'Credits';
    const configuring = current === 1 && !login;
    $('providers').hidden = current !== 0; $('account').hidden = !login;
    $('config').hidden = !configuring; $('screen').hidden = current !== 2;
    $('api').hidden = backend !== 'OpenAI'; $('codex').hidden = backend !== 'Codex';
    $('search-ai-notice').hidden = backend !== 'GoogleSearchAI';
    if (!login) $('pwd').value = '';
    $('verification').hidden = !registering;
    $('register').hidden = !login || registering || !canLogin;
    for (const id of ['confirm','email','code']) $(id).disabled = pending;
    if (data.notice) { setText($('code-notice'), data.notice); $('code-notice').hidden = false; }
    else if (changed) $('code-notice').hidden = true;
    if (configuring && changed) {
      const processing = $('processing');
      const google = backend === 'GoogleTranslate';
      const searchAi = backend === 'GoogleSearchAI';
      if (modeProvider !== backend && previousMode) {
        processing.value = previousMode; previousMode = null;
      }
      modeProvider = backend;
      processing.disabled = searchAi;
      for (const option of processing.options) {
        const image = option.value === 'Image' || option.value === 'Handwriting';
        option.disabled = (google && image) || (searchAi && option.value !== 'Image');
      }
      if (processing.selectedOptions[0]?.disabled) {
        previousMode = processing.value; processing.value = searchAi ? 'Image' : 'Ocr';
      }
    }
    if (current === 2) {
      $('key').value = '';
      const image = ['Handwriting', 'Image'].includes(data.processingMode);
      const destination = {Credits:'账号积分服务及其 AI 提供方', OpenAI:'你设置的模型服务', Codex:'本机 Codex 使用的模型', GoogleTranslate:'Google 翻译', GoogleSearchAI:'Google 搜索 AI 模式'}[backend];
      setText($('disclosure'), image ? '截图会发送到' + destination + '，一次请求直接返回译文。' : '识别出的文字会发送到' + destination + '。');
    }
    hasKey = data.hasApiKey; keyOrigin = data.keyOrigin; keyHint();
    setText($('title'), current === 0 ? '选择翻译服务' : current === 2 ? '选择使用模式' : registering ? '注册账号' : login ? '登录账号' : '设置翻译服务');
    setText($('counter'), (firstRun ? '初次设置' : '配置') + '　' + (current + 1) + ' / 3');
    document.querySelector('.progress').hidden = loginOnly;
    [...$('track').children].forEach((p, index) => p.classList.toggle('done', index <= current));
    setText($('back'), loginOnly && !registering ? '取消' : current === 0 ? '退出' : '返回');
    setText($('next'), pending ? '请稍候…' : registering ? '注册并登录' : login ? '登录' : current === 2 ? '开始使用' : '下一步');
    $('next').disabled = pending || (login && !canLogin);
    $('uname').disabled = $('pwd').disabled = pending || !canLogin;
    $('register').disabled = pending;
    updateCodeControls();
    $('vr-options').hidden = selected('runtime') === 'Desktop';
    setText($('error'), data.error || ''); $('error').hidden = !data.error;
    $('loading').hidden = true; $('wizard').hidden = false;
    if (changed) {
      document.querySelector('main').scrollTop = 0;
      if (login && canLogin) $('uname').focus({preventScroll:true}); else $('title').focus({preventScroll:true});
    }
  }
  $('wizard').addEventListener('submit', event => {
    event.preventDefault();
    if (pending || $('next').disabled) return;
    if (current === 0) send('service', {backend:selected('service')});
    else if (current === 2) send('finish', {runtimeMode:selected('runtime'), hotkey:$('hotkey').value, captureMode:$('capture').value, controller:$('controller').checked});
    else if (backend === 'Credits') {
      if (registering && $('pwd').value !== $('confirm').value) {
        setText($('error'), '两次填写的密码不同。'); $('error').hidden = false; $('confirm').focus(); return;
      }
      const fields = {username:$('uname').value, password:$('pwd').value};
      if (registering) Object.assign(fields, {email:$('email').value, code:$('code').value});
      send(registering ? 'signup' : 'login', fields);
    }
    else {
      const fields = {processingMode:$('processing').value, targetLanguage:$('target').value};
      if (backend === 'OpenAI') Object.assign(fields, {apiBaseUrl:$('url').value, apiModel:$('model').value, apiKey:$('key').value,
        clearKey:$('clear-key').checked, apiImageDetail:$('detail').value, apiStream:$('stream').checked});
      if (backend === 'Codex') Object.assign(fields, {codexPath:$('exe').value, codexModel:$('codex-model').value, reasoning:$('reason').value});
      send('configure', fields);
    }
  });
  $('back').addEventListener('click', () => send(current === 0 ? 'cancel' : 'back'));
  $('register').addEventListener('click', () => send('register'));
  $('send-code').addEventListener('click', () => send('sendCode', {email:$('email').value}));
  $('email').addEventListener('input', () => { $('code').value = ''; $('code-notice').hidden = true; updateCodeControls(); });
  $('code').addEventListener('input', updateCodeControls);
  $('url').addEventListener('input', addressChanged);
  $('clear-key').addEventListener('change', keyHint);
  $('preset').addEventListener('change', () => {
    if (!$('preset').value) return;
    $('url').value = $('preset').value; addressChanged();
  });
  document.querySelector('#screen fieldset').addEventListener('change', () => { $('vr-options').hidden = selected('runtime') === 'Desktop'; });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && !event.defaultPrevented) { event.preventDefault(); send(current > 0 ? 'back' : 'cancel'); }
  });
  bridge.addEventListener('message', event => {
    const data = event.data;
    if (data?.type === 'init') init(data);
    if (data?.type === 'step') updateStep(data);
  });
  send('ready');
})();
