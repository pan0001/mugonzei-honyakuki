'use strict';

(() => {
  const $ = id => document.getElementById(id);
  const authTabs = [$('login-tab'), $('register-tab')];
  const number = new Intl.NumberFormat('zh-CN');
  const date = new Intl.DateTimeFormat('zh-CN', { dateStyle: 'short', timeStyle: 'short' });
  const pendingRequests = new Map();
  const createdOrders = new Map();
  let token = '';
  let generation = 0;
  let mode = 'login';
  let authBusy = false;
  let verificationRequest = null;
  let issuedFor = null;
  let resendTimer;
  const resendDeadlines = new Map();
  let logoutBusy = false;
  let refreshBusy = false;
  let dashboardRequest = null;
  let checkoutBusy = false;
  let redemptionBusy = false;
  let checkoutOrderId = '';
  let plans = [];

  class ApiError extends Error {
    constructor(message, status = 0, code = '') { super(message); this.status = status; this.code = code; }
  }

  function message(text = '', tone = 'info') {
    const node = $('page-message');
    node.textContent = text;
    node.dataset.tone = tone;
    node.hidden = !text;
  }

  function clearSession() {
    issuedFor = null;
    generation++;
    token = '';
    pendingRequests.clear();
    createdOrders.clear();
    $('auth-section').hidden = false;
    $('dashboard').hidden = true;
    $('account-username').textContent = '';
    $('orders-body').replaceChildren();
    $('ledger-body').replaceChildren();
    $('checkout-message').replaceChildren();
    $('checkout-message').hidden = true;
    checkoutOrderId = '';
    for (const id of ['available-balance', 'total-balance', 'reserved-balance']) $(id).textContent = '—';
    $('password').value = '';
    $('confirm-password').value = '';
    $('register-email').value = '';
    resendDeadlines.clear();
    resetVerification();
    refreshBusy = false;
    dashboardRequest = null;
    checkoutBusy = false;
    redemptionBusy = false;
    $('redemption-code').value = '';
    $('redemption-code').disabled = false;
    $('redemption-submit').disabled = false;
    $('redemption-submit').textContent = '兑换积分';
    $('redemption-form').setAttribute('aria-busy', 'false');
    redemptionMessage();
    authBusy = false;
    updateSendButton();
    $('auth-submit').disabled = logoutBusy;
    $('auth-submit').textContent = mode === 'register' ? '创建账户 ↗' : '登录账户 ↗';
    authTabs.forEach(tab => { tab.disabled = logoutBusy; });
    $('refresh-account').disabled = false;
    $('dashboard').setAttribute('aria-busy', 'false');
  }

  async function api(path, { method = 'GET', body, authenticated = true, controller = new AbortController(), expectedStatus } = {}) {
    const requestToken = authenticated ? token : '';
    const timer = setTimeout(() => controller.abort(), 25000);
    try {
      const headers = { Accept: 'application/json' };
      if (requestToken) headers.Authorization = 'Bearer ' + requestToken;
      if (body !== undefined) headers['Content-Type'] = 'application/json';
      const response = await fetch('/v1' + path, {
        method, headers, body: body === undefined ? undefined : JSON.stringify(body),
        signal: controller.signal, credentials: 'omit', cache: 'no-store', redirect: 'error'
      });
      if (response.ok && expectedStatus !== undefined && response.status !== expectedStatus)
        throw new ApiError('此服务尚未支持邮箱验证码。', response.status);
      let data = null;
      if (response.status !== 204) {
        try { data = await response.json(); }
        catch {
          // A rate limiter or reverse proxy may return an empty error response.
          if (response.ok) throw new ApiError('服务器返回了无法读取的响应，请稍后重试。', response.status);
        }
      }
      if (!response.ok) {
        if (response.status === 401 && requestToken && token === requestToken) {
          clearSession();
          message('登录已失效，请重新登录。', 'error');
          $('username').focus();
        }
        const fallback = response.status === 429 ? '请求较频繁，请等待一分钟后重试。' : '请求未完成，请稍后重试。';
        throw new ApiError(typeof data?.message === 'string' ? data.message.slice(0, 400) : fallback, response.status, data?.error || '');
      }
      return data;
    } catch (error) {
      if (error instanceof ApiError) throw error;
      if (error.name === 'AbortError') throw new ApiError(path === '/orders' && method === 'POST'
        ? '连接超时，请重试同一方案或查看订单列表，避免重复付款。'
        : '连接超时，请稍后重试。');
      throw new ApiError('暂时连接不到服务器，请检查网络后重试。');
    } finally { clearTimeout(timer); }
  }

  function verificationMessage(text = '', tone = 'info') {
    const node = $('verification-message');
    node.textContent = text;
    node.dataset.tone = tone;
    node.hidden = !text;
  }

  function updateSendButton() {
    clearTimeout(resendTimer);
    const now = Date.now();
    for (const [email, deadline] of resendDeadlines) {
      if (deadline <= now) resendDeadlines.delete(email);
    }
    const remaining = Math.ceil(((resendDeadlines.get($('register-email').value.trim().toLowerCase()) || now) - now) / 1000);
    const button = $('send-email-code');
    button.disabled = mode !== 'register' || authBusy || logoutBusy || !!verificationRequest || remaining > 0;
    button.textContent = verificationRequest ? '正在发送…' : remaining > 0 ? remaining + ' 秒后重发' : '发送验证码';
    if (mode === 'register' && remaining > 0 && !$('auth-section').hidden) resendTimer = setTimeout(updateSendButton, 1000);
  }

  function resetVerification() {
    verificationRequest?.abort();
    verificationRequest = null;
    $('register-code').value = '';
    verificationMessage();
    updateSendButton();
    $('auth-submit').disabled = authBusy || logoutBusy;
  }

  async function sendEmailCode() {
    if (mode !== 'register' || authBusy || logoutBusy || verificationRequest) return;
    const field = $('register-email');
    const email = field.value.trim();
    if (!field.reportValidity() || !email) return;
    const key = email.toLowerCase();
    if (resendDeadlines.get(key) > Date.now()) return;
    const request = new AbortController();
    verificationRequest = request;
    updateSendButton();
    $('auth-submit').disabled = true;
    verificationMessage('正在发送验证码…');
    try {
      await api('/auth/register/email-code', { method: 'POST', authenticated: false, body: { email }, controller: request, expectedStatus: 204 });
      if (verificationRequest !== request) return;
      issuedFor = { key, expiresAt: Date.now() + 600000 };
      resendDeadlines.set(key, Date.now() + 60000);
      $('register-code').value = '';
      verificationMessage('请查看该邮箱的收件箱或垃圾邮件，输入最新一封邮件里的验证码。', 'success');
      $('register-code').focus();
    } catch (error) {
      if (verificationRequest !== request) return;
      if (error.status === 429) resendDeadlines.set(key, Date.now() + 60000);
      const detail = error.status === 429 ? '发送次数较多，请稍后再试。'
        : [200, 404, 405].includes(error.status) ? '此服务尚未支持邮箱验证码，请联系运营者更新服务端。'
        : error.status === 400 ? '请输入有效的邮箱地址。'
        : error.status === 503 ? '验证码邮件暂时无法发送，请稍后再试或联系运营者。'
        : '未能确认发送结果，请先查看邮箱，再决定是否重发。';
      verificationMessage(detail, 'error');
    } finally {
      if (verificationRequest === request) {
        verificationRequest = null;
        updateSendButton();
        $('auth-submit').disabled = authBusy || logoutBusy;
      }
    }
  }

  $('send-email-code').addEventListener('click', sendEmailCode);
  $('register-email').addEventListener('input', resetVerification);

  function setMode(next) {
    if (authBusy || logoutBusy || next === mode) return;
    mode = next;
    const registering = mode === 'register';
    authTabs.forEach((tab, index) => {
      const active = registering === (index === 1);
      tab.setAttribute('aria-selected', String(active));
      tab.tabIndex = active ? 0 : -1;
    });
    $('auth-panel').setAttribute('aria-labelledby', registering ? 'register-tab' : 'login-tab');
    $('auth-title').textContent = registering ? '创建账户' : '欢迎回来';
    $('auth-description').textContent = registering ? '用邮箱验证码完成注册，之后就能用用户名和密码登录客户端。' : '使用与客户端相同的用户名和密码。';
    $('auth-submit').textContent = registering ? '创建账户 ↗' : '登录账户 ↗';
    $('password').autocomplete = registering ? 'new-password' : 'current-password';
    $('confirm-row').hidden = !registering;
    $('confirm-password').disabled = !registering;
    $('confirm-password').required = registering;
    $('confirm-password').setCustomValidity('');
    $('confirm-password').value = '';
    $('register-email').value = '';
    $('verification-fields').hidden = !registering;
    for (const field of [$('register-email'), $('register-code')]) {
      field.disabled = !registering;
      field.required = registering;
    }
    resetVerification();
    message();
  }

  authTabs.forEach((tab, index) => {
    tab.addEventListener('click', () => setMode(index ? 'register' : 'login'));
    tab.addEventListener('keydown', event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? 1 : 1 - index;
      setMode(next ? 'register' : 'login');
      authTabs[next].focus();
    });
  });
  for (const id of ['password', 'confirm-password']) $(id).addEventListener('input', () => $('confirm-password').setCustomValidity(''));

  $('auth-form').addEventListener('submit', async event => {
    event.preventDefault();
    if (authBusy || logoutBusy || verificationRequest) return;
    if (mode === 'register' && $('password').value !== $('confirm-password').value) {
      $('confirm-password').setCustomValidity('两次输入的密码不一致。');
      $('confirm-password').reportValidity();
      return;
    }
    if (!$('auth-form').reportValidity()) return;
    const body = { username: $('username').value.trim(), password: $('password').value };
    if (mode === 'register') {
      body.email = $('register-email').value.trim();
      if (issuedFor?.key !== body.email.toLowerCase() || issuedFor.expiresAt <= Date.now()) {
        verificationMessage('请先向当前邮箱发送验证码，再输入邮件中的最新验证码。', 'error');
        $('send-email-code').focus();
        return;
      }
      body.code = $('register-code').value;
    }
    authBusy = true;
    updateSendButton();
    let authGeneration = generation;
    $('auth-submit').disabled = true;
    authTabs.forEach(tab => { tab.disabled = true; });
    $('auth-submit').textContent = mode === 'register' ? '正在创建…' : '正在登录…';
    message();
    try {
      const reply = await api('/auth/' + mode, { method: 'POST', authenticated: false, body });
      if (typeof reply?.token !== 'string' || !/^[A-Za-z0-9_-]{43}$/.test(reply.token)) throw new ApiError('登录响应无效，请稍后重试。');
      generation++;
      authGeneration = generation;
      token = reply.token;
      $('password').value = '';
      $('confirm-password').value = '';
      $('account-username').textContent = reply.username;
      $('auth-section').hidden = true;
      issuedFor = null;
      $('register-email').value = '';
      resendDeadlines.clear();
      resetVerification();
      $('dashboard').hidden = false;
      $('dashboard-title').tabIndex = -1;
      $('dashboard-title').focus();
      await loadDashboard();
    } catch (error) {
      if (authGeneration === generation && error.code === 'invalid_email_code') {
        $('register-code').value = '';
        verificationMessage('验证码有误、已过期或尝试过多。请核对最新邮件，或重新发送。', 'error');
        $('register-code').focus();
      } else if (authGeneration === generation) message(error.message, 'error');
    } finally {
      if (authGeneration === generation && !logoutBusy) {
        authBusy = false;
        updateSendButton();
        $('auth-submit').disabled = false;
        authTabs.forEach(tab => { tab.disabled = false; });
        $('auth-submit').textContent = mode === 'register' ? '创建账户 ↗' : '登录账户 ↗';
      }
    }
  });

  function node(tag, text, className) {
    const element = document.createElement(tag);
    if (text !== undefined) element.textContent = String(text);
    if (className) element.className = className;
    return element;
  }

  function formatDate(value) { const parsed = new Date(value); return Number.isNaN(parsed.getTime()) ? '—' : date.format(parsed); }
  function formatMoney(fen) { return '¥' + (Number(fen) / 100).toFixed(2); }
  function formatNumber(value) { return Number.isSafeInteger(value) ? number.format(value) : '—'; }
  function tableMessage(id, text) {
    const row = node('tr');
    const cell = node('td', text, 'empty-state'); cell.colSpan = 5;
    row.append(cell); $(id).replaceChildren(row);
  }

  function paymentLink(value) {
    try {
      const url = new URL(value);
      if (url.protocol !== 'https:' || url.port || url.username || url.password || url.hash
        || !['afdian.com', 'ifdian.net', 'afdian.net'].includes(url.hostname) || url.pathname !== '/order/create') return null;
      const link = node('a', '前往爱发电 ↗', 'button button-primary button-small');
      link.href = url.href; link.target = '_blank'; link.rel = 'noopener noreferrer';
      return link;
    } catch { return null; }
  }

  function renderPlans() {
    const list = $('plans'); list.replaceChildren();
    if (!plans.length) {
      list.append(node('p', '充值暂未开放。当前没有可购买的积分方案，请稍后再来。', 'empty-state'));
      return;
    }
    for (const plan of plans) {
      const card = node('div', undefined, 'plan-card');
      const detail = node('div');
      detail.append(node('h4', plan.name), node('p', formatNumber(plan.credits) + ' 积分 · ' + formatMoney(plan.amountFen), 'muted'));
      const existing = createdOrders.get(plan.id);
      const label = existing ? '查看待付款订单' : pendingRequests.has(plan.id) ? '重试创建订单' : '创建充值订单';
      const button = node('button', label, 'button button-primary button-small');
      button.type = 'button'; button.disabled = checkoutBusy;
      button.addEventListener('click', () => existing ? showCheckout(existing) : createOrder(plan));
      card.append(detail, button); list.append(card);
    }
  }

  function renderOrders(rows) {
    if (!rows.length) { tableMessage('orders-body', '还没有充值订单。方案开放后，可在上方选择。'); return; }
    const fragment = document.createDocumentFragment();
    const statuses = { pending: '待核验', paid: '已到账', refund_pending: '退款处理中', refunded: '已退款' };
    for (const order of rows) {
      const row = node('tr');
      row.append(node('td', formatDate(order.createdAt)));
      const idCell = node('td'); const id = node('code', String(order.id).slice(0, 10) + '…', 'record-id'); id.title = String(order.id); idCell.append(id); row.append(idCell);
      row.append(node('td', formatNumber(order.credits) + ' / ' + formatMoney(order.amountFen)));
      const status = node('td'); status.append(node('span', statuses[order.status] || '状态待确认', 'order-status status-' + (Object.hasOwn(statuses, order.status) ? order.status : 'unknown'))); row.append(status);
      const actions = node('td'); const group = node('div', undefined, 'inline-actions');
      if (order.status === 'pending') {
        const link = paymentLink(order.payUrl); if (link) group.append(link);
        const refresh = node('button', '核验到账', 'button button-ghost button-small'); refresh.type = 'button';
        refresh.addEventListener('click', () => verifyOrder(order.id, refresh)); group.append(refresh);
      } else group.append(node('span', '—', 'muted'));
      actions.append(group); row.append(actions); fragment.append(row);
    }
    $('orders-body').replaceChildren(fragment);
  }

  function renderLedger(rows) {
    if (!rows.length) { tableMessage('ledger-body', '还没有积分变动。充值和使用记录会显示在这里。'); return; }
    const labels = { recharge: '充值到账', redemption: '兑换码', translation: '翻译扣除', refund_hold: '退款预留', refund: '退款扣回', refund_release: '解除退款预留' };
    const fragment = document.createDocumentFragment();
    for (const record of rows) {
      const row = node('tr');
      row.append(node('td', formatDate(record.createdAt)), node('td', labels[record.kind] || '积分变动'), node('td', (record.amount > 0 ? '+' : '') + formatNumber(record.amount), record.amount > 0 ? 'credit-positive' : ''), node('td', formatNumber(record.balance)));
      const reference = node('td'); const code = node('code', String(record.reference).slice(0, 23) + (String(record.reference).length > 23 ? '…' : ''), 'record-id'); code.title = String(record.reference); reference.append(code); row.append(reference); fragment.append(row);
    }
    $('ledger-body').replaceChildren(fragment);
  }

  function updatePlanStatus(rows) {
    $('service-title').textContent = rows.length ? '充值方案已上架' : '充值暂未开放';
    $('service-description').textContent = rows.length ? '登录后查看方案。实际付款和到账状态以服务器核验为准。' : '当前没有可购买的方案。可先使用客户端的自有翻译服务；积分翻译是否开放以客户端服务状态为准。';
  }

  async function loadDashboard() {
    if (!token) return false;
    if (refreshBusy) return dashboardRequest;
    refreshBusy = true;
    const current = generation;
    const request = readDashboard(current);
    dashboardRequest = request;
    try { return await request; }
    catch {
      if (current === generation) message('账户信息读取失败，请稍后刷新。', 'error');
      return false;
    } finally {
      if (current === generation) {
        refreshBusy = false;
        dashboardRequest = null;
        $('refresh-account').disabled = false;
        $('dashboard').setAttribute('aria-busy', 'false');
      }
    }
  }

  async function readDashboard(current) {
    $('refresh-account').disabled = true;
    $('dashboard').setAttribute('aria-busy', 'true');
    message('正在读取账户…');
    const results = await Promise.allSettled([api('/account'), api('/ledger'), api('/orders'), api('/plans', { authenticated: false })]);
    if (current !== generation) return false;
    let failures = 0;
    if (results[0].status === 'fulfilled') {
      const account = results[0].value;
      $('account-username').textContent = account.username;
      $('available-balance').textContent = formatNumber(account.balance - account.reserved);
      $('total-balance').textContent = formatNumber(account.balance);
      $('reserved-balance').textContent = formatNumber(account.reserved);
    } else {
      failures++;
      for (const id of ['available-balance', 'total-balance', 'reserved-balance']) $(id).textContent = '—';
    }
    if (results[1].status === 'fulfilled' && Array.isArray(results[1].value)) renderLedger(results[1].value);
    else { failures++; tableMessage('ledger-body', '积分记录读取失败，请点击「刷新账户」重试。'); }
    if (results[2].status === 'fulfilled' && Array.isArray(results[2].value)) {
      const orders = results[2].value;
      for (const [planId, order] of createdOrders) {
        const fresh = orders.find(item => item.id === order.id);
        if (fresh && fresh.status !== 'pending') createdOrders.delete(planId);
        else if (fresh) createdOrders.set(planId, fresh);
      }
      const checkout = orders.find(item => item.id === checkoutOrderId);
      if (checkout) resolveCheckout(checkout);
      renderOrders(orders);
    } else { failures++; tableMessage('orders-body', '订单读取失败，请点击「刷新账户」重试。'); }
    if (results[3].status === 'fulfilled' && Array.isArray(results[3].value)) {
      plans = results[3].value; renderPlans(); updatePlanStatus(plans);
    } else {
      failures++; $('plans').replaceChildren(node('p', '充值方案暂时读取失败，请刷新账户后重试。', 'empty-state'));
    }
    message(failures ? '部分信息读取失败，请检查网络后刷新。' : '账户信息已更新。', failures ? 'error' : 'success');
    return failures === 0;
  }

  function showCheckout(order) {
    checkoutOrderId = order.id;
    const panel = $('checkout-message'); panel.replaceChildren(); panel.hidden = false;
    panel.dataset.tone = 'info';
    panel.append(node('p', '订单已创建：' + formatNumber(order.credits) + ' 积分，' + formatMoney(order.amountFen) + '。'));
    const link = paymentLink(order.payUrl);
    if (link) panel.append(link, node('p', '付款后回到这里，在订单列表点击「核验到账」。请勿重复付款。', 'form-hint'));
    else panel.append(node('p', '付款入口目前不可用，请稍后刷新订单。', 'form-hint'));
    panel.tabIndex = -1; panel.focus();
  }

  function resolveCheckout(order) {
    if (order.id !== checkoutOrderId || order.status === 'pending') return;
    const panel = $('checkout-message');
    panel.replaceChildren(node('p', order.status === 'paid' ? '这笔订单已到账，无需再次付款。' : '这笔订单的状态已更新，请查看下方订单记录。'));
    panel.dataset.tone = order.status === 'paid' ? 'success' : 'info';
  }

  async function createOrder(plan) {
    if (checkoutBusy || !token) return;
    checkoutBusy = true;
    const current = generation;
    const requestId = pendingRequests.get(plan.id) || crypto.randomUUID();
    pendingRequests.set(plan.id, requestId);
    renderPlans(); message('正在创建订单…');
    try {
      const order = await api('/orders', { method: 'POST', body: { planId: plan.id, requestId } });
      if (current !== generation) return;
      if (!order || typeof order.id !== 'string' || !/^[a-f0-9]{32}$/.test(order.id)
        || typeof order.status !== 'string' || !Number.isSafeInteger(order.credits) || !Number.isSafeInteger(order.amountFen))
        throw new ApiError('订单响应不完整，请重试同一方案或刷新订单列表。');
      pendingRequests.delete(plan.id);
      createdOrders.set(plan.id, order);
      showCheckout(order);
      if (dashboardRequest) {
        try { await dashboardRequest; } catch { /* Refresh again below. */ }
      }
      if (current !== generation) return;
      await loadDashboard();
    } catch (error) {
      if (current === generation) message(error.message + (error.status === 0 ? ' 再次点击将复用本次请求编号。' : ''), 'error');
    } finally {
      if (current === generation) { checkoutBusy = false; renderPlans(); }
    }
  }

  async function verifyOrder(id, button) {
    const current = generation;
    button.disabled = true; button.textContent = '正在核验…';
    message('正在向支付平台核验订单…');
    try {
      const order = await api('/orders/' + encodeURIComponent(id) + '?refresh=true');
      if (current !== generation) return;
      resolveCheckout(order);
      // A read started before payment verification may contain an old balance.
      // Finish that read first, then explicitly read the verified state.
      if (dashboardRequest) {
        try { await dashboardRequest; } catch { /* The new read below can recover. */ }
      }
      if (current !== generation) return;
      const refreshed = await loadDashboard();
      if (current !== generation) return;
      message(order.status === 'paid'
        ? refreshed ? '订单已到账，积分余额已刷新。' : '订单已到账；部分账户信息读取失败，请稍后刷新账户。'
        : '暂未核验到付款成功。若刚完成支付，请稍候再试。', order.status === 'paid' ? 'success' : 'info');
    } catch (error) { if (current === generation) message(error.message, 'error'); }
    finally { button.disabled = false; button.textContent = '核验到账'; }
  }

  function redemptionMessage(text = '', tone = 'info') {
    const status = $('redemption-message');
    status.textContent = text;
    status.dataset.tone = tone;
    status.hidden = !text;
  }

  async function redeemCredits() {
    if (!token || logoutBusy || redemptionBusy) return;
    const code = $('redemption-code').value.trim();
    if (code.length < 20 || code.length > 80) {
      redemptionMessage('请输入 20–80 个字符的完整兑换码。', 'error');
      $('redemption-code').focus();
      return;
    }
    const current = generation;
    redemptionBusy = true;
    $('redemption-code').disabled = true;
    $('redemption-submit').disabled = true;
    $('redemption-submit').textContent = '正在兑换…';
    $('redemption-form').setAttribute('aria-busy', 'true');
    redemptionMessage('正在核验兑换码，请稍候…');
    try {
      const result = await api('/redemptions', { method: 'POST', body: { code } });
      if (current !== generation) return;
      if (!Number.isSafeInteger(result?.credits) || result.credits < 1
        || !Number.isSafeInteger(result.balance) || result.balance < 0
        || !Number.isSafeInteger(result.reserved) || result.reserved < 0 || result.reserved > result.balance
        || typeof result.alreadyRedeemed !== 'boolean' || typeof result.reference !== 'string' || !result.reference)
        throw new ApiError('兑换响应不完整。');
      $('redemption-code').value = '';
      const summary = result.alreadyRedeemed
        ? '此兑换码已在本账户兑换过 ' + formatNumber(result.credits) + ' 积分，本次没有重复增加积分。'
        : '兑换成功，' + formatNumber(result.credits) + ' 积分已到账。';
      redemptionMessage(summary + ' 正在刷新账户…', 'success');
      // An earlier refresh may still hold the pre-redemption balance. Wait for
      // it, then start a new read so that its result cannot overwrite the credit.
      if (dashboardRequest) {
        try { await dashboardRequest; } catch { /* The fresh read below can recover. */ }
      }
      if (current !== generation) return;
      const refreshed = await loadDashboard();
      if (current !== generation) return;
      redemptionMessage(summary + (refreshed ? ' 余额和积分记录已刷新。' : ' 部分账户信息读取失败，请点击「刷新账户」。'), 'success');
    } catch (error) {
      if (current !== generation) return;
      const detail = error.status === 400 ? '兑换码不可用，请核对后重试。'
        : error.status === 409 ? '当前余额无法接收这些积分，请联系运营者。'
        : error.status === 429 ? '兑换请求较频繁，请等待一分钟后重试。'
        : '暂时无法确认兑换结果。请保留此码并重试；同一账户重复提交不会重复到账。';
      redemptionMessage(detail, 'error');
    } finally {
      if (current === generation) {
        redemptionBusy = false;
        $('redemption-code').disabled = false;
        $('redemption-submit').disabled = false;
        $('redemption-submit').textContent = '兑换积分';
        $('redemption-form').setAttribute('aria-busy', 'false');
      }
    }
  }

  $('redemption-form').addEventListener('submit', event => { event.preventDefault(); return redeemCredits(); });

  $('refresh-account').addEventListener('click', loadDashboard);
  $('logout').addEventListener('click', async () => {
    if (logoutBusy) return;
    logoutBusy = true;
    const request = api('/auth/logout', { method: 'POST' });
    clearSession();
    authBusy = true;
    $('auth-submit').disabled = true;
    authTabs.forEach(tab => { tab.disabled = true; });
    $('logout').disabled = true;
    message('正在退出…');
    $('username').focus();
    try { await request; message('已退出登录。', 'success'); }
    catch (error) {
      if (error.status === 401) message('已退出登录。', 'success');
      else message('本页已退出。服务器会话未确认撤销，该会话最长 14 天后过期。', 'error');
    }
    finally {
      logoutBusy = false;
      authBusy = false;
      updateSendButton();
      $('auth-submit').disabled = false;
      authTabs.forEach(tab => { tab.disabled = false; });
      $('logout').disabled = false;
    }
  });

  api('/plans', { authenticated: false }).then(rows => {
    if (!Array.isArray(rows)) throw new ApiError('无法读取充值方案。');
    plans = rows; updatePlanStatus(rows);
  }).catch(() => {
    $('service-title').textContent = '暂时连接不到账户后台';
    $('service-description').textContent = '请检查网络后重试。客户端的自有翻译服务不受此页面影响。';
  });
})();
