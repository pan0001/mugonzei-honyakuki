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
const context = vm.createContext({ document, Intl, Date, URL, AbortController, setTimeout, clearTimeout, crypto, console, fetch: (...args) => transport(...args) });
const source = fs.readFileSync(path.join(__dirname, '..', 'website', 'account.js'), 'utf8');
// Expose closure entry points only in this in-memory test copy; production
// source and its account/session state remain unchanged.
const instrumented = source.replace(/\}\)\(\);\s*$/, `globalThis.review = { paymentLink, createOrder, verifyOrder, loadDashboard, renderOrders, renderLedger, api, clearSession, setSession: value => { token = value; generation++; }, getToken: () => token, getPending: id => pendingRequests.get(id) }; })();`);
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
  console.log('PASS ' + assertions + ' website account security and workflow assertions');
}
main().catch(error => { console.error(error.message); process.exitCode = 1; });
