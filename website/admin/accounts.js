'use strict';

window.createAccountManager = function ({api, el, money, when, summary}) {
  const $ = id => document.getElementById(id);
  const labels = {
    pending:'待核验', paid:'已到账', refund_pending:'退款处理中', refunded:'已退款',
    recharge:'充值到账', redemption:'兑换码入账', translation:'翻译扣除', refund:'退款扣回',
    refund_hold:'退款预留', refund_release:'解除退款预留', admin_adjustment:'管理员调整',
    adjust:'调整积分', suspend:'停用账号', restore:'恢复账号'
  };
  const errors = {
    invalid_account_action:'请检查操作类型、数量和原因。',
    insufficient_available_credits:'可用积分不足。请关闭窗口并刷新明细后再核对。',
    account_state_conflict:'账号状态已经改变。请关闭窗口并刷新明细。',
    credit_limit:'调整后的余额超过账号积分上限。',
    account_action_conflict:'本次请求编号已有记录，请先核对管理操作记录。'
  };
  const pending = new Map();
  let active = false, view = 0, auditRequest = 0;
  let query = '', listPage = 1, orderPage = 1, ledgerPage = 1, auditPage = 1;
  let selectedId = '', account = null, trigger = null, operation = null;
  function number(n) { return Number.isSafeInteger(n) ? n.toLocaleString('zh-CN') : '—'; }
  function signed(n) { return n > 0 ? '+' + number(n) : number(n); }
  function status(row) { return row.disabled ? '已停用' : '正常'; }
  function isAccount(row) {
    return /^[a-f0-9]{32}$/.test(row?.id) && typeof row.username === 'string' && typeof row.disabled === 'boolean';
  }
  function clear(...ids) { for (const id of ids) $(id).replaceChildren(); }
  function clearDetail() {
    account = null;
    $('accountName').textContent = '账号明细'; $('accountId').textContent = ''; $('detailStatus').textContent = '';
    $('accountTools').hidden = true;
    clear('accountMetrics','accountOrders','accountLedger','accountAudit','orderPager','ledgerPager','auditPager');
  }
  function closeAction() {
    if (operation?.busy && pending.get(operation.id) === operation) operation.uncertain = true;
    if ($('actionDialog').open) $('actionDialog').close();
    operation = null;
    $('actionForm').reset(); $('actionReason').value = ''; $('adjustAmount').value = '';
    $('actionTarget').textContent = ''; $('actionTitle').textContent = ''; $('actionMessage').textContent = '';
    clear('actionReview');
  }
  function leave() {
    active = false; view++; auditRequest++; selectedId = ''; trigger = null;
    closeAction(); clearDetail(); clear('accountList','accountPager'); $('directoryStatus').textContent = '';
  }
  function reset() {
    leave(); pending.clear(); query = ''; listPage = 1;
    $('accountSearch').value = ''; $('resetSearch').hidden = true;
  }
  function enter(all = false) {
    active = true;
    if (all) { query = ''; listPage = 1; $('accountSearch').value = ''; }
    return directory(listPage);
  }
  function line(title, detail, meta, secondary) {
    const row = el('article'); row.className = 'account-record';
    const info = el('div'); info.append(el('h3',title),el('p',detail)); row.append(info);
    const side = el('div'); side.className = 'account-meta'; side.append(el('strong',meta),el('p',secondary)); row.append(side);
    return row;
  }
  function pager(id, data, navigate) {
    const holder = $(id); holder.replaceChildren();
    const pages = Math.ceil(data.total / data.pageSize);
    if (pages <= 1) return;
    function button(label, n) {
      const b = el('button',label); b.type = 'button'; b.className = 'light';
      b.disabled = n < 1 || n > pages; b.addEventListener('click', () => navigate(n)); return b;
    }
    holder.append(button('上一页',data.page-1),el('span','第 ' + data.page + ' / ' + pages + ' 页'),button('下一页',data.page+1));
  }
  async function directory(page) {
    if (!active) return;
    selectedId = ''; auditRequest++; closeAction(); clearDetail();
    const request = ++view; listPage = page;
    $('accountDetail').hidden = true; $('accountDirectory').hidden = false;
    $('resetSearch').hidden = !query && !$('accountSearch').value;
    $('directoryStatus').textContent = '正在读取账号…'; clear('accountList','accountPager');
    try {
      const data = await api('accounts?page=' + page + '&pageSize=20' + (query ? '&q=' + encodeURIComponent(query) : ''));
      if (view !== request) return;
      if (!Array.isArray(data.items) || !data.items.every(isAccount)) throw Error();
      $('directoryStatus').textContent = query ? (data.total ? '找到 ' + number(data.total) + ' 个账号' : '没有找到匹配的账号。')
        : data.total ? '共 ' + number(data.total) + ' 个注册账号' : '还没有注册账号。';
      for (const item of data.items) {
        const row = line(item.username, item.id, '可用 ' + number(item.available) + ' 积分', status(item));
        const open = el('button','查看明细'); open.type = 'button'; open.className = 'light';
        open.setAttribute('aria-label','查看 ' + item.username + ' 的账号明细');
        open.addEventListener('click', () => { trigger = item.id; return select(item.id); });
        row.append(open); $('accountList').append(row);
        if (trigger === item.id) { open.focus(); trigger = null; }
      }
      listPage = data.page; pager('accountPager',data,directory);
    } catch {
      if (view === request) $('directoryStatus').textContent = '账号读取失败，请点击「刷新列表」。';
    }
  }
  function select(id) {
    selectedId = id; orderPage = ledgerPage = auditPage = 1;
    $('accountDirectory').hidden = true; $('accountDetail').hidden = false;
    const result = detail(); $('accountName').focus(); return result;
  }
  function metric(label,value) {
    const group = el('div'); group.append(el('dt',label),el('dd',value)); return group;
  }
  function updateTools() {
    if (!account) return;
    const retry = pending.has(selectedId);
    $('adjustCredits').disabled = retry;
    $('toggleAccount').disabled = retry; $('toggleAccount').textContent = account.disabled ? '恢复账号' : '停用账号';
    $('retryAction').hidden = !retry;
    $('accountTools').hidden = false;
  }
  function records(id,items,empty,render) {
    const node = $(id); node.replaceChildren();
    if (!items.length) node.append(el('p',empty));
    for (const item of items) node.append(render(item));
  }
  function renderDetail(data) {
    account = data.account; $('accountName').textContent = account.username;
    $('accountId').textContent = '账号 ID ' + account.id;
    $('accountMetrics').replaceChildren(metric('可用积分',number(account.available)),metric('总积分',number(account.balance)),
      metric('预留积分',number(account.reserved)),metric('账号状态',status(account)));
    updateTools(); $('detailStatus').textContent = '';
    records('accountOrders',data.orders.items,'暂无充值订单。',o => line(o.name,'订单 ' + o.id + ' · ' + when(o.createdAt),
      money(o.amountFen),number(o.credits) + ' 积分 · ' + (labels[o.status] || '待确认')));
    records('accountLedger',data.ledger.items,'暂无积分流水。',l => line(labels[l.kind] || '积分变动',when(l.createdAt),
      signed(l.amount) + ' 积分','变动后余额 ' + number(l.balance)));
    orderPage = data.orders.page; ledgerPage = data.ledger.page;
    pager('orderPager',data.orders,n => { orderPage = n; return detail(); });
    pager('ledgerPager',data.ledger,n => { ledgerPage = n; return detail(); });
  }
  async function detail() {
    if (!active || !selectedId) return;
    const request = ++view, id = selectedId;
    clearDetail(); $('detailStatus').textContent = '正在读取账号明细…';
    const audit = loadAudit(auditPage);
    try {
      const data = await api('accounts/' + id + '?orderPage=' + orderPage + '&ledgerPage=' + ledgerPage + '&pageSize=10');
      if (view !== request) return;
      if (!isAccount(data?.account) || data.account.id !== id || !Array.isArray(data.orders?.items) || !Array.isArray(data.ledger?.items)) throw Error();
      renderDetail(data);
    } catch (err) {
      if (view === request) $('detailStatus').textContent = err.status === 404 ? '找不到这个账号。' : '账号明细读取失败，请刷新重试。';
    }
    await audit;
  }
  async function loadAudit(page) {
    const request = ++auditRequest, id = selectedId;
    clear('auditPager'); $('accountAudit').replaceChildren(el('p','正在读取操作记录…'));
    try {
      const data = await api('accounts/' + id + '/actions?page=' + page + '&pageSize=10');
      if (request !== auditRequest) return;
      records('accountAudit',data.items,'暂无管理操作记录。',a => line(labels[a.action] || '账号操作',a.reason,
        a.action === 'adjust' ? signed(a.delta) + ' 积分' : '余额 ' + number(a.balanceAfter),when(a.createdAt)));
      auditPage = data.page; pager('auditPager',data,loadAudit);
    } catch {
      if (request === auditRequest) $('accountAudit').replaceChildren(el('p','管理操作记录读取失败，请点击「刷新明细」。'));
    }
  }
  function reviewAction(op) {
    $('actionFields').hidden = true;
    const rows = [metric('操作',labels[op.action])];
    if (op.action === 'adjust') {
      rows.push(metric('积分变动',signed(op.delta)),metric('操作前可用',number(op.available)),metric('预计可用',number(op.available + op.delta)));
    } else {
      rows.push(metric('登录与使用',op.action === 'suspend' ? '停用后该账号将无法登录或发起新请求。' : '恢复后用户可以重新登录。'));
    }
    rows.push(metric('操作原因',op.reason)); $('actionReview').replaceChildren(...rows);
    $('actionSubmit').textContent = '确认' + labels[op.action];
    $('actionMessage').textContent = op.uncertain ? '上次提交的结果待确认。再次提交会使用原请求编号。' : '';
  }
  function openAction(action) {
    if (!active || !account || $('actionDialog').open) return;
    const prior = pending.get(account.id);
    if (prior && (!prior.uncertain || prior.busy)) return;
    operation = prior || {id:account.id,action,available:account.available};
    $('actionTitle').textContent = labels[operation.action]; $('actionTarget').textContent = account.username + ' · ' + account.id;
    $('actionFields').hidden = false; $('adjustFields').hidden = action !== 'adjust';
    $('adjustDirection').value = 'add'; $('actionSubmit').textContent = '核对操作'; $('actionMessage').textContent = '';
    $('actionClose').disabled = $('actionSubmit').disabled = false;
    if (prior) reviewAction(operation);
    $('actionDialog').showModal();
    (prior ? $('actionSubmit') : action === 'adjust' ? $('adjustAmount') : $('actionReason')).focus();
  }
  function prepare(op) {
    const reason = $('actionReason').value.trim();
    if (!reason || reason.length > 200 || /[\x00-\x1f\x7f-\x9f]/.test(reason)) throw Error('请输入 1–200 个字的操作原因，写在一行内。');
    let delta = 0;
    if (op.action === 'adjust') {
      const raw = $('adjustAmount').value;
      if (!/^[1-9]\d{0,6}$/.test(raw) || Number(raw) > 1000000) throw Error('单次调整数量须为 1 至 1,000,000 的整数。');
      delta = Number(raw) * ($('adjustDirection').value === 'subtract' ? -1 : 1);
      if (delta < -op.available) throw Error('扣减数量超过当前可用积分。');
    }
    Object.assign(op,{delta,reason,key:crypto.randomUUID()});
    reviewAction(op);
  }
  async function submitAction(event) {
    event.preventDefault(); const op = operation;
    if (!op || op.busy || op.id !== selectedId || !active) return;
    if (!op.key) {
      try { prepare(op); } catch (err) { $('actionMessage').textContent = err.message; }
      return;
    }
    op.busy = true; $('actionSubmit').disabled = $('actionClose').disabled = true;
    $('actionMessage').textContent = '正在提交…'; pending.set(op.id,op);
    try {
      const body = {action:op.action,delta:op.delta,reason:op.reason};
      const result = await api('accounts/' + op.id + '/actions','POST',body,op.key);
      if (operation !== op) return;
      if (result?.action !== op.action || result.delta !== op.delta || typeof result.alreadyApplied !== 'boolean') throw Error();
      pending.delete(op.id); closeAction();
      orderPage = ledgerPage = auditPage = 1;
      await detail();
      if (active && account?.id === op.id) $('detailStatus').textContent = result.alreadyApplied ? '已核对，上次操作已生效。' : '操作已生效。';
    } catch (err) {
      if (operation !== op) return;
      // The server checks for an earlier committed request before returning these business conflicts.
      const rejected = ['insufficient_available_credits','account_state_conflict','credit_limit'].includes(err.code);
      const definite = rejected || (!op.uncertain && [400,403,404,409].includes(err.status));
      if (definite) {
        pending.delete(op.id); delete op.key; op.uncertain = false;
        $('actionFields').hidden = false; $('actionSubmit').textContent = '核对操作'; clear('actionReview');
        $('actionMessage').textContent = errors[err.code] || '操作未执行，请核对后重试。';
      } else {
        op.uncertain = true;
        $('actionMessage').textContent = err.status === 429 ? '请求较频繁，请稍后用原请求重试。' : '提交结果待确认。请用下方按钮重试，同一请求只会生效一次。';
      }
      updateTools();
    } finally {
      op.busy = false;
      if (operation === op) $('actionSubmit').disabled = $('actionClose').disabled = false;
    }
  }
  $('accountSearch').addEventListener('input',() => { $('resetSearch').hidden = !query && !$('accountSearch').value; });
  $('accountSearchForm').addEventListener('submit',event => { event.preventDefault(); query = $('accountSearch').value.trim(); return directory(1); });
  $('resetSearch').addEventListener('click',() => { $('accountSearch').value = query = ''; return directory(1); });
  $('refreshAccounts').addEventListener('click',() => active && Promise.all([directory(listPage),summary()]));
  $('backAccounts').addEventListener('click',() => directory(listPage));
  $('refreshDetail').addEventListener('click',() => detail());
  $('adjustCredits').addEventListener('click',() => openAction('adjust'));
  $('toggleAccount').addEventListener('click',() => account && openAction(account.disabled ? 'restore' : 'suspend'));
  $('retryAction').addEventListener('click',() => pending.has(selectedId) && openAction(pending.get(selectedId).action));
  $('actionForm').addEventListener('submit',submitAction);
  $('actionClose').addEventListener('click',() => { if (!operation?.busy) closeAction(); });
  $('actionDialog').addEventListener('cancel',event => { event.preventDefault(); if (!operation?.busy) closeAction(); });
  return {enter,leave,reset};
};
