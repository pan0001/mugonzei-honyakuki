'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const bridge = window.chrome?.webview;
  const node = (tag, text, style) => {
    const item = document.createElement(tag);
    if (text != null) item.textContent = String(text);
    if (style) item.className = style;
    return item;
  };
  const price = fen => '¥' + (Number(fen) / 100).toFixed(2);
  const date = new Intl.DateTimeFormat('zh-CN', { dateStyle:'short', timeStyle:'short' });
  const states = { pending:'等待付款', paid:'已到账', completed:'已完成', cancelled:'已取消', expired:'已过期', refunded:'已退款',
    refund_pending:'退款处理中', running:'处理中', queued:'排队中', succeeded:'已完成', failed:'未完成', no_text:'没有读到文字' };
  const kinds = { redemption:'兑换码入账', recharge:'充值到账', purchase:'充值', translation:'翻译', refund:'退款', refund_hold:'预留退款积分', refund_release:'释放退款预留', sponsor:'赞助到账' };
  const lookup = (map, key) => Object.hasOwn(map, key) ? map[key] : key;
  let busy = false, signedIn = false, planRows = [], quoteId = null;
  function lock(value) {
    busy = value;
    for (const b of document.querySelectorAll('button:not([data-page]),input')) b.disabled = busy || !signedIn;
  }
  function send(type, fields = {}) {
    if (!bridge || busy || !signedIn) return;
    lock(true); bridge.postMessage({ type, ...fields });
  }
  function button(label, action, style) {
    const b = node('button', label, style); b.type = 'button'; b.disabled = busy || !signedIn; b.addEventListener('click', action); return b;
  }
  function renderRows(id, rows, empty, render) {
    $(id).replaceChildren(...(rows.length ? rows.map(render) : [node('p', empty, 'empty')]));
  }
  const moment = value => { const d = new Date(value); return Number.isFinite(d.valueOf()) ? date.format(d) : ''; };
  function order(row) {
    const e = node('article', null, 'entry'), body = node('div'), actions = node('div', null, 'actions');
    body.append(node('p', row.credits + ' 积分 · ' + price(row.amountFen) + ' · ' + lookup(states, row.status)),
      node('time', moment(row.createdAt)), node('span', row.id, 'ref'));
    if (row.paymentHeld) body.append(node('p', '已阻止付款，请联系运营者核对。', 'muted'));
    if (row.payable) actions.append(button('去付款', () => send('pay', {id:row.id})));
    if (row.status === 'pending') actions.append(button('核验到账', () => send('verify', {id:row.id})));
    e.append(body, actions); return e;
  }
  function plan(row) {
    const e = node('article', null, 'plan');
    const coupon = $('coupon-code').value.trim();
    e.append(node('strong', row.credits + ' 积分'), node('div', row.name, 'muted'),
      button(price(row.amountFen) + (coupon ? ' · 查看优惠价' : ' 充值'), () => {
        clearQuote();
        if ($('coupon-code').value.trim() !== coupon) { showPlans(); return; }
        if (coupon) send('quote', {id:row.id, couponCode:coupon});
        else send('buy', {id:row.id});
      }, 'buy'));
    return e;
  }
  function showPlans() { renderRows('plans', planRows, '当前没有可购买的积分包。', plan); }
  function clearQuote() {
    quoteId = null;
    $('coupon-quote').hidden = true;
    for (const id of ['quote-plan','quote-details','quote-total']) $(id).textContent = '';
  }
  $('coupon-code').addEventListener('input', () => { clearQuote(); showPlans(); });
  $('confirm-coupon').addEventListener('click', () => {
    if (quoteId) send('confirmCoupon', {id:quoteId, couponCode:$('coupon-code').value.trim()});
  });
  function entry(row) {
    const e = node('article', null, 'entry'), body = node('div');
    body.append(node('p', lookup(kinds, row.kind)), node('time', moment(row.createdAt)), node('span', row.reference, 'ref'));
    e.append(body, node('span', (row.amount > 0 ? '+' : '') + row.amount + ' · 余额 ' + row.balance)); return e;
  }
  for (const tab of document.querySelectorAll('[data-page]')) tab.addEventListener('click', () => {
    for (const other of document.querySelectorAll('[data-page]')) {
      const selected = other === tab; other.setAttribute('aria-pressed', String(selected)); $(other.dataset.page).hidden = !selected;
    }
  });
  for (const type of ['refresh','logout','recover','cancel']) $(type).addEventListener('click', () => send(type));
  $('redeem-form').addEventListener('submit', event => { event.preventDefault(); const code = $('code').value; $('code').value = ''; if (code.trim()) send('redeem', {code}); });
  if (!bridge) { $('identity').textContent = '请在无言势翻译器中打开账户。'; lock(true); return; }
  bridge.addEventListener('message', event => {
    const data = event.data;
    switch (data.type) {
      case 'identity': signedIn = true; $('identity').textContent = data.username + ' · ' + data.host; $('content').hidden = false; break;
      case 'busy': lock(data.value); break;
      case 'balance': $('available').textContent = String(Math.max(0, data.balance - data.reserved)); $('details').textContent = '总余额 ' + data.balance + ' · 预留 ' + data.reserved; break;
      case 'plans': clearQuote(); planRows = data.rows; showPlans(); break;
      case 'quote':
        quoteId = data.id;
        $('quote-plan').textContent = data.name + ' · ' + data.credits + ' 积分';
        $('quote-details').textContent = '原价 ' + price(data.originalAmountFen) + ' · 优惠 ' + price(data.discountFen);
        $('quote-total').textContent = '应付 ' + price(data.amountFen);
        $('coupon-quote').hidden = false; break;
      case 'clearQuote':
        clearQuote();
        if (data.purchased) { $('coupon-code').value = ''; showPlans(); }
        break;
      case 'orders': renderRows('orders', data.rows, '暂无充值订单。', order); break;
      case 'ledger': renderRows('ledger', data.rows, '暂无积分流水。', entry); break;
      case 'recent': $('task-status').textContent = data.isExpired ? '译文已过期' : (data.error || lookup(states,data.status)); $('source').textContent = data.sourceText || '—'; $('translation').textContent = data.translation || '—'; $('task-text').hidden = false; break;
      case 'notice': $('notice').textContent = data.text; break;
      case 'signedOut':
        signedIn = false; $('content').hidden = true; $('identity').textContent = data.message; $('notice').textContent = '关闭账户窗口后，可重新登录。';
        for (const id of ['available','details','plans','orders','ledger','source','translation','task-status']) $(id).replaceChildren();
        $('code').value = ''; $('coupon-code').value = ''; planRows = []; clearQuote(); lock(false); break;
    }
  });
  lock(true); bridge.postMessage({type:'ready'});
})();
