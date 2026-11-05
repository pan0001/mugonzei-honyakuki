'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const kinds = ['coupons', 'redemptions'];
  const keys = new Map();
  const latest = new Map();
  const date = new Intl.DateTimeFormat('zh-CN', {dateStyle:'short', timeStyle:'short'});
  let epoch = 0, loggedIn = false, authBusy = false, writing = false;
  let plans = [], review = null;
  const accounts = window.createAccountManager({api,el,money,when,summary:() => refresh('summary')});

  function notice(text = '', error = false) {
    $('notice').textContent = text;
    $('notice').dataset.error = String(error);
  }
  function el(tag, text) {
    const n = document.createElement(tag);
    if (text != null) n.textContent = text;
    return n;
  }
  function money(fen) { return Number.isSafeInteger(fen) ? '¥' + (fen / 100).toFixed(2) : '—'; }
  function when(value) { const time = Date.parse(value); return Number.isFinite(time) ? date.format(time) : '—'; }
  function clearIssued() { $('codes').value = ''; $('issued').hidden = true; }
  function closeReview() {
    if ($('review').open) $('review').close();
    review = null; writing = false;
    $('confirm').disabled = false; $('back').disabled = false;
  }
  function signOut(text = '') {
    epoch++; loggedIn = false; keys.clear(); latest.clear(); plans = [];
    accounts.reset();
    closeReview(); clearIssued();
    for (const id of ['couponForm','redemptionForm','loginForm']) $(id).reset();
    updateRedemptionCodeField();
    for (const id of ['plan','couponList','redemptionList','summary']) $(id).replaceChildren();
    for (const id of ['accounts','paidOrders','paidAmount']) $(id).textContent = '—';
    $('workspace').hidden = true; $('logout').hidden = true; $('login').hidden = false;
    $('signIn').disabled = authBusy; notice(text);
  }
  class Fault extends Error {
    constructor(status, code) { super('Admin request failed'); this.status = status; this.code = code; }
  }
  async function api(path, method = 'GET', body = undefined, key = '') {
    const stamp = epoch;
    const headers = {Accept:'application/json'};
    if (method !== 'GET') {
      headers['X-Mugonzei-Admin'] = '1'; headers['Content-Type'] = 'application/json';
      if (key) headers['Idempotency-Key'] = key;
    }
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 15000);
    try {
      const reply = await fetch('/admin/api/' + path, {method, headers, credentials:'same-origin', mode:'same-origin', redirect:'error',
        cache:'no-store', referrerPolicy:'same-origin', signal:controller.signal, body:body === undefined ? undefined : JSON.stringify(body)});
      let json = null;
      if (reply.status !== 204) {
        try { json = await reply.json(); } catch { if (reply.ok) throw new Fault(0, 'invalid_reply'); }
      }
      if (!reply.ok) {
        if (reply.status === 401 && loggedIn && stamp === epoch) signOut('会话已过期，请重新登录。');
        throw new Fault(reply.status, json?.error);
      }
      return json;
    } finally { clearTimeout(timer); }
  }
  function listUnavailable(kind) {
    const id = kind === 'coupons' ? 'couponList' : 'redemptionList';
    $(id).replaceChildren(el('p', '记录读取失败，请刷新重试。'));
  }
  function describePlan() {
    const p = plans.find(p => p.id === $('plan').value);
    $('planDetail').textContent = p ? p.credits + ' 积分，原价 ' + money(p.amountFen) : '暂无可用套餐。';
    $('reviewCoupon').disabled = !p;
  }
  function renderPlans(items) {
    const old = $('plan').value;
    plans = items.filter(p => typeof p?.id === 'string' && Number.isSafeInteger(p.amountFen) && Number.isSafeInteger(p.credits));
    $('plan').replaceChildren();
    for (const p of plans) {
      const option = el('option', p.name + ' · ' + money(p.amountFen)); option.value = p.id; $('plan').append(option);
    }
    if (plans.length) $('plan').value = plans.some(p => p.id === old) ? old : plans[0].id;
    describePlan();
  }
  const statuses = {active:'有效',expired:'已过期',redeemed:'已兑换',revoked:'已停用'};
  const modeLabels = {singleUse:'一次性码 · 仅一个账号可领', oncePerAccount:'共享码 · 每个账号可领一次'};
  function renderList(kind, items) {
    const coupon = kind === 'coupons';
    const list = $(coupon ? 'couponList' : 'redemptionList');
    list.replaceChildren();
    if (!items.length) { list.append(el('p', coupon ? '暂无优惠活动。' : '暂无兑换码记录。')); return; }
    for (const item of items) {
      const row = el('article'); row.className = 'record';
      const content = el('div');
      const state = coupon ? (item.revokedAt ? 'revoked' : Date.parse(item.expiresAt) <= Date.now() ? 'expired' : 'active') : item.status;
      const mode = item.mode ?? 'singleUse';
      const claims = Number.isSafeInteger(item.claimCount) && item.claimCount >= 0 ? item.claimCount : '—';
      const summary = coupon ? item.planId + ' · ' + money(item.amountFen) : item.credits + ' 积分' + (item.note ? ' · ' + item.note : '');
      content.append(el('h3', summary), el('p', (statuses[state] || '待确认') + ' · 有效至 ' + (item.expiresAt ? when(item.expiresAt) : '长期有效')),
        el('p', (coupon ? '活动 ID ' : '记录 ID ') + item.id));
      if (coupon) content.append(el('p', '爱发电折扣方案 ' + item.afdianPlanId));
      else content.append(el('p', (modeLabels[mode] || '领取方式待确认') + ' · 已领取账号 ' + claims));
      row.append(content);
      if (/^[a-f0-9]{32}$/i.test(item.id)) {
        const actions = el('div'); actions.className = 'actions';
        if (state === 'active') {
          const revoke = el('button', coupon ? '停用活动' : '撤销兑换码'); revoke.type = 'button'; revoke.className = 'light';
          const rows = [['记录',summary],['ID',item.id]];
          if (!coupon && mode === 'oncePerAccount') rows.push(['停用后','停止新账号领取，已到账积分保留。']);
          revoke.addEventListener('click', () => showReview({kind,id:item.id,action:'revoke'}, revoke.textContent, rows));
          actions.append(revoke);
        }
        if (!coupon && item.mode === 'singleUse' && ['active','redeemed'].includes(state)
          && !item.revokedAt && (!item.expiresAt || Date.parse(item.expiresAt) > Date.now())) {
          const share = el('button','转为共享码'); share.type = 'button'; share.className = 'light';
          share.addEventListener('click', () => showReview({kind,id:item.id,action:'share'}, '转为共享码', [
            ['记录',summary],['ID',item.id],['已领取账号',claims],['转换后','其他账号均可凭原口令各领一次。已领取账号保留记录，无法重复领取。']
          ]));
          actions.append(share);
        }
        if (actions.children.length) row.append(actions);
      }
      list.append(row);
    }
  }
  async function refresh(key) {
    const stamp = epoch, sequence = (latest.get(key) || 0) + 1;
    latest.set(key, sequence);
    try {
      const value = await api(key);
      if (epoch !== stamp || latest.get(key) !== sequence) return;
      if (key === 'summary') {
        $('accounts').textContent = Number.isSafeInteger(value?.accounts) ? value.accounts : '—';
        $('paidOrders').textContent = Number.isSafeInteger(value?.paidOrders) ? value.paidOrders : '—';
        $('paidAmount').textContent = money(value?.paidAmountFen); return;
      }
      if (!Array.isArray(value)) throw new Fault(0, 'invalid_reply');
      if (key === 'plans') renderPlans(value); else renderList(key, value);
    } catch {
      if (epoch !== stamp || latest.get(key) !== sequence) return;
      if (key === 'summary') { for (const id of ['accounts','paidOrders','paidAmount']) $(id).textContent = '—'; }
      else if (key === 'plans') { renderPlans([]); $('planDetail').textContent = '套餐读取失败，请点击「刷新活动」。'; }
      else listUnavailable(key);
    }
  }
  function chooseTab(kind, allAccounts = false) {
    if (!loggedIn) return;
    for (const [value,tab,page] of [['accounts','accountTab','accountPage'],['coupons','couponTab','couponPage'],['redemptions','redemptionTab','redemptionPage']]) {
      const selected = value === kind;
      $(page).hidden = !selected;
      if (selected) $(tab).setAttribute('aria-current','page'); else $(tab).removeAttribute('aria-current');
    }
    if (kind === 'accounts') return accounts.enter(allAccounts);
    accounts.leave(); return refresh(kind);
  }
  function signedIn() {
    epoch++; loggedIn = true; $('login').hidden = true; $('workspace').hidden = false; $('logout').hidden = false;
    notice(); $('accountTab').focus();
    return Promise.all([refresh('plans'), refresh('summary'), chooseTab('accounts')]);
  }
  async function session() {
    const stamp = epoch;
    try {
      const value = await api('session');
      if (stamp !== epoch) return;
      if (value?.authenticated === true) return signedIn();
      signOut();
    } catch (error) {
      if (stamp === epoch) signOut(error.status === 404 ? '此服务尚未启用管理后台。' : '会话检查失败，可重新登录。');
    }
  }
  $('loginForm').addEventListener('submit', async event => {
    event.preventDefault();
    if (authBusy || loggedIn || !$('loginForm').reportValidity()) return;
    const stamp = epoch;
    authBusy = true; $('signIn').disabled = true; notice('正在登录…');
    try {
      const pending = api('session','POST',{token:$('token').value});
      $('token').value = '';
      const value = await pending;
      if (stamp !== epoch) return;
      if (value?.authenticated !== true) throw new Fault(0,'invalid_reply');
      await signedIn();
    } catch (error) {
      if (stamp === epoch) notice(error.status === 429 ? '尝试次数较多，请稍后重试。'
        : error.status === 404 ? '此服务尚未启用管理后台。' : '登录失败，请核对管理口令或稍后重试。', true);
    } finally { authBusy = false; $('signIn').disabled = false; }
  });
  $('logout').addEventListener('click', async () => {
    if (authBusy) return;
    authBusy = true; $('logout').disabled = true;
    signOut('正在退出…'); const stamp = epoch;
    try { await api('session','DELETE',{}); if (stamp === epoch) notice('已退出管理。'); }
    catch (error) {
      if (stamp === epoch && error.status !== 401) {
        notice('本页已清空。服务器尚未确认退出，请重试「退出管理」。',true); $('logout').hidden = false;
      }
    } finally { authBusy = false; $('logout').disabled = false; $('signIn').disabled = false; }
  });
  function whole(id, min, max) {
    const raw = $(id).value;
    const n = Number(raw);
    if (!/^[0-9]+$/.test(raw) || !Number.isSafeInteger(n) || n < min || n > max) throw Error('请输入 ' + min + ' 至 ' + max + ' 之间的整数。');
    return n;
  }
  function couponBody() {
    const p = plans.find(p => p.id === $('plan').value);
    if (!p) throw Error('请先选择可售套餐。');
    const parts = /^([0-9]{1,9})(?:\.([0-9]{1,2}))?$/.exec($('price').value.trim());
    const amountFen = parts ? Number(parts[1]) * 100 + Number((parts[2] || '').padEnd(2,'0')) : 0;
    if (amountFen < 1 || amountFen >= p.amountFen) throw Error('折后价至少为 0.01 元且须低于原价，最多保留两位小数。');
    const afdianPlanId = $('providerPlan').value.trim().toLowerCase();
    if (!/^[0-9a-f]{32}$/.test(afdianPlanId) || plans.some(v => String(v.afdianPlanId).toLowerCase() === afdianPlanId))
      throw Error('请填写独立的、32 位十六进制的爱发电折扣方案 ID。');
    const customCode = $('customCode').value.trim();
    if (customCode && (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/i.test(customCode) || /^mu/i.test(customCode) || !/^[a-z0-9]{4,40}$/i.test(customCode.replaceAll('-',''))))
      throw Error('请检查自定义口令的格式。');
    return {planId:p.id, amountFen, afdianPlanId, days:whole('couponDays',1,365), ...(customCode ? {customCode} : {})};
  }
  function updateRedemptionCodeField() {
    const multiple = Number($('count').value) !== 1;
    const field = $('redemptionCustomCode');
    field.disabled = multiple;
    if (multiple) field.value = '';
  }
  function redemptionBody() {
    const note = $('note').value.trim();
    if (note.length > 120 || /[\x00-\x1f\x7f]/.test(note)) throw Error('备注最多 120 字，请移除控制字符。');
    const count = whole('count',1,50);
    const mode = $('redemptionMode').value;
    if (!Object.hasOwn(modeLabels, mode)) throw Error('请选择领取方式。');
    const customCode = count === 1 ? $('redemptionCustomCode').value.replace(/^ +| +$/g, '') : '';
    return {credits:whole('credits',1,1000000), count, days:whole('redemptionDays',1,365), note,
      ...(mode === 'oncePerAccount' ? {mode} : {}), ...(customCode ? {customCode} : {})};
  }
  function showReview(operation, title, rows) {
    if (!loggedIn || review) return;
    if (!$('issued').hidden) { notice('请先保存并关闭上一次生成的口令。'); $('issuedTitle').focus(); return; }
    review = operation;
    $('reviewTitle').textContent = title; $('confirm').textContent = operation.action === 'share' ? '确认转为共享码' : operation.id ? '确认停用' : '确认创建';
    $('summary').replaceChildren();
    for (const [name,value] of rows) { const row=el('div'); row.append(el('dt',name),el('dd',value)); $('summary').append(row); }
    $('reviewMessage').textContent = ''; $('review').showModal(); $('back').focus();
  }
  for (const kind of kinds) {
    const coupon = kind === 'coupons';
    const form = $(coupon ? 'couponForm' : 'redemptionForm');
    form.addEventListener('input', () => keys.delete(kind));
    form.addEventListener('submit', event => {
      event.preventDefault(); if (!loggedIn || !form.reportValidity()) return;
      try {
        const b = coupon ? couponBody() : redemptionBody();
        const plan = plans.find(p => p.id === b.planId);
        const rows = coupon ? [['套餐',plan.name + ' · ' + plan.credits + ' 积分'],['原价',money(plan.amountFen)],['折后价',money(b.amountFen)],['爱发电方案',b.afdianPlanId],['口令',b.customCode ? '使用已填写的口令' : '自动生成']]
          : [['领取方式',modeLabels[b.mode || 'singleUse']],['每码积分',b.credits],['生成数量',b.count],
            b.mode === 'oncePerAccount' ? ['发放范围','每个账号可从每张共享码各领一次，实际发放积分随领取账号数增加。'] : ['积分合计',b.credits*b.count],
            ['口令',b.customCode ? '使用已填写的自定义口令' : '随机生成'],['批次备注',b.note || '无']];
        rows.push(['有效期',b.days + ' 天']);
        showReview({kind}, coupon ? '确认优惠活动' : '确认兑换码批次', rows);
      } catch (error) { notice(error.message,true); }
    });
  }
  const errors = {
    coupon_plan_conflict:'此爱发电方案已绑定其他价格或套餐，请更换独立折扣方案。', coupon_exists:'这个优惠口令已经发放过，请更换。',
    invalid_coupon:'活动配置无效，请检查折后价和爱发电方案。', coupon_unavailable:'自定义口令不可用，请更换。',
    redemption_already_used:'该兑换码已被使用，无法撤销。', invalid_redemption_options:'请检查积分、数量、有效期和备注。',
    invalid_redemption_code:'自定义兑换口令不可用，请检查字符、长度或保留格式。', redemption_code_exists:'这个口令已发放或与已有口令冲突，请更换后再试。',
    redemption_share_unavailable:'兑换码已过期或停用，无法转为共享码。请返回并刷新记录。'
  };
  $('confirm').addEventListener('click', async () => {
    if (!loggedIn || !review || writing) return;
    const stamp = epoch;
    const operation = review;
    const {kind,id,action} = operation;
    const coupon = kind === 'coupons';
    writing = true; $('confirm').disabled = true; $('back').disabled = true; $('reviewMessage').textContent = '正在提交…';
    try {
      if (id) {
        const result = await api(kind + '/' + encodeURIComponent(id) + '/' + action,'POST',{});
        if (stamp !== epoch) return;
        if (action === 'share' && (result?.id !== id || result.mode !== 'oncePerAccount')) throw new Fault(0,'invalid_reply');
        notice(action === 'share' ? '领取方式已转为共享，原有领取记录保留。' : coupon ? '活动已停用。' : '兑换码已撤销。');
      } else {
        const body = coupon ? couponBody() : redemptionBody();
        if (!keys.has(kind)) keys.set(kind,crypto.randomUUID());
        const result = await api(kind,'POST',body,keys.get(kind));
        if (stamp !== epoch) return;
        const rows = coupon ? [result] : result?.codes;
        if (!Array.isArray(rows) || rows.length !== (coupon ? 1 : body.count) || !rows.every(r => typeof r?.code === 'string' && r.code.length > 0 && r.code.length <= 80))
          throw new Fault(0,'invalid_reply');
        keys.delete(kind); $('codes').value = rows.map(r => r.code).join('\n');
        $('issuedTitle').textContent = coupon ? '优惠口令已创建' : '已生成 ' + rows.length + ' 个兑换码';
        $('issued').hidden = false; $(coupon ? 'customCode' : 'redemptionCustomCode').value = ''; notice();
      }
      closeReview(); if (!id) $('issuedTitle').focus();
      await refresh(kind);
    } catch (error) {
      if (stamp !== epoch) return;
      if (['coupon_issue_exists','redemption_batch_exists'].includes(error.code)) {
        closeReview(); keys.delete(kind); $(coupon ? 'customCode' : 'redemptionCustomCode').value = '';
        notice('服务器已处理这次创建，明文无法再次读取。请查看记录，必要时先停用对应记录。',true);
        await refresh(kind);
      } else {
        const message = Object.hasOwn(errors,error.code) ? errors[error.code]
          : error.status === 429 ? '请求较频繁，请稍后重试。' : '未能确认结果。可返回刷新记录，或使用同一请求重试。';
        $('reviewMessage').textContent = message;
      }
    } finally {
      if (stamp === epoch && review === operation) { writing = false; $('confirm').disabled = false; $('back').disabled = false; }
    }
  });
  $('review').addEventListener('cancel', event => { event.preventDefault(); if (!writing) closeReview(); });
  $('back').addEventListener('click', () => { if (!writing) closeReview(); });
  $('selectCodes').addEventListener('click', () => { $('codes').focus(); $('codes').select(); });
  $('dismissCodes').addEventListener('click', () => { clearIssued(); ($('accountPage').hidden ? ($('couponPage').hidden ? $('redemptionTab') : $('couponTab')) : $('accountTab')).focus(); });
  $('accountTab').addEventListener('click', () => chooseTab('accounts'));
  $('openAccounts').addEventListener('click', () => { const list = chooseTab('accounts',true); $('accountHeading').focus(); return list; });
  $('couponTab').addEventListener('click', () => chooseTab('coupons'));
  $('redemptionTab').addEventListener('click', () => chooseTab('redemptions'));
  $('refreshCoupons').addEventListener('click', () => { if (loggedIn) return Promise.all([refresh('plans'),refresh('coupons'),refresh('summary')]); });
  $('refreshRedemptions').addEventListener('click', () => { if (loggedIn) return Promise.all([refresh('redemptions'),refresh('summary')]); });
  $('plan').addEventListener('change', describePlan);
  $('count').addEventListener('input', updateRedemptionCodeField);
  window.addEventListener('pagehide', () => signOut());
  window.addEventListener('pageshow', event => { if (event.persisted) session(); });
  session();
})();
