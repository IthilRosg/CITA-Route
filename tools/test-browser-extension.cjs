const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const path = require('node:path');
const root = path.join(__dirname, '..', 'browser-extension');
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'manifest.json'), 'utf8'));
const extensionId = [...crypto.createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest().subarray(0, 16)]
  .flatMap(byte => [String.fromCharCode(97 + (byte >> 4)), String.fromCharCode(97 + (byte & 15))]).join('');
const event = () => ({ handlers: [], addListener(fn) { this.handlers.push(fn); } });
const turn = () => new Promise(resolve => setTimeout(resolve, 20));
(async () => {
  assert.equal(extensionId, 'gokcbalkmlaaakcdnlflpknihmpaekga');
  assert.equal(manifest.incognito, 'not_allowed');
  assert(!manifest.permissions.includes('debugger') && !manifest.externally_connectable);
  let active = false, clock = 0, connects = 0;
  const posted = [], sent = [];
  const port = { onDisconnect: event(), onMessage: event(), postMessage(value) { posted.push(value); }, disconnect() {} };
  const chrome = {
    runtime: { id: extensionId, lastError: null, onMessage: event(), getURL: value => `chrome-extension://${extensionId}/${value}`,
      connectNative(name) { assert.equal(name, 'xyz.citaeris.route.feedback'); connects++; return port; } },
    action: { setBadgeText() {} }, tabs: { async sendMessage(...values) { sent.push(values); } },
    storage: { local: { async get() { return { enabled: active }; } }, onChanged: event() },
    webRequest: { onBeforeRequest: event(), onCompleted: event(), onErrorOccurred: event() }
  };
  const context = vm.createContext({ chrome, crypto: crypto.webcrypto, URL, TextEncoder, performance: { now: () => clock }, setTimeout() { return 1; }, setInterval() {} });
  vm.runInContext(fs.readFileSync(path.join(root, 'background.js'), 'utf8'), context);
  await turn(); assert.equal(connects, 0, 'Disabled means no native process');
  active = true; await vm.runInContext('setting()', context); assert.equal(connects, 1);
  let stateReply;chrome.runtime.onMessage.handlers[0]({ kind: 'state' }, { id: extensionId, tab: { id: 2 }, url: `chrome-extension://${extensionId}/popup.html` }, value => { stateReply = value; });
  assert.equal(stateReply.enabled, true, 'The own popup works when opened as an extension tab');
  let leaked = false;chrome.runtime.onMessage.handlers[0]({ kind: 'state' }, { id: extensionId, url: 'https://untrusted.example/' }, () => { leaked = true; });assert.equal(leaked, false);
  const begin = chrome.webRequest.onBeforeRequest.handlers[0], fail = chrome.webRequest.onErrorOccurred.handlers[0], complete = chrome.webRequest.onCompleted.handlers[0];
  const item = (id, url, extra = {}) => ({ requestId: id, url, method: 'GET', type: 'image', tabId: 1, frameId: 0, ...extra });
  for (const url of ['https://127.0.0.1/a', 'https://printer.local/a', 'https://host.ts.net/a', 'https://chatgpt.com/a', 'http://example.com/a', 'https://u:p@example.com/a', 'https://example.com:8443/a']) begin(item(url, url));
  begin(item('post', 'https://cdn.example.com/a', { method: 'POST' }));
  begin(item('private', 'https://cdn.example.com/a', { incognito: true }));
  assert.equal(vm.runInContext('requests.size', context), 0);
  const doc='a1111111-1111-1111-1111-111111111111',doc2='b2222222-2222-2222-2222-222222222222';
  const relationship = extra => JSON.parse(vm.runInContext(`JSON.stringify(dependency(${JSON.stringify({type:'image',documentId:doc,initiator:'https://page.example.com',...extra})}, ${clock}))`, context));
  const rel=relationship({});assert.equal(rel.parentDomain,'page.example.com');
  assert.equal(relationship({}).contextId,rel.contextId);
  assert.notEqual(relationship({documentId:doc2}).contextId,rel.contextId,'Different documents on the same origin never share identity');
  assert(relationship({documentId:'ABCDEF0123456789ABCDEF0123456789'}).contextId,'Chromium compact 128-bit document tokens are supported');
  for(const extra of [{initiator:'null'},{initiator:'https://host.ts.net'},{initiator:'https://chatgpt.com'},{documentId:undefined},{type:'main_frame'},{type:'sub_frame'}])assert.deepEqual(relationship(extra),{});
  begin(item('context-resource','https://cdn.example.com/a?private=HIDDEN',{documentId:doc,initiator:'https://page.example.com'}));
  const envelope=JSON.parse(vm.runInContext('JSON.stringify(queue.at(-1))',context));
  assert.equal(envelope.contextId,rel.contextId);assert.equal(envelope.parentDomain,'page.example.com');
  assert(!JSON.stringify(envelope).includes(doc)&&!JSON.stringify(envelope).includes('HIDDEN'),'Browser document UUID and resource URL stay inside browser');
  complete({requestId:'context-resource',statusCode:403,fromCache:false});
  begin(item('one', 'https://cdn.example.com/a.png?token=DO_NOT_SEND'));
  vm.runInContext('flush()', context);
  assert(!JSON.stringify(posted).includes('DO_NOT_SEND') && !JSON.stringify(posted).includes('a.png'));
  assert.equal(posted[0].events[0].domain, 'cdn.example.com');
  port.onMessage.handlers[0]({ id: posted[0].id, entries: [{ id: posted[0].events[0].id, status: 'watching' }] });
  fail({ requestId: 'one', error: 'net::ERR_BLOCKED_BY_CLIENT' });
  assert.equal(vm.runInContext('counters.errors', context), 0);
  for (const type of ['main_frame', 'sub_frame', 'script', 'stylesheet', 'font', 'image', 'media', 'xmlhttprequest']) {
    begin(item('ok-' + type, 'https://cdn.example.com/resource?secret=PRIVATE', { type }));
    complete({ requestId: 'ok-' + type, statusCode: 200, fromCache: false });
    complete({ requestId: 'ok-' + type, statusCode: 200, fromCache: false });
  }
  assert.equal(vm.runInContext('counters.completed', context), 8, 'Each network resource completes exactly once');
  for (const [statusCode, fromCache] of [[200, true], [304, false], [403, false], [429, false], [500, false], [200, undefined]]) {
    begin(item('no-proof', 'https://cdn.example.com/resource'));
    complete({ requestId: 'no-proof', statusCode, fromCache });
  }
  assert.equal(vm.runInContext('counters.completed', context), 8, 'Cache hits, unknown provenance and HTTP refusals never certify a live path');
  assert(!JSON.stringify(vm.runInContext('queue', context)).includes('PRIVATE'), 'Success feedback never carries a URL or query');
  begin(item('two', 'https://cdn.example.com/b.png'));
  fail({ requestId: 'two', error: 'net::ERR_CONNECTION_CLOSED' });
  fail({ requestId: 'two', error: 'net::ERR_CONNECTION_CLOSED' });
  assert.equal(vm.runInContext('counters.errors', context), 1, 'One resource is counted once');
  begin(item('dns','https://cdn.example.com/resource'));
  fail({requestId:'dns',error:'net::ERR_NAME_NOT_RESOLVED'});
  assert.equal(vm.runInContext('counters.errors',context),2,'DNS failures are forwarded as check hints');
  for (let i = 0; i < 1000; i++) begin(item('f' + i, 'https://cdn.example.com/a.png'));
  assert(vm.runInContext('requests.size <= 128 && queue.length <= 256 && pending.size <= 16', context));
  for(let i=0;i<300;i++)relationship({documentId:`${i.toString(16).padStart(8,'0')}-1111-1111-1111-111111111111`});
  assert(vm.runInContext('documents.size <= 128',context),'Document identities are bounded');
  clock=600000;assert.notEqual(relationship({}).contextId,rel.contextId,'Expired document identity is not reused');
  vm.runInContext('queue=[]; busy=false; timer=null;',context);
  const longDomain=[63,63,63,61].map(n=>'a'.repeat(n)).join('.');
  context.largeEvents=Array.from({length:32},()=>({id:crypto.randomUUID().replaceAll('-',''),kind:'begin',domain:longDomain,parentDomain:longDomain,contextId:crypto.randomUUID().replaceAll('-',''),method:'GET',resource:'Image'}));
  vm.runInContext('queue.push(...largeEvents);flush()',context);
  const boundedBatch=posted.at(-1);assert(Buffer.byteLength(JSON.stringify(boundedBatch))<=16000&&boundedBatch.events.length<32,'Long paired domains split at the native byte limit');
  assert(vm.runInContext('queue.length > 0',context),'Remaining events are retained for the next batch');
  port.onMessage.handlers[0]({id:boundedBatch.id,entries:[]});vm.runInContext('flush()',context);
  assert.equal(vm.runInContext('queue.length',context),0);
  class Image {
    constructor(src) { this._src = src; this.currentSrc = src; this.naturalWidth = 0; this.isConnected = true; this.srcset = ''; this.retries = 0; }
    get src() { return this._src; } set src(value) { this._src = value; this.retries++; }
    closest() { return null; }
  }
  const images = [new Image('https://cdn.example.com/a.png'), new Image('https://cdn.example.com/b.png'), new Image('https://cdn.example.com/a.png?signature=x')];
  let resourceError; const onMessage = event();
  const imageContext = vm.createContext({ chrome: { runtime: { id: extensionId, onMessage } }, HTMLImageElement: Image,
    document: { images, addEventListener(type, handler) { assert.equal(type, 'error'); resourceError = handler; } }, crypto: crypto.webcrypto, TextEncoder, URL });
  vm.runInContext(fs.readFileSync(path.join(root, 'images.js'), 'utf8'), imageContext);
  images.forEach(target => resourceError({ target }));
  const message = { kind: 'routeReady', domain: 'cdn.example.com', fingerprint: crypto.createHash('sha256').update(images[0].src).digest('hex') };
  onMessage.handlers[0](message, { id: 'untrusted' }); await turn(); assert.equal(images[0].retries, 0);
  onMessage.handlers[0](message, { id: extensionId }); await turn();
  assert.deepEqual(images.map(image => image.retries), [1, 0, 0], 'Only the exact failed unsigned image retries');
  onMessage.handlers[0](message, { id: extensionId }); await turn(); assert.equal(images[0].retries, 1);
  console.log('PASS: extension identity, opt-in, URL/token filtering, network completion across eight resource types, cache/HTTP refusal exclusion, cancellation/client blocks, duplicate errors, bounded queues and exact one-time image retry.');
})().catch(error => { console.error(error); process.exitCode = 1; });
