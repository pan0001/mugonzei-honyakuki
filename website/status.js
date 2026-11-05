(() => {
  'use strict';

  const byId = id => document.getElementById(id);
  const refreshButton = byId('refresh-status');
  const autoRefresh = byId('auto-refresh');
  const localTime = new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false
  });
  const storageStates = { readable: ['可读取', 'good'], unavailable: ['不可读取', 'bad'] };
  const translationStates = {
    configured: ['已配置', 'config'], disabled: ['未启用', 'muted'],
    simulated: ['模拟模式', 'config'], stopping: ['正在停止', 'bad']
  };
  const paymentStates = { enabled: ['已开启', 'config'], disabled: ['未开启', 'muted'], simulated: ['模拟模式', 'config'] };
  let pollTimer;
  let activeRequest;

  function setText(id, value) {
    if (byId(id).textContent !== value) byId(id).textContent = value;
  }

  function badge(id, [label, tone]) {
    setText(id, label);
    byId(id).dataset.tone = tone;
  }

  function overview(title, detail, tone) {
    setText('overview-title', title);
    setText('overview-detail', detail);
    byId('overall-light').dataset.tone = tone;
  }

  function clearDetails(label) {
    byId('timing-values').hidden = true;
    setText('timing-note', label);
    for (const id of ['storage-state', 'translation-state', 'payments-state']) badge(id, [label, 'muted']);
    setText('uptime', label);
    setText('queue-note', label);
    byId('queue-note').hidden = false;
    byId('queue-stats').hidden = true;
    for (const id of ['running', 'concurrency', 'waiting', 'capacity']) setText(id, '—');
    byId('running-meter').hidden = true;
    byId('waiting-meter').hidden = true;
  }

  function showUnknown(title, detail) {
    overview(title, detail, 'muted');
    badge('api-state', ['未知', 'muted']);
    clearDetails('未知');
    // Keep the timestamp of the last valid sample, even if this read failed.
  }

  function showCheckedAt(timestamp) {
    setText('checked-at', localTime.format(timestamp));
    byId('checked-at').dateTime = timestamp.toISOString();
  }

  function showPlansReachable() {
    overview('套餐接口可访问', '浏览器已读取公开套餐列表。服务器暂未提供详细状态。', 'muted');
    badge('api-state', ['可访问', 'good']);
    clearDetails('未提供');
    showCheckedAt(new Date());
  }

  function isCount(value) {
    return Number.isSafeInteger(value) && value >= 0;
  }

  function knownState(states, value) {
    return typeof value === 'string' && Object.hasOwn(states, value);
  }

  function validSample(sample, httpStatus) {
    return sample && ['operational', 'degraded'].includes(sample.status)
      && (httpStatus === 200 || (httpStatus === 503 && sample.status === 'degraded'))
      && typeof sample.checkedAt === 'string' && /^\d{4}-\d\d-\d\dT.*(?:Z|[+-]\d\d:\d\d)$/.test(sample.checkedAt)
      && Number.isFinite(Date.parse(sample.checkedAt)) && isCount(sample.uptimeSeconds)
      && knownState(storageStates, sample.storage) && knownState(translationStates, sample.translation)
      && knownState(paymentStates, sample.payments) && sample.queue
      && ['running', 'concurrency', 'waiting', 'capacity'].every(key => isCount(sample.queue[key]));
  }

  function uptime(seconds) {
    const minutes = Math.floor(seconds / 60);
    const hours = Math.floor(minutes / 60);
    if (hours >= 24) return Math.floor(hours / 24) + ' 天 ' + hours % 24 + ' 小时';
    if (hours) return hours + ' 小时 ' + minutes % 60 + ' 分钟';
    if (minutes) return minutes + ' 分钟';
    return seconds + ' 秒';
  }

  function showMeter(id, value, max) {
    const meter = byId(id);
    meter.hidden = max === 0;
    meter.max = max || 1;
    meter.value = value;
  }

  function showSample(sample) {
    const timing = sample.timings;
    const fields = [['queue-ms', 'queueMs'], ['model-ms', 'modelMs'], ['settlement-ms', 'settlementMs']];
    const hasTimings = timing && isCount(timing.samples) && timing.samples > 0 && timing.samples <= 32 &&
      fields.every(([, key]) => Number.isFinite(timing[key]) && timing[key] >= 0);
    byId('timing-values').hidden = !hasTimings;
    setText('timing-note', hasTimings ? '最近 ' + timing.samples + ' 笔已完成任务的平均耗时'
      : timing?.samples === 0 ? '服务启动后还没有已完成的翻译。' : '服务器暂未提供耗时。');
    if (hasTimings) for (const [id, key] of fields) {
      const ms = timing[key];
      setText(id, ms < 1000 ? (ms < 1 ? '< 1' : String(Math.round(ms))) + ' 毫秒' : (ms / 1000).toFixed(1) + ' 秒');
    }
    if (sample.status === 'operational') {
      overview('后台运行正常', '状态接口可访问，账户数据库可正常读取。', 'good');
    } else {
      overview('部分服务异常', '后台已报告异常。请查看下方服务明细。', 'bad');
    }
    showCheckedAt(new Date(sample.checkedAt));
    setText('uptime', uptime(sample.uptimeSeconds));
    badge('api-state', ['可访问', 'good']);
    badge('storage-state', storageStates[sample.storage]);
    badge('translation-state', translationStates[sample.translation]);
    badge('payments-state', paymentStates[sample.payments]);
    const queue = sample.queue;
    byId('queue-note').hidden = true;
    byId('queue-stats').hidden = false;
    for (const key of ['running', 'concurrency', 'waiting', 'capacity']) setText(key, String(queue[key]));
    showMeter('running-meter', queue.running, queue.concurrency);
    showMeter('waiting-meter', queue.waiting, queue.capacity);
  }

  function readPublicApi(path, signal) {
    return fetch(path, {
      credentials: 'omit', cache: 'no-store', mode: 'same-origin', redirect: 'error',
      headers: { Accept: 'application/json' }, signal
    });
  }

  function scheduleRefresh() {
    clearTimeout(pollTimer);
    if (autoRefresh.checked && !document.hidden && navigator.onLine && !activeRequest) {
      pollTimer = setTimeout(refresh, 30_000);
    }
  }

  async function refresh() {
    if (activeRequest || document.hidden || !navigator.onLine) return;
    clearTimeout(pollTimer);
    const controller = new AbortController();
    activeRequest = controller;
    refreshButton.disabled = true;
    setText('refresh-label', '正在刷新…');
    const deadline = setTimeout(() => controller.abort(), 8_000);
    try {
      const response = await readPublicApi('/v1/status', controller.signal);
      if (activeRequest !== controller) return;
      if (response.status === 404) {
        const plans = await readPublicApi('/v1/plans', controller.signal);
        if (plans.status !== 200 || !Array.isArray(await plans.json())) throw new Error('Invalid plans response');
        if (activeRequest === controller) showPlansReachable();
        return;
      }
      const sample = await response.json();
      if (!validSample(sample, response.status)) throw new Error('Invalid status response');
      if (activeRequest === controller) showSample(sample);
    } catch {
      if (activeRequest === controller) {
        const message = controller.signal.aborted ? '读取状态超时。' : '未能获取有效的状态报告。';
        showUnknown('当前状态未知', message + '请稍后刷新重试。');
      }
    } finally {
      clearTimeout(deadline);
      if (activeRequest === controller) {
        activeRequest = null;
        setText('refresh-label', '刷新状态');
        refreshButton.disabled = false;
        scheduleRefresh();
      }
    }
  }

  function updateConnection() {
    if (!document.hidden && navigator.onLine) {
      overview('正在读取状态', '正在向服务器请求最新检测结果。', 'muted');
      refresh();
      return;
    }
    clearTimeout(pollTimer);
    activeRequest?.abort();
    activeRequest = null;
    refreshButton.disabled = true;
    setText('refresh-label', '刷新状态');
    if (!navigator.onLine) {
      showUnknown('设备已离线', '恢复网络连接后将重新读取服务器状态。');
    } else {
      showUnknown('已暂停检测', '返回此页面后将重新读取服务器状态。');
    }
  }

  refreshButton.addEventListener('click', refresh);
  autoRefresh.addEventListener('change', scheduleRefresh);
  document.addEventListener('visibilitychange', updateConnection);
  window.addEventListener('online', updateConnection);
  window.addEventListener('offline', updateConnection);
  byId('auto-refresh-label').hidden = false;
  updateConnection();
})();
