// node tests/website-admin.cjs — runs the shipped client with a local, in-memory API.
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const assert = require('node:assert/strict'), crypto = require('node:crypto');
const dir = path.join(__dirname,'../website/admin');
const html = fs.readFileSync(path.join(dir,'index.html'),'utf8');
const js = fs.readFileSync(path.join(dir,'admin.js'),'utf8');
const accountsJs = fs.readFileSync(path.join(dir,'accounts.js'),'utf8');
class Control {
  constructor(tag) { this.tag=tag; this.children=[]; this.listeners={}; this.attrs={}; this.dataset={}; this.value=''; this.textContent=''; this.hidden=false; this.disabled=false; }
  append(...els) { this.children.push(...els); }
  replaceChildren(...els) { this.children=els; this.textContent=''; }
  addEventListener(type,fn) { this.listeners[type]=fn; }
  setAttribute(name,val) { this.attrs[name]=val; }
  removeAttribute(name) { delete this.attrs[name]; }
  reportValidity() { return true; }
  reset() { for(const c of this.fields||[]) c.value=c.defaultValue; }
  focus() {} select() {}
  showModal() { this.open=true; }
  close() { this.open=false; }
  set innerHTML(_) { throw new Error('HTML injection'); }
}
const dom = new Map();
for (const m of html.matchAll(/<([a-z0-9]+)\b([^>]*\bid="([^"]+)"[^>]*)>/gi)) {
  const c=new Control(m[1]); c.value=c.defaultValue=m[2].match(/\bvalue="([^"]*)"/)?.[1]||'';
  c.hidden=/\bhidden\b/.test(m[2]); c.disabled=/\bdisabled\b/.test(m[2]); dom.set(m[3],c);
}
const $=id=>{assert(dom.has(id),'Missing element '+id);return dom.get(id);};
for(const m of html.matchAll(/<select id="([^"]+)"[^>]*>\s*<option value="([^"]+)"/g)) $(m[1]).value=$(m[1]).defaultValue=m[2];
for(const m of html.matchAll(/<form id="([^"]+)"[^>]*>([\s\S]*?)<\/form>/g))
  $(m[1]).fields=[...m[2].matchAll(/<(?:input|select)\b[^>]*\bid="([^"]+)"/g)].map(v=>$(v[1]));
const win=new Control('window');
const calls=[], token='Fixture-'.repeat(6);
const plan={id:'starter',name:'小额方案',credits:50,amountFen:3000,afdianPlanId:'a'.repeat(32)};
const coupon={id:'b'.repeat(32),planId:plan.id,amountFen:1999,afdianPlanId:'d'.repeat(32),expiresAt:'2099-02-01',revokedAt:null};
const redemption={id:'c'.repeat(32),credits:10,note:'test <img>',status:'active',mode:'singleUse',claimCount:0,expiresAt:'2099-02-01'};
let used={...redemption,id:'e'.repeat(32),status:'redeemed',claimCount:1};
let rejectShare=false;
const redemptions=()=>[{...redemption,code:'SHOULD-NOT-RENDER'},used,
  {...redemption,id:'0'.repeat(32),mode:'oncePerAccount',claimCount:7},
  {...redemption,id:'1'.repeat(32),status:'revoked'}, {...redemption,id:'2'.repeat(32),expiresAt:'2000-01-01',status:'redeemed'}];
const ok=(data,status=200)=>({ok:status>=200&&status<300,status,json:async()=>data});
const fixtureId = 'a'.repeat(32);
function accountServer() {
  const primary = {id:fixtureId,username:'练习账号',disabled:false,balance:105,reserved:5,available:100};
  const others = Array.from({length:20},(_,i)=>({...primary,id:(i+1).toString(16).padStart(32,'0'),username:'模拟'+i}));
  const rows = [primary,...others];
  const date='2026-11-05T11:20:00Z';
  const orders = Array.from({length:12},(_,i)=>({id:'order-'+i,name:'体验套餐 '+i,credits:200,amountFen:500,status:'paid',createdAt:date}));
  const ledger = Array.from({length:13},(_,i)=>({id:i,kind:'redemption',amount:100,balance:100,createdAt:date}));
  const applied = new Map(), audit = [];
  return function serve(route,method,body,key) {
    const url = new URL(route,'https://fixture.invalid/');
    const [resource,id,history] = url.pathname.slice(1).split('/');
    if (resource!=='accounts') return null;
    const page = (items,param='page') => {
      const n=Number(url.searchParams.get(param)||1), limit=Number(url.searchParams.get('pageSize')||10);
      return {items:items.slice((n-1)*limit,n*limit),total:items.length,page:n,pageSize:limit};
    };
    if (!id) {
      const search = url.searchParams.get('q') || '';
      return ok(page(rows.filter(item=>item.username.includes(search)||item.id===search)));
    }
    const account = rows.find(item=>item.id===id);
    if (!account) return ok({},404);
    if (!history) return ok({account:{...account},orders:page(orders,'orderPage'),ledger:page(ledger,'ledgerPage')});
    assert.equal(history,'actions');
    if (method==='GET') return ok(page(audit));
    assert.match(key,/^[a-f0-9-]{36}$/);
    if (applied.has(key)) { assert.deepEqual(body,applied.get(key)); return ok({...account,...body,alreadyApplied:true}); }
    applied.set(key,body); account.balance+=body.delta; account.available+=body.delta;
    if (body.action!=='adjust') account.disabled=body.action==='suspend';
    audit.unshift({...body,balanceAfter:account.balance,createdAt:date});
    return ok({...body,balance:account.balance,available:account.available,reserved:account.reserved,disabled:account.disabled,alreadyApplied:false});
  };
}
const serveAccounts=accountServer();
let interceptAccount;
let session=false, summaryOffline=false, couponReply, batchReply, listReply;
async function transport(url,opts) {
  assert(url.startsWith('/admin/api/')); assert.equal(opts.credentials,'same-origin'); assert.equal(opts.mode,'same-origin'); assert.equal(opts.redirect,'error'); assert.equal(opts.cache,'no-store');
  if(opts.method!=='GET') { assert.equal(opts.headers['X-Mugonzei-Admin'],'1'); assert.equal(opts.headers['Content-Type'],'application/json'); }
  const route=url.slice('/admin/api/'.length); const body=opts.body===undefined?undefined:JSON.parse(opts.body);
  const key=opts.headers['Idempotency-Key']; calls.push({route,method:opts.method,key,body});
  if(route==='session'){
    if(opts.method==='POST'){assert.deepEqual(body,{token});session=true;}
    if(opts.method==='DELETE'){session=false;return ok(null,204);}
    return ok({authenticated:session});
  }
  assert(session,'The client calls protected endpoints only in an authenticated session');
  if(route==='summary')return summaryOffline?ok({},503):ok({accounts:21,paidOrders:2,paidAmountFen:2587});
  if(route.startsWith('accounts'))return interceptAccount?.(route,opts.method,body,key) || serveAccounts(route,opts.method,body,key);
  if(route==='plans')return ok([plan]);
  if(opts.method==='GET'&&route==='coupons')return listReply?listReply():ok([{...coupon,code:'SHOULD-NOT-RENDER'}]);
  if(opts.method==='GET'&&route==='redemptions')return ok(redemptions());
  if(opts.method==='POST'&&route==='coupons')return couponReply();
  if(opts.method==='POST'&&route==='redemptions')return batchReply();
  if(route.endsWith('/revoke'))return ok({revoked:true});
  if(route==='redemptions/'+used.id+'/share') {
    assert.deepEqual(body,{});
    if(rejectShare)return ok({error:'redemption_share_unavailable'},409);
    used={...used,mode:'oncePerAccount',status:'active'};return ok(used);
  }
  throw Error('Unexpected path');
}
const document={getElementById:$,createElement:t=>new Control(t)};
const context=vm.createContext({document,window:win,fetch:transport,crypto,Intl,Date,AbortController,setTimeout,clearTimeout});
vm.runInContext(accountsJs,context); vm.runInContext(js,context);
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const evt={preventDefault(){}}, submit=id=>$(id).listeners.submit(evt), click=id=>$(id).listeners.click();
const flatten=c=>[c,...c.children.flatMap(flatten)];
const content=id=>flatten($(id)).map(x=>x.textContent).join('|');
const posts=route=>calls.filter(c=>c.method==='POST'&&c.route===route);
async function login(){ $('token').value=token; await submit('loginForm'); }
async function accountTests() {
  const button=(id,label)=>flatten($(id)).find(c=>c.tag==='button' && c.textContent===label);
  assert.equal($('accountPage').hidden,false);
  assert.equal(flatten($('accountList')).filter(n=>n.tag==='button').length,20);
  await button('accountPager','下一页').listeners.click();
  assert.equal(flatten($('accountList')).filter(n=>n.tag==='button').length,1);
  $('accountSearch').value='不存在'; await submit('accountSearchForm'); assert.match(content('directoryStatus'),/没有找到/);
  let late;
  interceptAccount=route=>route.includes('q=late')?new Promise(resolve=>{late=resolve;}):null;
  $('accountSearch').value='late'; const older=submit('accountSearchForm');
  $('accountSearch').value=fixtureId; await submit('accountSearchForm');
  late(ok({items:[],total:0,page:1,pageSize:20})); await older; interceptAccount=null;
  assert(content('accountList').includes('练习账号'));
  await button('accountList','查看明细').listeners.click();
  assert.equal($('accountName').textContent,'练习账号'); assert(content('accountMetrics').includes('预留积分'));
  assert(content('accountOrders').includes('¥5.00')); assert(content('accountLedger').includes('兑换码入账'));
  await button('orderPager','下一页').listeners.click(); assert(content('accountOrders').includes('体验套餐 11'));
  await button('ledgerPager','下一页').listeners.click(); assert(content('orderPager').includes('2 / 2'));
  await click('adjustCredits'); $('adjustDirection').value='subtract'; $('adjustAmount').value='101'; $('actionReason').value='核对奖励';
  await submit('actionForm'); assert(content('actionMessage').includes('超过当前可用'));
  $('adjustAmount').value='7'; await submit('actionForm');
  assert(content('actionReview').includes('-7')); assert(content('actionTarget').includes(fixtureId));
  const endpoint='accounts/'+fixtureId+'/actions';
  assert.equal(posts(endpoint).length,0,'No change until the operator confirms amount, recipient and reason');
  let lose;
  interceptAccount=(route,method,body,key)=>{
    if(route!==endpoint || method!=='POST')return null;
    serveAccounts(route,method,body,key);return new Promise((_,reject)=>{lose=reject;});
  };
  const first=submit('actionForm'); await submit('actionForm'); assert.equal(posts(endpoint).length,1);
  lose(Error('lost after commit'));await first;interceptAccount=null;await click('actionClose');
  await click('refreshDetail'); assert.equal($('adjustCredits').disabled,true); assert.equal($('retryAction').hidden,false);
  await click('retryAction');await submit('actionForm');
  assert.equal(posts(endpoint)[0].key,posts(endpoint)[1].key);
  assert.deepEqual(posts(endpoint)[1].body,{action:'adjust',delta:-7,reason:'核对奖励'});
  assert(content('accountMetrics').includes('93'));assert(content('accountAudit').includes('核对奖励'));assert.equal($('adjustCredits').disabled,false);
  await click('toggleAccount');$('actionReason').value='暂停测试';await submit('actionForm');await submit('actionForm');
  assert.equal($('toggleAccount').textContent,'恢复账号');assert(content('accountMetrics').includes('已停用'));
  await click('toggleAccount');$('actionReason').value='恢复测试';await submit('actionForm');await submit('actionForm');
  assert.equal($('toggleAccount').textContent,'停用账号');
  let finish;
  interceptAccount=route=>route.startsWith('accounts/'+fixtureId+'?')?new Promise(resolve=>{finish=resolve;}):null;
  const oldDetail=click('refreshDetail');await click('logout');finish(ok({account:{id:fixtureId,username:'old-session'}}));await oldDetail;
  assert.equal($('accountList').children.length,0);assert.equal($('accountLedger').children.length,0);assert.equal($('accountAudit').children.length,0);
  assert(!content('detailStatus').includes('读取失败'));assert.equal($('accountSearch').value,'');interceptAccount=null;await login();
}
async function tests(){
  await tick();
  assert.deepEqual(calls.map(c=>c.route),['session']); assert.equal($('workspace').hidden,true);
  await login();assert.equal($('token').value,'');assert.equal($('workspace').hidden,false);
  assert.equal($('paidAmount').textContent,'¥25.87');assert.equal($('accounts').textContent,21);
  await accountTests();
  await click('couponTab');
  assert(content('plan').includes(plan.name));assert(!content('couponList').includes('SHOULD-NOT-RENDER'));
  $('price').value='30';$('providerPlan').value=coupon.afdianPlanId;await submit('couponForm');assert(!$('review').open);
  $('price').value='19.99';$('providerPlan').value=plan.afdianPlanId;await submit('couponForm');assert(!$('review').open);
  $('providerPlan').value=coupon.afdianPlanId;$('customCode').value='SOURCE-TEST';await submit('couponForm');
  assert($('review').open);assert(content('summary').includes('¥19.99'));assert(content('summary').includes(plan.name));
  assert(!content('summary').includes('SOURCE-TEST'));assert.equal(posts('coupons').length,0);
  await click('back');assert(!$('review').open);await submit('couponForm');
  let rejectWrite;
  couponReply=()=>new Promise((_,reject)=>{rejectWrite=reject;});
  const first=click('confirm');await click('confirm');assert.equal(posts('coupons').length,1);
  assert($('confirm').disabled && $('back').disabled);
  assert.deepEqual(posts('coupons')[0].body,{planId:plan.id,amountFen:1999,afdianPlanId:coupon.afdianPlanId,days:30,customCode:'SOURCE-TEST'});
  rejectWrite(Error('lost response'));await first;assert($('review').open && $('reviewMessage').textContent.includes('同一请求'));
  await click('back');await submit('couponForm');
  couponReply=()=>ok({error:'coupon_issue_exists'},409);await click('confirm');
  const prior=posts('coupons');
  assert.match(prior[0].key,/^[0-9a-f-]{36}$/);assert.equal(prior[0].key,prior[1].key);
  assert($('issued').hidden && $('notice').textContent.includes('无法再次读取'));assert.equal($('customCode').value,'');
  $('price').value='7.05';$('couponForm').listeners.input();
  couponReply=()=>ok({...coupon,code:'MC-SYNTHETIC'},201);await submit('couponForm');await click('confirm');
  assert.equal(posts('coupons').at(-1).body.amountFen,705);assert(!('customCode' in posts('coupons').at(-1).body));
  assert.notEqual(posts('coupons').at(-1).key,prior[0].key);
  assert.equal($('codes').value,'MC-SYNTHETIC');assert(!$('issued').hidden);
  await submit('couponForm');assert(!$('review').open);await click('dismissCodes');assert.equal($('codes').value,'');
  const stop=flatten($('couponList')).find(c=>c.tag==='button');stop.listeners.click();
  assert.equal(posts('coupons/'+coupon.id+'/revoke').length,0);await click('confirm');assert.equal(posts('coupons/'+coupon.id+'/revoke').length,1);
  await click('redemptionTab');assert(content('redemptionList').includes('test <img>'));assert(!content('redemptionList').includes('SHOULD-NOT-RENDER'));
  const revoke=flatten($('redemptionList')).find(c=>c.tag==='button');revoke.listeners.click();
  assert.equal(posts('redemptions/'+redemption.id+'/revoke').length,0);await click('confirm');assert.equal(posts('redemptions/'+redemption.id+'/revoke').length,1);
  const shareButtons=()=>flatten($('redemptionList')).filter(c=>c.tag==='button'&&c.textContent==='转为共享码');
  assert.equal(shareButtons().length,2,'Only current single-use records, including a previously claimed record, offer conversion');
  assert(content('redemptionList').includes('已领取账号 7'));
  shareButtons()[1].listeners.click();const sharePath='redemptions/'+used.id+'/share';
  assert.equal($('confirm').textContent,'确认转为共享码');assert(content('summary').includes('已领取账号保留记录'));
  await click('back');assert.equal(posts(sharePath).length,0,'Canceling review cannot convert the original credential');
  shareButtons()[1].listeners.click();rejectShare=true;await click('confirm');
  assert($('reviewMessage').textContent.includes('过期或停用') && $('review').open);
  rejectShare=false;await click('confirm');assert.equal(posts(sharePath).length,2);assert.equal(shareButtons().length,1);
  assert(content('redemptionList').includes('共享码 · 每个账号可领一次 · 已领取账号 1'));
  $('credits').value='10';$('count').value='1';$('count').listeners.input();
  for(const phrase of ['SampleEnglish','中文测试口令']){
    const shared=phrase==='中文测试口令';$('redemptionMode').value=shared?'oncePerAccount':'singleUse';
    $('redemptionCustomCode').value=phrase; await submit('redemptionForm');
    assert(content('summary').includes('使用已填写的自定义口令')&&!content('summary').includes(phrase));
    assert.equal(content('summary').includes('实际发放积分随领取账号数增加'),shared);
    assert.equal(content('summary').includes('积分合计'),!shared);
    batchReply=()=>ok({codes:[{code:phrase}]},201); await click('confirm');
    assert.equal(posts('redemptions').at(-1).body.customCode,phrase);
    assert.equal(posts('redemptions').at(-1).body.mode,shared?'oncePerAccount':undefined);
    assert.equal($('codes').value,phrase);assert.equal($('redemptionCustomCode').value,'');
    await click('dismissCodes');assert.equal($('codes').value,'');
  }
  $('redemptionMode').value='singleUse';
  $('redemptionCustomCode').value='SingleOnly';
  $('count').value='2';$('count').listeners.input();
  assert($('redemptionCustomCode').disabled && !$('redemptionCustomCode').value,'Batch selection clears and disables the single-code credential');
  const singlePosts = posts('redemptions').length;
  $('credits').value='10';$('count').value='51';await submit('redemptionForm');assert(!$('review').open);
  $('count').value='2';$('note').value='QA';await submit('redemptionForm');
  assert(content('summary').includes('20') && content('summary').includes('随机生成'));assert.equal(posts('redemptions').length,singlePosts);
  batchReply=()=>Promise.reject(Error('lost'));await click('confirm');const lostKey=posts('redemptions').at(-1).key;
  batchReply=()=>ok({error:'redemption_batch_exists'},409);await click('confirm');assert.equal(posts('redemptions').at(-1).key,lostKey);
  assert($('issued').hidden && $('notice').textContent.includes('无法再次读取'));
  await submit('redemptionForm');
  batchReply=()=>ok({codes:[{code:'MU-ONE'},{code:'MU-TWO'}]},201);
  await click('confirm');assert.equal($('codes').value,'MU-ONE\nMU-TWO');
  assert.deepEqual(posts('redemptions').at(-1).body,{credits:10,count:2,days:30,note:'QA'});assert.match(posts('redemptions').at(-1).key,/^[0-9a-f-]{36}$/);
  await click('logout');assert($('workspace').hidden && !$('login').hidden);assert.equal($('codes').value,'');assert.equal($('note').value,'');
  summaryOffline=true;await login();assert.equal($('accounts').textContent,'—');assert(!$('reviewCoupon').disabled);
  summaryOffline=false;await click('refreshCoupons');assert.equal($('accounts').textContent,21);
  summaryOffline=true;await click('refreshRedemptions');assert.equal($('accounts').textContent,'—');
  let finish;listReply=()=>new Promise(r=>{finish=r;});const old=click('refreshCoupons');await click('logout');
  finish(ok([coupon]));await old;assert.equal($('couponList').children.length,0,'Late session cannot repopulate logged-out metadata');
  assert(!/localStorage|sessionStorage|document\.cookie|innerHTML|console\./.test(js+accountsJs));
  assert(html.includes('name="robots" content="noindex,nofollow"'));assert(html.includes('src="/admin/admin.js"'));assert(html.includes('href="/admin/admin.css"'));
  console.log('PASS admin website: account search, pagination, signed adjustments, suspension/restoration, stale response isolation, idempotent retry, coupons and redemptions');
}
// Optional real-browser check: provide Playwright and CHROMIUM_PATH to an installed browser.
// A loopback HTTP server inspects the browser's actual Origin and cookie headers.
async function browserTest() {
  const http = require('node:http');
  const { chromium } = require('playwright');
  const writes = [];
  let converted=false, browserAccounts;
  const server = http.createServer(async (req,res) => {
    const origin = 'http://127.0.0.1:' + server.address().port;
    const files = {'/admin/':'index.html','/admin/admin.js':'admin.js','/admin/accounts.js':'accounts.js','/admin/admin.css':'admin.css'};
    res.setHeader('Referrer-Policy','no-referrer');
    if (files[req.url]) {
      const f=files[req.url];
      res.setHeader('Content-Type',({'.js':'application/javascript','.html':'text/html','.css':'text/css'}[path.extname(f)])+'; charset=utf-8');
      res.end(fs.readFileSync(path.join(dir,f)));return;
    }
    if(!req.url.startsWith('/admin/api/')) {res.writeHead(404).end();return;}
    const route=req.url.slice('/admin/api/'.length);
    res.setHeader('Content-Type','application/json');
    if(req.method!=='GET') {
      writes.push({method:req.method,route,origin:req.headers.origin});
      if(req.headers.origin!==origin || req.headers['x-mugonzei-admin']!=='1') {res.writeHead(403).end('{}');return;}
    }
    const auth=req.headers.cookie==='admin-test=local';
    if(route==='session') {
      if(req.method==='POST')res.setHeader('Set-Cookie','admin-test=local; HttpOnly; SameSite=Strict; Path=/admin');
      if(req.method==='DELETE'){res.setHeader('Set-Cookie','admin-test=; Max-Age=0; Path=/admin');res.writeHead(204).end();return;}
      res.end(JSON.stringify({authenticated:req.method==='POST'||auth}));return;
    }
    if(!auth){res.writeHead(401).end('{}');return;}
    const previous={...redemption,id:'d'.repeat(32),mode:converted?'oncePerAccount':'singleUse',status:converted?'active':'redeemed',claimCount:1};
    const paths={plans:[plan],coupons:[coupon],redemptions:[previous],summary:{accounts:21,paidOrders:1,paidAmountFen:1999}};
    if(route.startsWith('accounts')) {
      const chunks=[];for await(const chunk of req)chunks.push(chunk);
      const body=chunks.length?JSON.parse(Buffer.concat(chunks)):undefined;
      const result=browserAccounts(route,req.method,body,req.headers['idempotency-key']);
      res.writeHead(result.status).end(JSON.stringify(await result.json()));return;
    }
    if(req.method==='POST'&&route==='coupons'){
      assert.match(req.headers['idempotency-key'],/^[a-f0-9-]{36}$/);
      res.writeHead(201).end(JSON.stringify({...coupon,code:'SYNTHETIC-ONCE'}));return;
    }
    if(req.method==='POST'&&route==='redemptions'){
      assert.match(req.headers['idempotency-key'],/^[a-f0-9-]{36}$/);
      const chunks=[];for await(const chunk of req)chunks.push(chunk);
      const body=JSON.parse(Buffer.concat(chunks));assert.equal(body.count,1);assert.equal(body.customCode,'测试专用口令');
      assert.equal(body.mode,'oncePerAccount');
      res.writeHead(201).end(JSON.stringify({codes:[{...redemption,code:body.customCode}]}));return;
    }
    if(req.method==='POST'&&route==='redemptions/'+previous.id+'/share'){
      converted=true;res.end(JSON.stringify({...previous,mode:'oncePerAccount',status:'active'}));return;
    }
    res.end(JSON.stringify(paths[route]));
  });
  let browser;
  try {
    await new Promise(r=>server.listen(0,'127.0.0.1',r));
    const origin='http://127.0.0.1:'+server.address().port;
    browser=await chromium.launch({headless:true,executablePath:process.env.CHROMIUM_PATH,args:['--disable-background-networking']});
    for(const width of [320,390,1440]){
      converted=false;browserAccounts=accountServer();
      const page=await browser.newPage({viewport:{width,height:820}});page.setDefaultTimeout(4500);
      page.on('request',request=>assert(request.url().startsWith(origin+'/'),'The admin page only requests local resources'));
      await page.goto(origin+'/admin/');await page.locator('#token').fill(token);
      await Promise.all([page.waitForResponse(r=>r.url()===origin+'/admin/api/session' && r.request().method()==='POST'),page.locator('#signIn').click()]);
      assert.equal(writes.at(-1)?.origin,origin,'The browser must send its real same-origin Origin on login');
      await page.locator('#accountSearch').fill('练习');await page.locator('#accountSearchForm button[type=submit]').click();
      await page.getByRole('button',{name:'查看 练习账号 的账号明细'}).click();await page.locator('#adjustCredits').click();
      await page.locator('#adjustAmount').fill('5');await page.locator('#actionReason').fill('现场演示');await page.locator('#actionSubmit').click();
      assert((await page.locator('#actionReview').textContent()).includes('+5'));
      assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth && document.querySelector('#actionDialog').getBoundingClientRect().right<=innerWidth));
      await page.locator('#actionSubmit').click();await page.locator('#accountAudit').getByText('现场演示').waitFor();
      assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
      await page.locator('#couponTab').click();
      await page.locator('#plan option').first().waitFor({state:'attached'});
      await page.locator('#price').fill('19.99');await page.locator('#providerPlan').fill(coupon.afdianPlanId);
      await page.locator('#reviewCoupon').click();await page.locator('dialog[open]').waitFor();
      assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth && document.querySelector('dialog').getBoundingClientRect().right<=innerWidth));
      await page.locator('#confirm').click();await page.locator('#codes').waitFor();assert.equal(await page.locator('#codes').inputValue(),'SYNTHETIC-ONCE');
      await page.locator('#dismissCodes').click();await page.locator('#redemptionTab').click();
      await page.getByRole('button',{name:'转为共享码',exact:true}).click();await page.getByRole('button',{name:'确认转为共享码'}).click();
      await page.locator('#redemptionList').getByText('共享码 · 每个账号可领一次 · 已领取账号 1').waitFor();
      await page.locator('#redemptionMode').selectOption('oncePerAccount');
      await page.locator('#credits').fill('20');await page.locator('#redemptionCustomCode').fill('draft-phrase');await page.locator('#count').fill('2');
      assert(await page.locator('#redemptionCustomCode').isDisabled());assert.equal(await page.locator('#redemptionCustomCode').inputValue(),'');
      await page.locator('#count').fill('1');await page.locator('#redemptionCustomCode').fill('测试专用口令');
      await page.getByRole('button',{name:'核对批次'}).click();assert(!(await page.locator('#summary').textContent()).includes('测试专用口令'));
      await page.locator('#confirm').click();await page.locator('#codes').waitFor();assert.equal(await page.locator('#codes').inputValue(),'测试专用口令');
      assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
      await page.locator('#logout').click();await page.locator('#notice').filter({hasText:'已退出管理。'}).waitFor();
      await page.close();
    }
    assert.equal(writes.length,18);
    console.log('PASS real Chromium login/create/logout same-origin headers and authenticated layouts at 320/390/1440px');
  }finally{await browser?.close();await new Promise(r=>server.close(r));}
}
tests().then(()=>process.argv.includes('--browser')?browserTest():undefined).catch(e=>{console.error(e);process.exitCode=1;});
