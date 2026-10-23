/* No page messages, headers, body access, request rewriting or remote scripts. */
let enabled = false, port = null, busy = false, confirmed = false, nextConnect = 0;
let queue = [], timer = null, batchId = null;
let lastNativeError = '';
const requests = new Map(), pending = new Map();
const documents = new Map();
const counters = { watched: 0, errors: 0, completed: 0, ready: 0, dropped: 0, linked: 0, missingDocument: 0, missingInitiator: 0 };
const uid = () => crypto.randomUUID().replaceAll('-', '');
const types = { image: 'Image', media: 'Media', xmlhttprequest: 'XHR', main_frame: 'Document', sub_frame: 'Document', script: 'Script', stylesheet: 'Stylesheet', font: 'Font' };
const failures = new Set(['net::ERR_QUIC_PROTOCOL_ERROR', 'net::ERR_QUIC_HANDSHAKE_FAILED',
  'net::ERR_NAME_NOT_RESOLVED', 'net::ERR_NAME_RESOLUTION_FAILED', 'net::ERR_DNS_TIMED_OUT',
  'net::ERR_CONNECTION_CLOSED', 'net::ERR_CONNECTION_RESET', 'net::ERR_TIMED_OUT',
  'net::ERR_CONTENT_LENGTH_MISMATCH', 'net::ERR_INCOMPLETE_CHUNKED_ENCODING', 'net::ERR_HTTP2_PROTOCOL_ERROR']);
let tokens = 128, refill = performance.now();
function domain(url) {
  try {
    const u = new URL(url), h = u.hostname.toLowerCase();
    if (u.protocol !== 'https:' || (u.port && u.port !== '443') || u.username || u.password ||
      h.length > 253 || !/^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{1,62}$/.test(h) ||
      /(?:^|\.)(?:local|lan|home\.arpa|ts\.net|openai\.com|chatgpt\.com|oaiusercontent\.com|oaistatic\.com|sora\.com|telegram\.org|telegram\.me|tdesktop\.com|t\.me)$/.test(h)) return null;
    return h;
  } catch { return null; }
}
function schedule() { if (!timer && enabled) timer = setTimeout(flush, 30); }
function dependency(details, now) {
  // An initiator is not necessarily the top page (frames/workers exist).
  // Never infer ownership from a tab or from a previous navigation.
  if (details.type === 'main_frame' || details.type === 'sub_frame') return {};
  if (!/^(?:[a-f0-9]{32}|[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12})$/i.test(details.documentId || '')) { counters.missingDocument++; return {}; }
  const parentDomain = domain(details.initiator);
  if (!parentDomain) { counters.missingInitiator++; return {}; }
  for (const [key, value] of documents) if (now < value.at || now - value.at >= 600000) documents.delete(key);
  const key = details.documentId + ':' + parentDomain;
  let context = documents.get(key);
  if (!context) {
    if (documents.size >= 128) documents.delete(documents.keys().next().value);
    context = { id: uid(), at: now }; documents.set(key, context);
  }
  return { parentDomain, contextId: context.id };
}
function add(item) {
  if (!enabled || !port || queue.length >= 256) { counters.dropped++; return false; }
  queue.push(item); schedule(); return true;
}
function disconnect() {
  port = null; busy = false; confirmed = false; batchId = null; queue = []; requests.clear(); pending.clear(); documents.clear(); nextConnect = performance.now() + 5000;
  chrome.action.setBadgeText({ text: enabled ? '…' : '' });
}
function connect() {
  if (!enabled || port || performance.now() < nextConnect) return;
  try {
    port = chrome.runtime.connectNative('xyz.citaeris.route.feedback');
    port.onDisconnect.addListener(() => { lastNativeError = String(chrome.runtime.lastError?.message || '').slice(0, 160); disconnect(); });
    port.onMessage.addListener(reply => {
      if (!reply || reply.id !== batchId || !Array.isArray(reply.entries)) return;
      busy = false; confirmed = true; batchId = null;
      chrome.action.setBadgeText({ text: '' });
      for (const entry of reply.entries) {
        if (entry.status === 'ready' && pending.has(entry.id)) {
          const resource = pending.get(entry.id); pending.delete(entry.id); counters.ready++;
          if (resource.type === 'Image' && entry.domain === resource.domain && resource.tab >= 0)
            resource.fingerprint.then(fingerprint => chrome.tabs.sendMessage(resource.tab, { kind: 'routeReady', domain: entry.domain, fingerprint }, { frameId: resource.frame })).catch(() => {});
        } else if (['ignored', 'expired', 'finished'].includes(entry.status)) pending.delete(entry.id);
      }
      schedule();
    });
  } catch { disconnect(); }
}
function flush() {
  timer = null; if (!enabled || !port || busy || !queue.length) return;
  batchId = uid(); busy = true;
  const events = [];
  while (queue.length && events.length < 32) {
    const next = queue[0];
    if (new TextEncoder().encode(JSON.stringify({ id: batchId, events: [...events, next] })).length > 16000) break;
    events.push(queue.shift());
  }
  try { port.postMessage({ id: batchId, events }); } catch { disconnect(); }
}
const filter = { urls: ['https://*/*'], types: Object.keys(types) };
chrome.webRequest.onBeforeRequest.addListener(details => {
  if (!enabled) return; connect(); if (!port) return;
  const now = performance.now(); tokens = Math.min(128, tokens + Math.max(0, now - refill) / 25); refill = now;
  for (const [id, value] of requests) if (now - value.at >= 85000) { add({ id: value.id, kind: 'finish' }); requests.delete(id); }
  const previous = requests.get(details.requestId);
  if (previous) { add({ id: previous.id, kind: 'finish' }); requests.delete(details.requestId); }
  const host = domain(details.url), resource = types[details.type];
  if (!host || details.method !== 'GET' || details.tabId < 0 || details.incognito || requests.size >= 128 || tokens < 1) return;
  tokens--; const fingerprint = resource === 'Image' ? crypto.subtle.digest('SHA-256', new TextEncoder().encode(details.url)).then(value => Array.from(new Uint8Array(value), byte => byte.toString(16).padStart(2, '0')).join('')) : null;
  const item = { id: uid(), domain: host, type: resource, at: now, tab: details.tabId, frame: details.frameId, fingerprint };
  const relation = dependency(details, now);
  if (add({ id: item.id, kind: 'begin', domain: host, method: 'GET', resource, ...relation })) { requests.set(details.requestId, item); counters.watched++; if(relation.contextId)counters.linked++; }
}, filter);
chrome.webRequest.onCompleted.addListener(details => {
  const item = requests.get(details.requestId); if (!item) return;
  requests.delete(details.requestId);
  // Browser network completion is evidence of delivery, not semantic content
  // validity. Cache hits, redirects and HTTP refusals cannot prove a live path.
  const success = details.statusCode >= 200 && details.statusCode < 300 && details.fromCache === false;
  if (add({ id: item.id, kind: success ? 'complete' : 'finish' }) && success) counters.completed++;
}, filter);
chrome.webRequest.onErrorOccurred.addListener(details => {
  const item = requests.get(details.requestId); if (!item) return;
  requests.delete(details.requestId);
  if (!failures.has(details.error) || pending.size >= 16) { add({ id: item.id, kind: 'finish' }); return; }
  if (add({ id: item.id, kind: 'fail', error: details.error, cancelled: false })) {
    pending.set(item.id, { ...item, at: performance.now() }); counters.errors++;
  }
}, filter);
setInterval(() => {
  if (!enabled || !port || !pending.size) return;
  for (const [id, item] of pending) {
    if (performance.now() - item.at > 15000) { add({ id, kind: 'finish' }); pending.delete(id); }
    else add({ id, kind: 'poll' });
  }
}, 500);
async function setting() {
  enabled = (await chrome.storage.local.get('enabled')).enabled === true;
  if (enabled) { nextConnect = 0; connect(); } else { const old = port; port = null; old?.disconnect(); disconnect(); }
}
chrome.storage.onChanged.addListener((changes, area) => { if (area === 'local' && changes.enabled) void setting(); });
chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (sender.id !== chrome.runtime.id || sender.url !== chrome.runtime.getURL('popup.html')) return;
  if (message?.kind === 'state') respond({ enabled, connected: confirmed, lastNativeError, ...counters });
});
void setting();
