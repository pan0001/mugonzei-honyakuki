// Run from the repository root: node tests/website-account.cjs (Node.js 20+).
// Executes the actual website script in an isolated VM with a minimal DOM and
// mocked fetch. No network requests, real credentials or payment actions occur.
// Browser layout/accessibility and live backend integration require separate QA.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
let assertions = 0;
function check(value, message) { assert.ok(value, message); assertions++; }
class Element {
  constructor(tag = 'div') { this.tagName = tag; this.children = []; this.dataset = {}; this.hidden = false; this.disabled = false; this.value = ''; this.events = {}; this.attributes = {}; this.textContent = ''; }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; this.textContent = ''; }
  setAttribute(name, value) { this.attributes[name] = value; }
  addEventListener(name, fn) { this.events[name] = fn; }
  focus() {}
  reportValidity() { return true; }
  setCustomValidity() {}
  set innerHTML(value) { throw new Error('Unsafe innerHTML assignment: ' + value); }
}
const elements = new Map();
const element = id => { if (!elements.has(id)) elements.set(id, new Element()); return elements.get(id); };
const document = { getElementById: element, createElement: tag => new Element(tag), createDocumentFragment: () => new Element('fragment') };
const response = (body, status = 200) => ({ status, ok: status >= 200 && status < 300, json: async () => body });
let transport = async () => response([]);
class TestDate extends Date { static offset = 0; static now() { return Date.now() + this.offset; } }
const context = vm.createContext({ document, Intl, Date: TestDate, URL, AbortController, setTimeout, clearTimeout, crypto, console, fetch: (...args) => transport(...args) });
const source = fs.readFileSync(path.join(__dirname, '..', 'website', 'account.js'), 'utf8');
// Expose closure entry points only in this in-memory test copy; production
// source and its account/session state remain unchanged.
const instrumented = source.replace(/\}\)\(\);\s*$/, `globalThis.review = { paymentLink, createOrder, verifyOrder, redeemCredits, loadDashboard, renderOrders, renderLedger, api, clearSession, setSession: value => { token = value; generation++; }, getToken: () => token, getPending: id => pendingRequests.get(id) }; })();`);
check(instrumented !== source, 'Test instrumentation attached');
vm.runInContext(instrumented, context);
const api = context.review;
const walk = node => [node, ...node.children.flatMap(child => child instanceof Element ? walk(child) : [])];
async function main() {
  await new Promise(resolve => setImmediate(resolve));
  for (const host of ['afdian.com', 'afdian.net', 'ifdian.net']) {
    const link = api.paymentLink(`https://${host}/order/create?custom_order_id=abc`);
    check(link?.target === '_blank' && link.rel === 'noopener noreferrer', 'Safe Afdian payment link supported');
  }
  for (const value of [null, '', '/order/create', 'javascript:alert(1)', 'http://afdian.com/order/create', 'https://afdian.com.evil.test/order/create', 'https://evil.test/afdian.com/order/create', 'https://user@afdian.com/order/create', 'https://afdian.com:8443/order/create', 'https://afdian.com/order/create#x', 'https://afdian.com/order/other', 'https://www.afdian.com/order/create']) {
    check(api.paymentLink(value) === null, 'Unsafe payment URL rejected');
  }
  const order = { id: crypto.randomUUID().replaceAll('-', ''), status: 'pending', credits: 10, amountFen: 1000, createdAt: new Date().toISOString(), payUrl: 'https://afdian.com/order/create?custom_order_id=example' };
  const plan = { id: 'test-plan', name: '测试计划', credits: 10, amountFen: 1000 };
  const ids = [];
  let fail = true;
  let paid = false;
  transport = async (path, options) => {
    if (path === '/v1/orders' && options.method === 'POST') {
      ids.push(JSON.parse(options.body).requestId);
      if (fail) { fail = false; throw new Error('network reset after server commit'); }
      return response(order);
    }
    if (path.startsWith('/v1/orders/') && path.endsWith('?refresh=true')) { paid = true; return response({ ...order, status: 'paid', payUrl: null }); }
    if (path === '/v1/account') return response({ username: 'tester', balance: paid ? 10 : 0, reserved: 0 });
    if (path === '/v1/ledger') return response([]);
    if (path === '/v1/orders') return response([{ ...order, status: paid ? 'paid' : 'pending', payUrl: paid ? null : order.payUrl }]);
    if (path === '/v1/plans') return response([plan]);
    throw new Error('Unexpected path ' + path);
  };
  api.setSession('a'.repeat(43));
  await api.createOrder(plan);
  check(api.getPending(plan.id), 'Unknown outcome retains request ID');
  await api.createOrder(plan);
  check(ids.length === 2 && ids[0] === ids[1], 'Retry reuses same request ID');
  check(walk(element('checkout-message')).some(el => el.tagName === 'a'), 'Pending order offers payment link');
  await api.verifyOrder(order.id, new Element('button'));
  check(!walk(element('checkout-message')).some(el => el.tagName === 'a'), 'Paid order removes stale checkout payment link');
  check(element('available-balance').textContent === '10', 'Paid order refresh updates balance');
  paid = false;
  const normalTransport = transport;
  let releaseOldAccount;
  let accountReads = 0;
  transport = async (path, options) => {
    if (path === '/v1/account' && ++accountReads === 1) return new Promise(resolve => { releaseOldAccount = () => resolve(response({ username: 'tester', balance: 0, reserved: 0 })); });
    return normalTransport(path, options);
  };
  const staleRefresh = api.loadDashboard();
  const verification = api.verifyOrder(order.id, new Element('button'));
  await new Promise(resolve => setImmediate(resolve));
  releaseOldAccount();
  await Promise.all([staleRefresh, verification]);
  check(accountReads === 2 && element('available-balance').textContent === '10', 'Concurrent pre-payment refresh cannot retain stale balance after verification');
  api.renderOrders([{ ...order, id: '<img src=x onerror=alert(1)>', payUrl: 'javascript:alert(1)' }]);
  check(!walk(element('orders-body')).some(el => el.tagName === 'a'), 'Malicious payment link omitted from order table');
  check(walk(element('orders-body')).some(el => String(el.textContent).includes('<img src=x')), 'Untrusted order ID stays literal text');
  transport = async () => response({ error: 'authentication_required', message: '请重新登录。' }, 401);
  await api.api('/account').catch(() => {});
  check(api.getToken() === '', '401 revokes in-memory session');
  check(element('dashboard').hidden && !element('auth-section').hidden, '401 returns to sign-in');
  await redemptionTests();
  await registrationTests();
  console.log('PASS ' + assertions + ' website account security and workflow assertions');
}

async function registrationTests() {
  const click = id => element(id).events.click();
  const submit = () => element('auth-form').events.submit({ preventDefault() {} });
  const email = element('register-email');
  const code = element('register-code');
  const send = element('send-email-code');
  const notice = element('verification-message');
  const mail = 'synthetic@example.invalid';
  const html = fs.readFileSync(path.join(__dirname, '..', 'website', 'account.html'), 'utf8');
  check(/id="register-email"[^>]*type="email"[^>]*disabled/.test(html)
    && /id="register-code"[^>]*inputmode="numeric"[^>]*autocomplete="one-time-code"[^>]*pattern="\[0-9\]\{6\}"[^>]*disabled/.test(html), 'Registration-only fields have native email and six ASCII digit validation');
  check(['register-email', 'register-code'].every(id => html.includes('label for="' + id + '"'))
    && /id="verification-message"[^>]*role="status"[^>]*aria-live="polite"/.test(html), 'Email and code have accessible labels and delivery feedback');
  click('register-tab');
  check(email.required && code.required && !email.disabled && !code.disabled && !element('verification-fields').hidden, 'Registration enables the two required email controls');
  let posts = [];
  let releaseSend;
  transport = async (path, options) => {
    posts.push({ path, options });
    return new Promise(resolve => { releaseSend = resolve; });
  };
  email.value = mail; email.events.input();
  const old = click('send-email-code');
  await click('send-email-code');
  await submit();
  check(posts.length === 1 && send.disabled && element('auth-submit').disabled, 'An in-flight email request prevents duplicate sending or submitting the register form');
  check(posts[0].path === '/v1/auth/register/email-code' && posts[0].options.method === 'POST'
    && posts[0].options.body === JSON.stringify({ email: mail }) && !posts[0].options.headers.Authorization, 'Sending a code transmits only the email in an anonymous POST body');
  code.value = '932188';
  click('login-tab');
  check(posts[0].options.signal.aborted && !email.value && !code.value && email.disabled && code.disabled && element('verification-fields').hidden, 'Switching to login aborts delivery and removes registration fields from the form');
  releaseSend(response(null, 204)); await old;
  check(notice.hidden && !element('auth-submit').disabled, 'Late send reply cannot change the login screen');

  click('register-tab'); email.value = mail; email.events.input();
  const wrongAddress = click('send-email-code');
  email.value = 'next@example.invalid'; email.events.input();
  check(posts[1].options.signal.aborted && !send.disabled, 'Editing the address aborts the old delivery and allows the corrected address');
  releaseSend(response(null, 204)); await wrongAddress;
  check(notice.hidden && !send.disabled, 'A late reply for the previous address never marks the new address as sent');
  let legacySignupCalls = 0;
  transport = async path => {
    if (path === '/v1/auth/register') legacySignupCalls++;
    return response({}, 200); // Unexpected successful body from an old proxy/host.
  };
  await click('send-email-code');
  code.value = '012345';
  await submit();
  check(!legacySignupCalls && notice.dataset.tone === 'error', 'Never register through a legacy service that did not acknowledge delivery with 204');
  transport = async () => response({ error: 'unavailable', message: 'raw upstream' }, 503);
  await click('send-email-code');
  check(notice.dataset.tone === 'error' && notice.textContent.includes('暂时无法发送') && !notice.textContent.includes('raw upstream') && !send.disabled, 'Missing SMTP gives actionable, sanitized feedback and permits retry');
  transport = async () => ({ ok: false, status: 429, json: async () => { throw new SyntaxError(); } });
  await click('send-email-code');
  check(send.disabled && send.textContent.includes('秒后重发') && notice.textContent.includes('稍后'), 'An empty HTTP 429 response starts a local cooldown');
  TestDate.offset += 61000;
  let sends = 0;
  transport = async () => { sends++; return response(null, 204); };
  email.events.input();
  await click('send-email-code'); await click('send-email-code');
  check(sends === 1 && notice.dataset.tone === 'success' && send.disabled && !element('auth-submit').disabled, 'Successful sending enforces one minute between requests but permits submitting the code');

  const secret = 'test-only-password';
  element('username').value = 'demo_user'; element('password').value = secret; element('confirm-password').value = secret;
  code.value = '043210';
  let submitted;
  transport = async (path, options) => { submitted = { path, body: JSON.parse(options.body) }; return response({ error: 'invalid_email_code', message: 'private' }, 400); };
  await submit();
  check(submitted.path === '/v1/auth/register' && submitted.body.email === email.value && submitted.body.code === '043210'
    && submitted.body.username === 'demo_user' && submitted.body.password === secret && Object.keys(submitted.body).length === 4, 'Registration sends the complete contract and preserves a leading zero');
  check(!code.value && notice.dataset.tone === 'error' && notice.textContent.includes('重新发送') && !notice.textContent.includes('private'), 'An invalid or expired code is cleared and can be re-entered');
  code.value = '012345';
  transport = async path => path === '/v1/auth/register' ? response({ token: 'z'.repeat(43), username: 'demo_user' })
    : path === '/v1/account' ? response({ username: 'demo_user', balance: 0, reserved: 0 }) : response([]);
  await submit();
  check(!element('dashboard').hidden && api.getToken() === 'z'.repeat(43) && !code.value && !email.value
    && !element('password').value && !element('confirm-password').value, 'Successful registration opens the dashboard and clears the code, email and passwords');
  api.clearSession(); click('login-tab');
  element('username').value = 'legacy'; element('password').value = secret;
  transport = async (path, options) => { submitted = { path, body: JSON.parse(options.body) }; return response({ error: 'invalid_credentials' }, 401); };
  await submit();
  check(submitted.path === '/v1/auth/login' && submitted.body.username === 'legacy' && Object.keys(submitted.body).length === 2 && !code.required && !email.required, 'Legacy logins keep the username and password contract');
}

async function redemptionTests() {
  const code = ' mu 0123 4567 89ab cdef 0123 4567 89ab cdef ';
  const reply = { credits: 25, balance: 25, reserved: 0, alreadyRedeemed: false, reference: 'redemption:fixture' };
  let balance = 0;
  const read = async path => {
    if (path === '/v1/account') return response({ username: 'redeemer', balance, reserved: 0 });
    if (path === '/v1/ledger') return response(balance ? [{ kind: 'redemption', amount: 25, balance, reference: reply.reference, createdAt: new Date().toISOString() }] : []);
    if (path === '/v1/orders' || path === '/v1/plans') return response([]);
    throw new Error('Unexpected fixture path ' + path);
  };
  api.clearSession(); api.setSession('r'.repeat(43));
  let posts = 0;
  const submitted = [];
  let loseFirstResponse;
  transport = async (path, options) => {
    if (path !== '/v1/redemptions') return read(path);
    posts++;
    check(options.method === 'POST' && options.headers.Authorization === 'Bearer ' + 'r'.repeat(43), 'Redemption uses authenticated POST');
    const body = JSON.parse(options.body);
    check(Object.keys(body).length === 1 && body.code === code.trim(), 'Only the entered code is submitted; server handles case/spaces');
    submitted.push(body.code);
    if (posts === 1) {
      balance = 25; // Server committed, but its response is lost.
      return new Promise((resolve, reject) => { loseFirstResponse = () => reject(new Error('connection reset')); });
    }
    return response({ ...reply, alreadyRedeemed: true });
  };
  for (const invalid of ['x'.repeat(19), 'x'.repeat(81)]) {
    element('redemption-code').value = invalid;
    await api.redeemCredits();
  }
  check(posts === 0 && element('redemption-message').dataset.tone === 'error', 'Invalid input length never reaches server');
  element('redemption-code').value = code;
  const first = element('redemption-form').events.submit({ preventDefault() {} });
  check(element('redemption-submit').disabled && element('redemption-code').disabled, 'Code and submit are disabled during redemption');
  check(element('redemption-form').attributes['aria-busy'] === 'true', 'Pending redemption is announced as busy');
  await api.redeemCredits();
  check(posts === 1, 'Rapid duplicate submission is ignored');
  loseFirstResponse(); await first;
  check(element('redemption-code').value === code && !element('redemption-submit').disabled, 'Network failure retains code and permits explicit retry');
  check(element('redemption-message').textContent.includes('无法确认') && !element('redemption-message').textContent.includes(code.trim()), 'Unknown outcome explains safe retry without echoing code');
  await api.redeemCredits();
  check(submitted.length === 2 && submitted[0] === submitted[1], 'Retry submits the same code after a lost response');
  check(element('redemption-code').value === '' && element('available-balance').textContent === '25', 'Confirmed retry clears code and refreshes balance');
  check(element('redemption-message').textContent.includes('25') && element('redemption-message').textContent.includes('没有重复'), 'Already-redeemed result does not claim a second credit');
  check(walk(element('ledger-body')).some(node => node.textContent === '兑换码'), 'Redemption ledger entries have a recognizable label');
  check(!element('redemption-submit').disabled && element('plans').children.some(node => String(node.textContent).includes('暂未开放')), 'Redemption remains available without payment plans');

  // A dashboard GET begun before redemption must finish before the post-credit refresh.
  let accountReads = 0;
  let releaseOldAccount;
  balance = 50;
  transport = async path => {
    if (path === '/v1/redemptions') return response({ ...reply, balance: 50 });
    if (path === '/v1/account' && ++accountReads === 1) return new Promise(resolve => { releaseOldAccount = () => resolve(response({ username: 'redeemer', balance: 25, reserved: 0 })); });
    return read(path);
  };
  element('redemption-code').value = code;
  const staleRefresh = api.loadDashboard();
  const redemption = api.redeemCredits();
  await new Promise(resolve => setImmediate(resolve));
  check(accountReads === 1 && element('redemption-submit').disabled, 'Post-redemption refresh waits for an existing dashboard request');
  releaseOldAccount(); await Promise.all([staleRefresh, redemption]);
  check(accountReads === 2 && element('available-balance').textContent === '50', 'Old dashboard response cannot retain a stale balance after redemption');
  check(element('redemption-message').textContent.includes('兑换成功，25'), 'A new redemption displays its credited amount');

  // Errors leave the code recoverable and use local messages, never raw server text.
  for (const [status, error, expected] of [[400, 'redemption_unavailable', '不可用'], [409, 'credit_limit', '余额'], [429, 'rate_limited', '一分钟'], [500, 'server_error', '无法确认']]) {
    transport = async () => response({ error, message: 'RAW ' + code }, status);
    element('redemption-code').value = code;
    await api.redeemCredits();
    check(element('redemption-code').value === code && !element('redemption-submit').disabled, 'Rejected redemption keeps input and re-enables submission');
    check(element('redemption-message').textContent.includes(expected) && !element('redemption-message').textContent.includes('RAW'), 'Redemption failure is readable without reflecting server-supplied code');
  }
  transport = async () => response({ ...reply, credits: 0 });
  await api.redeemCredits();
  check(element('redemption-code').value === code && element('redemption-message').dataset.tone === 'error', 'Malformed success is an unknown outcome and retains code');

  // Confirmed redemption remains confirmed if only its dashboard refresh fails.
  transport = async path => path === '/v1/redemptions' ? response(reply) : Promise.reject(new Error('dashboard offline'));
  await api.redeemCredits();
  check(element('redemption-code').value === '' && element('redemption-message').textContent.includes('兑换成功') && element('redemption-message').textContent.includes('读取失败'), 'Refresh failure cannot turn a confirmed redemption into a failed redemption');

  // Logout clears the input immediately and a late old-session response must not
  // clear or enable a newly signed-in user's independent pending redemption.
  api.clearSession(); api.setSession('o'.repeat(43));
  const deferred = [];
  let lateReads = 0;
  transport = async (path, options) => {
    if (path === '/v1/redemptions') return new Promise(resolve => deferred.push(resolve));
    if (path === '/v1/auth/logout') return response(null, 204);
    lateReads++; return read(path);
  };
  element('redemption-code').value = code;
  const old = api.redeemCredits();
  await element('logout').events.click();
  check(api.getToken() === '' && element('redemption-code').value === '' && element('redemption-message').hidden, 'Logout immediately clears redemption input and status');
  api.setSession('n'.repeat(43));
  const nextCode = 'MU-AAAA-BBBB-CCCC-DDDD-EEEE-FFFF-AAAA-BBBB';
  element('redemption-code').value = nextCode;
  const next = api.redeemCredits();
  deferred[0](response(reply)); await old;
  check(lateReads === 0 && element('redemption-code').value === nextCode && element('redemption-submit').disabled, 'Late old-session success cannot refresh or change new-session controls');
  deferred[1](response({ error: 'redemption_unavailable' }, 400)); await next;
  check(!element('redemption-submit').disabled && element('redemption-code').value === nextCode, 'Current-session completion releases only its own controls');
  transport = async () => response({ error: 'authentication_required' }, 401);
  await api.redeemCredits();
  check(api.getToken() === '' && element('redemption-code').value === '' && element('redemption-message').hidden, 'Expired session clears the code without a late redemption error replacing sign-in status');
  const html = fs.readFileSync(path.join(__dirname, '..', 'website', 'account.html'), 'utf8');
  check(html.includes('label for="redemption-code"') && /id="redemption-message"[^>]*role="status"[^>]*aria-live="polite"/.test(html), 'Redemption input and result have accessible labels/live status');
  check(/id="redemption-code"[^>]*autocomplete="off"[^>]*minlength="20"[^>]*maxlength="80"/.test(html), 'Code input avoids autofill and matches the length contract');
}
main().catch(error => { console.error(error.message); process.exitCode = 1; });
