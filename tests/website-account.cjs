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
      const body = JSON.parse(options.body);
      check(Object.keys(body).sort().join(',') === 'planId,requestId', 'Ordinary orders retain their original endpoint and body');
      ids.push(body.requestId);
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
  await couponTests();
  await firstRechargeTests();
  await redemptionTests();
  await registrationTests();
  console.log('PASS ' + assertions + ' website account security and workflow assertions');
}

async function firstRechargeTests() {
  api.clearSession(); api.setSession('b'.repeat(43));
  const plans = [
    {id:'credits-200',name:'200积分',credits:200,amountFen:500,firstRechargeBonusCredits:100},
    {id:'credits-500',name:'500积分',credits:500,amountFen:1000,firstRechargeBonusCredits:100},
    {id:'custom',name:'自定义套餐',credits:40,amountFen:100,firstRechargeBonusCredits:20}
  ];
  const pending = {id:crypto.randomUUID().replaceAll('-',''), planId:plans[0].id, credits:200, status:'pending', amountFen:500,
    creditedCredits:null, firstRechargeBonusCredits:0, potentialFirstRechargeBonusCredits:100, payUrl:'https://afdian.com/order/create?custom_order_id=example'};
  let eligible = true;
  let orders = [pending];
  let accountOffline = false;
  transport = async (url, opts) => {
    if (url === '/v1/plans') return response(plans);
    if (url === '/v1/account') return accountOffline ? response({},503) : response({username:'test',balance:73,reserved:0,firstRechargeEligible:eligible});
    if (url === '/v1/ledger') return response([]);
    if (url === '/v1/orders') return response(opts.method === 'POST' ? pending : orders);
    if (url === '/v1/checkout/quote') return response({planId:plans[0].id,credits:200,amountFen:350,originalAmountFen:500,discountFen:150,firstRechargeBonusCredits:eligible ? 100 : 0});
    throw new Error('Unexpected first recharge path');
  };
  const texts = id => walk(element(id)).map(e => e.textContent).join(' ');
  const planTitles = () => walk(element('plans')).filter(e => e.tagName === 'h4').map(e => e.textContent).join('|');
  await api.loadDashboard();
  check(planTitles() === '5 元首充|10 元首充|自定义套餐', 'Eligible plan headings replace only base-quantity names; a custom name remains');
  check(texts('plans').includes('¥5.00 · 若本单首笔到账，共 300 积分（常规 200 + 首充赠 100）')
    && texts('plans').includes('¥10.00 · 若本单首笔到账，共 600 积分（常规 500 + 首充赠 100）'), 'Both offers show the potential total and their regular quantities with a true account eligibility');
  check(texts('service-description').includes('常规 200 次') && texts('service-description').includes('常规 500 次') &&
    texts('first-recharge-status').includes('共用一次'), 'The account explains one first payment across tiers, independent of an existing positive balance');
  await api.createOrder(plans[0]);
  check(texts('checkout-message').includes('若本单首笔到账，共 300') && texts('checkout-message').includes('应付 ¥5.00') &&
    texts('checkout-message').includes('爱发电套餐名可能只显示常规积分'), 'Creating an eligible pending order displays a conditional bonus, amount, and local bonus attribution');
  const coupon = walk(element('plans')).find(e=>e.textContent==='使用优惠码');
  coupon.events.click(); element('coupon-code').value='TEST'; await element('coupon-form').events.submit({preventDefault(){}});
  check(texts('quote-credits').includes('300 积分（常规 200 + 首充赠 100）') && texts('quote-total').includes('¥3.50'),
    'A first recharge coupon shows its own discounted amount and independently quoted potential bonus');
  const paid = {...pending,id:crypto.randomUUID().replaceAll('-',''),status:'paid',credits:500,amountFen:1000,creditedCredits:600,firstRechargeBonusCredits:100};
  eligible=false; orders=[pending,paid]; await api.loadDashboard();
  check(planTitles() === '200积分|500积分|自定义套餐', 'Regular customers see the original plan names');
  check(!texts('plans').includes('首充赠') && texts('first-recharge-status').includes('已有充值记录') && texts('quote-credits') === '200',
    'After another order settles, neither the plan nor a previously open quote promises the first bonus');
  check(texts('orders-body').includes('到账 600 积分（含首充赠 100）') && texts('orders-body').includes('实付 ¥10.00') &&
    !texts('orders-body').includes('若本单首笔到账'), 'Paid history shows what actually arrived while remaining pending orders revert to base');
  api.renderOrders([{...paid,creditedCredits:500,firstRechargeBonusCredits:0},{...paid,status:'refunded'}]);
  check(texts('orders-body').includes('到账 500 积分') && texts('orders-body').includes('原到账 600 积分（含首充赠 100）'),
    'Ordinary payments and refunded first payments retain their own historic actual amounts');
  eligible=true; await api.loadDashboard(); accountOffline=true; await api.loadDashboard();
  check(texts('first-recharge-status').includes('暂未确认') && !texts('plans').includes('首充赠') &&
    !texts('orders-body').includes('若本单首笔到账'), 'A failed account fetch clears stale eligibility and does not derive it from recent orders');
  api.clearSession();
  check(element('first-recharge-status').hidden && !texts('first-recharge-status'), 'Sign-out clears account-specific first payment state');
  const home = fs.readFileSync(path.join(__dirname,'..','website','index.html'),'utf8');
  check(home.includes('5 元 <span>300 次') && home.includes('10 元 <span>600 次') && home.includes('常规充值 200 次') &&
    home.includes('常规充值 500 次') && home.includes('每个账户共享一次首充奖励'), 'Public offers disclose both regular and promotional totals and the account-wide limit');
}

async function couponTests() {
  const plan = {id:'credits-50', name:'50 积分', credits:50, amountFen:1000};
  const quote = {planId:plan.id, credits:50, originalAmountFen:1000, discountFen:250, amountFen:750};
  const order = {id:crypto.randomUUID().replaceAll('-', ''), planId:plan.id, status:'pending', createdAt:new Date().toISOString(),
    credits:50, originalAmountFen:1000, discountFen:250, amountFen:750, payUrl:'https://afdian.com/order/create?custom_order_id=example'};
  const posts = [];
  let orders = [];
  let quoteReply = async () => response(quote);
  let orderReply = async () => response(order);
  let ordinaryPosts = 0;
  const fixture = async (url, options) => {
    if (options.method === 'POST') {
      const body = JSON.parse(options.body);
      posts.push({url, body});
      check(options.headers.Authorization === 'Bearer ' + 'k'.repeat(43), 'Coupon requests carry the current account token');
      if (url === '/v1/checkout/quote') return quoteReply(options);
      if (url === '/v1/checkout/orders') return orderReply();
      ordinaryPosts++;
      return response({}, 500);
    }
    if (url === '/v1/plans') return response([plan]);
    if (url === '/v1/orders') return response(orders);
    if (url === '/v1/ledger') return response([]);
    if (url === '/v1/account') return response({username:'fixture', balance:0, reserved:0});
    throw new Error('Unexpected coupon fixture path');
  };
  const setAccount = async () => { api.clearSession(); api.setSession('k'.repeat(43)); transport = fixture; await api.loadDashboard(); };
  const planButton = label => walk(element('plans')).find(e => e.tagName === 'button' && e.textContent === label);
  const open = () => planButton('使用优惠码').events.click();
  const input = value => { element('coupon-code').value = value; element('coupon-code').events.input(); };
  const submit = () => element('coupon-form').events.submit({preventDefault(){}});
  const confirm = () => element('coupon-confirm').events.click();
  const hasPayLink = () => walk(element('checkout-message')).some(e => e.tagName === 'a');
  const createPosts = () => posts.filter(p => p.url === '/v1/checkout/orders');
  const unsupported = '此积分服务暂不支持优惠码，如需原价请主动选择按原价购买。';
  await setAccount(); open();
  check(!element('coupon-panel').hidden && element('coupon-confirm').hidden, 'Opening the coupon editor makes no order or payment link');
  for (const bad of ['', 'X'.repeat(81)]) { input(bad); await submit(); }
  check(posts.length === 0 && element('coupon-message').dataset.tone === 'error', 'Unusable coupon input does not reach either checkout endpoint');

  let release;
  let signal;
  quoteReply = options => { signal = options.signal; return new Promise(resolve => { release = resolve; }); };
  input('old-code'); const stale = submit(); await submit();
  check(posts.length === 1 && element('coupon-preview').disabled, 'An in-flight quote blocks duplicate pricing requests');
  input(' new-Code ');
  check(signal.aborted, 'Changing the code immediately aborts the old quote');
  release(response(quote)); await stale;
  check(element('coupon-quote').hidden && element('coupon-confirm').disabled, 'A late quote cannot be confirmed for a changed code');
  for (const invalid of [null, {...quote,planId:'other'}, {...quote,discountFen:0}, {...quote,amountFen:0}, {...quote,amountFen:751}]) {
    quoteReply = async () => response(invalid);
    await submit(); await confirm();
    check(element('coupon-quote').hidden && !createPosts().length && !hasPayLink(), 'Invalid pricing data cannot produce a payable order');
  }
  quoteReply = async () => response({message:'raw coupon here'}, 400);
  await submit();
  check(element('coupon-message').textContent.includes('更换') && !element('coupon-message').textContent.includes('raw'), 'Invalid codes can be replaced without reflecting raw service errors');
  for (const status of [404,405]) {
    quoteReply = async () => response({}, status);
    await submit();
    check(element('coupon-message').textContent === unsupported && element('coupon-confirm').disabled &&
      !createPosts().length && ordinaryPosts === 0 && !hasPayLink(), 'Unsupported quote HTTP ' + status + ' explains optional manual full-price checkout without creating an order');
  }
  quoteReply = async () => response(quote);
  await submit();
  const quoteBody = posts.at(-1).body;
  check(JSON.stringify(quoteBody) === JSON.stringify({planId:plan.id,couponCode:'new-Code'}), 'Quote sends only the chosen plan and trimmed code (server normalizes case)');
  check(!element('coupon-quote').hidden && !element('coupon-confirm').disabled &&
    ['quote-credits','quote-original','quote-discount','quote-total'].map(id=>element(id).textContent).join('|') === '50|¥10.00|−¥2.50|¥7.50',
    'Approved server quote visibly contains credits, retail, savings, and payable amount');
  check(!createPosts().length && !hasPayLink(), 'Computing the discounted price never creates an order');
  element('coupon-cancel').events.click();
  check(element('coupon-panel').hidden && !element('coupon-code').value && element('coupon-confirm').disabled && !!planButton('按原价创建订单'), 'Abandoning a quote leaves ordinary checkout accessible with a cleared draft');

  open(); input('new-Code'); await submit();
  orderReply = async () => { throw new Error('simulated network loss'); };
  await confirm();
  check(!hasPayLink() && element('checkout-message').textContent.includes('复用') && planButton('使用优惠码').disabled, 'Unconfirmed creation cannot open payment or change uncertain request terms');
  orderReply = async () => { orders = [order]; return response(order); };
  await planButton('重试创建订单').events.click();
  const attempts = createPosts();
  check(attempts.length === 2 && JSON.stringify(attempts[0]) === JSON.stringify(attempts[1]) && attempts[0].body.requestId.length === 36,
    'Lost discounted order response retries the original request ID, endpoint and complete body');
  check(attempts[0].body.couponCode === 'new-Code' && attempts[0].body.expectedAmountFen === 750 && attempts[0].body.planId === plan.id
    && Object.keys(attempts[0].body).length === 4 && hasPayLink(), 'Explicit confirmation binds expected total and opens a payment link only for a matching server order');
  api.renderOrders([{...order,status:'paid'}]);
  const history = walk(element('orders-body')).map(e => e.textContent);
  check(history.includes('实付 ¥7.50') && history.includes('原价 ¥10.00 · 已减 ¥2.50'), 'Paid history identifies the charged amount separately from the original price');
  api.renderOrders([{id:order.id, status:'paid', credits:50, amountFen:1000, createdAt:order.createdAt}]);
  check(walk(element('orders-body')).some(e=>e.textContent === '实付 ¥10.00'), 'Older non-discount order history displays its actual amount without inventing a discount');

  orders = []; await setAccount(); open(); input('new-Code'); await submit();
  orderReply = async () => response({...order,amountFen:1000,discountFen:0});
  await confirm();
  check(!hasPayLink() && element('coupon-confirm').disabled && element('coupon-message').textContent.includes('不符'), 'Unexpected repricing suppresses payment and forces a fresh quote');
  api.renderOrders([order]);
  check(!walk(element('orders-body')).some(e=>e.tagName==='a'), 'A known mismatched order also has no pay link in the current session history');
  for (const status of [404,405]) {
    await submit(); orderReply = async () => response({}, status); await confirm();
    check(element('coupon-message').textContent === unsupported && !hasPayLink() && element('coupon-confirm').disabled &&
      ordinaryPosts === 0, 'Unsupported coupon order HTTP ' + status + ' explains manual full-price checkout and never opens payment or falls back');
  }

  quoteReply = () => new Promise(resolve=>{ release=resolve; });
  const oldSession = submit(); api.clearSession(); api.setSession('z'.repeat(43));
  release(response(quote)); await oldSession;
  check(element('coupon-panel').hidden && !element('coupon-code').value && element('coupon-confirm').disabled && !hasPayLink(), 'Logout clears the code and a previous session reply cannot revive checkout');
  const markup = fs.readFileSync(path.join(__dirname,'..','website','account.html'),'utf8');
  check(markup.includes('for="coupon-code"') && /id="coupon-message"[^>]*role="status"/.test(markup)
    && markup.includes('action="/v1/redemptions"') && markup.includes('action="/v1/checkout/quote"'), 'Coupon pricing has an accessible, separate form from credit redemption');
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
  for (const invalid of ['', ' \t ', 'x'.repeat(81)]) {
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

  for (const custom of ['SampleEnglish','中文测试口令']) {
    let submittedCustom;
    transport = async (path, options) => {
      if (path !== '/v1/redemptions') return read(path);
      submittedCustom = JSON.parse(options.body); return response(reply);
    };
    element('redemption-code').value = ' ' + custom + ' '; await api.redeemCredits();
    check(JSON.stringify(submittedCustom) === JSON.stringify({code:custom}) && !element('redemption-code').value,
      'Short English and Chinese custom phrases reach the same protected redemption endpoint unchanged and clear on success');
  }

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
  const input = html.match(/<input id="redemption-code"[^>]*>/)[0];
  check(input.includes('autocomplete="off"') && input.includes('maxlength="80"') && !/minlength|pattern=/.test(input), 'Web form permits short Unicode redemption phrases and avoids autofill');
  check(html.includes('一次性码只有一个账号能领，共享码每个账号都可领一次'), 'Account guidance distinguishes globally single-use from per-account claims');
}
main().catch(error => { console.error(error.message); process.exitCode = 1; });
