// Run the public page script against the real HTML IDs and mocked browser events.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const website = path.join(__dirname, '..', 'website');
const html = fs.readFileSync(path.join(website, 'status.html'), 'utf8');
const script = fs.readFileSync(path.join(website, 'status.js'), 'utf8');

class EventTargetStub {
  events = {};
  addEventListener(name, handler) { this.events[name] = handler; }
  dispatch(name) { return this.events[name](); }
}
const elements = new Map([...html.matchAll(/\bid="([^"]+)"/g)].map(([, id]) =>
  [id, Object.assign(new EventTargetStub(), { dataset: {}, textContent: '' })]));
const el = id => { assert.ok(elements.has(id), 'Existing HTML element: ' + id); return elements.get(id); };
el('auto-refresh').checked = true;
const document = Object.assign(new EventTargetStub(), { hidden: false, getElementById: el });
const window = new EventTargetStub();
const navigator = { onLine: true };
const timers = new Map();
let nextId = 1;
function schedule(fn, delay) { timers.set(nextId, { fn, delay }); return nextId++; }
function fire(delay) {
  const matches = [...timers].filter(([, timer]) => timer.delay === delay);
  assert.equal(matches.length, 1, 'Exactly one timer at ' + delay);
  const [id, { fn }] = matches[0];
  timers.delete(id);
  return fn();
}
const sample = {
  status: 'operational', checkedAt: '2026-11-05T08:45:31.1234567+00:00', uptimeSeconds: 91400,
  storage: 'readable', translation: 'configured', payments: 'enabled',
  queue: { running: 2, concurrency: 2, waiting: 3, capacity: 16 }
};
const json = (data, status = 200) => ({ status, json: async () => data });
let transport = async () => json(sample);
const requests = [];
function fetch(endpoint, options) {
  requests.push(endpoint);
  assert.ok(['/v1/status', '/v1/plans'].includes(endpoint), 'Only read public endpoints');
  assert.equal(options.method ?? 'GET', 'GET');
  assert.equal(options.credentials, 'omit');
  assert.equal(options.cache, 'no-store');
  assert.equal(options.mode, 'same-origin');
  assert.equal(options.redirect, 'error');
  return transport(options.signal, endpoint);
}
const flush = () => new Promise(resolve => setImmediate(resolve));
const refresh = () => el('refresh-status').dispatch('click');

async function main() {
  for (const filename of ['index.html', 'account.html']) {
    assert.match(fs.readFileSync(path.join(website, filename), 'utf8'), /<a\b[^>]*\bhref="\/status\.html"[^>]*>/);
  }
  vm.runInNewContext(script, { document, window, navigator, fetch, AbortController, setTimeout: schedule, clearTimeout: id => timers.delete(id) });
  await flush();
  assert.equal(el('overview-title').textContent, '后台运行正常');
  assert.equal(el('uptime').textContent, '1 天 1 小时');
  assert.equal(el('translation-state').textContent, '已配置');
  assert.equal(el('payments-state').textContent, '已开启');
  assert.equal(el('waiting').textContent, '3');
  assert.equal(el('running-meter').value, 2);
  assert.equal(el('waiting-meter').max, 16);
  assert.equal(el('checked-at').dateTime, '2026-11-05T08:45:31.123Z');
  assert.equal(el('refresh-status').disabled, false);

  transport = async () => json({ ...sample, status: 'degraded', storage: 'unavailable', translation: 'stopping' }, 503);
  await fire(30_000);
  assert.equal(el('overview-title').textContent, '部分服务异常');
  assert.equal(el('api-state').textContent, '可访问');
  assert.equal(el('storage-state').textContent, '不可读取');
  assert.equal(el('translation-state').textContent, '正在停止');
  for (const [value, translation, payments] of [['disabled', '未启用', '未开启'], ['simulated', '模拟模式', '模拟模式']]) {
    transport = async () => json({ ...sample, translation: value, payments: value });
    await refresh();
    assert.equal(el('translation-state').textContent, translation);
    assert.equal(el('payments-state').textContent, payments);
  }
  for (const invalid of [json(sample, 502), json({ ...sample, translation: '__proto__' }), json({ ...sample, checkedAt: 'yesterday' }),
    json({ ...sample, queue: { ...sample.queue, waiting: null } }), { status: 503, json: async () => { throw new SyntaxError('HTML'); } }]) {
    const start = requests.length;
    transport = async () => invalid;
    await refresh();
    assert.deepEqual(requests.slice(start), ['/v1/status']);
    assert.equal(el('overview-title').textContent, '当前状态未知');
    assert.equal(el('api-state').textContent, '未知');
    assert.equal(el('waiting').textContent, '—');
    assert.equal(el('waiting-meter').hidden, true);
    assert.equal(el('checked-at').dateTime, '2026-11-05T08:45:31.123Z');
  }

  const missing = { status: 404, json: () => { throw new Error('A missing route has no status document'); } };
  for (const plans of [[], [{ id: 'example', name: '测试套餐', amountFen: 500, credits: 200 }]]) {
    const start = requests.length;
    transport = async (_, endpoint) => endpoint === '/v1/status' ? missing : json(plans);
    await refresh();
    assert.deepEqual(requests.slice(start), ['/v1/status', '/v1/plans']);
    assert.equal(el('overview-title').textContent, '套餐接口可访问');
    assert.equal(el('api-state').textContent, '可访问');
    for (const id of ['storage-state', 'translation-state', 'payments-state', 'queue-note', 'uptime']) {
      assert.equal(el(id).textContent, '未提供');
    }
    assert.equal(el('queue-note').hidden, false);
    assert.equal(el('queue-stats').hidden, true);
    assert.equal(el('running').textContent, '—');
    assert.ok(Math.abs(Date.now() - Date.parse(el('checked-at').dateTime)) < 5000);
  }
  const lastReachable = el('checked-at').dateTime;
  for (const badPlans of [json([], 503), json({ error: 'unavailable' }), { status: 200, json: () => { throw new SyntaxError('HTML'); } }]) {
    transport = async (_, endpoint) => endpoint === '/v1/status' ? missing : badPlans;
    await refresh();
    assert.equal(el('overview-title').textContent, '当前状态未知');
    assert.equal(el('api-state').textContent, '未知');
    assert.equal(el('checked-at').dateTime, lastReachable);
  }
  transport = async () => json(sample);
  await refresh();
  assert.equal(el('queue-stats').hidden, false);
  assert.equal(el('queue-note').hidden, true);
  assert.equal(el('waiting').textContent, '3');

  transport = signal => new Promise((_resolve, reject) => signal.addEventListener('abort', () => reject(new Error('timeout'))));
  const timeoutRead = refresh();
  const readCount = requests.length;
  await refresh();
  assert.equal(requests.length, readCount, 'Overlapping requests coalesce');
  fire(8_000);
  await timeoutRead;
  assert.match(el('overview-detail').textContent, /超时/);
  assert.equal(el('refresh-status').disabled, false);

  const hangingRead = transport;
  transport = (signal, endpoint) => endpoint === '/v1/status' ? missing : hangingRead(signal);
  const fallbackTimeout = refresh();
  await flush();
  fire(8_000);
  await fallbackTimeout;
  assert.equal(el('api-state').textContent, '未知', 'A timed out plans probe cannot confirm availability');
  assert.match(el('overview-detail').textContent, /超时/);

  let finishOld;
  transport = (_, endpoint) => endpoint === '/v1/status' ? missing : new Promise(resolve => { finishOld = () => resolve(json([])); });
  const oldRead = refresh();
  await flush();
  document.hidden = true;
  document.dispatch('visibilitychange');
  assert.equal(el('overview-title').textContent, '已暂停检测');
  assert.equal(el('refresh-status').disabled, true);
  finishOld();
  await oldRead;
  assert.equal(el('overview-title').textContent, '已暂停检测', 'Hidden page ignores a late response');
  assert.equal(timers.size, 0);
  transport = async () => json(sample);
  document.hidden = false;
  document.dispatch('visibilitychange');
  await flush();
  assert.equal(el('overview-title').textContent, '后台运行正常');
  navigator.onLine = false;
  window.dispatch('offline');
  assert.equal(el('overview-title').textContent, '设备已离线');
  assert.equal(el('storage-state').textContent, '未知');
  assert.equal(timers.size, 0);
  navigator.onLine = true;
  window.dispatch('online');
  await flush();
  assert.equal(el('refresh-status').disabled, false);
  el('auto-refresh').checked = false;
  el('auto-refresh').dispatch('change');
  assert.equal(timers.size, 0);
  await refresh();
  assert.equal(el('overview-title').textContent, '后台运行正常');
  assert.equal(timers.size, 0, 'Manual refresh respects disabled polling');
  console.log('PASS website status snapshots, public plans fallback, error handling, timeouts, visibility and polling');
}
main().catch(error => { console.error(error); process.exitCode = 1; });
