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
  const begin = chrome.webRequest.onBeforeRequest.handlers[0], fail = chrome.webRequest.onErrorOccurred.handlers[0];
  const item = (id, url, extra = {}) => ({ requestId: id, url, method: 'GET', type: 'image', tabId: 1, frameId: 0, ...extra });
  for (const url of ['https://127.0.0.1/a', 'https://printer.local/a', 'https://host.ts.net/a', 'https://chatgpt.com/a', 'http://example.com/a', 'https://u:p@example.com/a', 'https://example.com:8443/a']) begin(item(url, url));
  begin(item('post', 'https://cdn.example.com/a', { method: 'POST' }));
  begin(item('private', 'https://cdn.example.com/a', { incognito: true }));
  assert.equal(vm.runInContext('requests.size', context), 0);
  begin(item('one', 'https://cdn.example.com/a.png?token=DO_NOT_SEND'));
  vm.runInContext('flush()', context);
  assert(!JSON.stringify(posted).includes('DO_NOT_SEND') && !JSON.stringify(posted).includes('a.png'));
  assert.equal(posted[0].events[0].domain, 'cdn.example.com');
  port.onMessage.handlers[0]({ id: posted[0].id, entries: [{ id: posted[0].events[0].id, status: 'watching' }] });
  fail({ requestId: 'one', error: 'net::ERR_BLOCKED_BY_CLIENT' });
  assert.equal(vm.runInContext('counters.errors', context), 0);
  begin(item('two', 'https://cdn.example.com/b.png'));
  fail({ requestId: 'two', error: 'net::ERR_CONNECTION_CLOSED' });
  fail({ requestId: 'two', error: 'net::ERR_CONNECTION_CLOSED' });
  assert.equal(vm.runInContext('counters.errors', context), 1, 'One resource is counted once');
  for (let i = 0; i < 1000; i++) begin(item('f' + i, 'https://cdn.example.com/a.png'));
  assert(vm.runInContext('requests.size <= 128 && queue.length <= 256 && pending.size <= 16', context));
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
  console.log('PASS: extension identity, opt-in, URL/token filtering, cancellation/client blocks, duplicate errors, bounded queues and exact one-time image retry.');
})().catch(error => { console.error(error); process.exitCode = 1; });
