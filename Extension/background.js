const HOST = 'com.ssl.exception.manager';
const MAX_AGE_MS = 90_000;
let nativePort = null;
let nativePending = new Map();

chrome.runtime.onInstalled.addListener(() => {
  void chrome.storage.local.remove('lastTask');
});

function connectHost() {
  if (nativePort) return nativePort;
  try {
    const port = chrome.runtime.connectNative(HOST);
    nativePort = port;
    port.onMessage.addListener((message) => {
      const pending = nativePending.get(message.id);
      if (!pending) return;
      if (message.queued) {
        pending.resolve(message);
        pending.resolve = () => {};
        pending.queued = true;
      } else {
        if (!pending.queued) pending.resolve(message);
        else {
          const completed = { kind: pending.command, automatic: pending.automatic, ok: !!message.ok, origin: pending.origin || '', errorCode: pending.errorCode || '', tabId: pending.tabId ?? -1, originRemoved: !!message.originRemoved, chromeDecisionRevoked: !!message.chromeDecisionRevoked, error: message.error || '', at: Date.now() };
          void chrome.storage.local.set({ lastTask: completed });
          if (pending.command === 'executeUnsafe' && message.ok && pending.origin) {
            void chrome.storage.local.get('recentBypasses').then(({ recentBypasses = [] }) => {
              const next = [{ origin: pending.origin, errorCode: pending.errorCode || '', at: Date.now(), tabId: pending.tabId ?? -1 }, ...recentBypasses.filter(item => item.origin !== pending.origin)].slice(0, 20);
              return chrome.storage.local.set({ recentBypasses: next });
            });
            if (!pending.automatic && Number.isInteger(pending.tabId) && pending.tabId >= 0) {
              const retryNavigation = (attempt) => {
                chrome.tabs.get(pending.tabId, (tab) => {
                  const tabError = chrome.runtime.lastError;
                  if (tabError || !tab || attempt > 3) return;
                  chrome.tabs.update(pending.tabId, { url: pending.targetUrl }, () => {
                    const navigationError = chrome.runtime.lastError;
                    void chrome.storage.local.set({ lastTask: {
                      kind: 'executeUnsafe', automatic: false, ok: true, origin: pending.origin,
                      tabId: pending.tabId, navigationRequested: !navigationError,
                      navigationAttempt: attempt, navigationError: navigationError?.message || '', at: Date.now()
                    } });
                    if (!navigationError && attempt < 3) setTimeout(() => retryNavigation(attempt + 1), 1800);
                  });
                });
              };
              setTimeout(() => retryNavigation(1), 2500);
            }
          }
        }
        nativePending.delete(message.id);
      }
    });
    port.onDisconnect.addListener(() => {
      const error = chrome.runtime.lastError?.message || 'The helper connection was lost.';
      nativePort = null;
      for (const pending of nativePending.values()) pending.resolve({ ok: false, error });
      nativePending.clear();
    });
    return port;
  } catch (error) {
    nativePort = null;
    throw error;
  }
}

function nativeCall(command, fields = {}, onQueued) {
  return new Promise((resolve, reject) => {
    let port;
    try { port = connectHost(); } catch (error) { reject(error); return; }
    const id = crypto.randomUUID();
    nativePending.set(id, { command, automatic: fields.automatic === true, origin: fields.origin, errorCode: fields.errorCode, tabId: fields.tabId, targetUrl: fields.targetUrl, resolve: (message) => { if (onQueued && message.queued) onQueued(message); resolve(message); }, queued: false });
    try { port.postMessage({ id, command, ...fields }); }
    catch (error) { nativePending.delete(id); reject(error); }
  });
}

function isFresh(record) {
  return record && Number.isFinite(record.timestamp) && record.timestamp <= Date.now() && Date.now() - record.timestamp <= MAX_AGE_MS;
}

async function removeTabError(tabId) {
  const key = String(tabId);
  await chrome.storage.session.remove(key);
}

chrome.webNavigation.onBeforeNavigate.addListener(({ tabId, frameId }) => {
  if (frameId === 0 && tabId >= 0) void removeTabError(tabId);
});

chrome.webNavigation.onErrorOccurred.addListener(async (details) => {
  if (details.frameId !== 0 || details.tabId < 0 || !details.error?.startsWith('net::ERR_CERT_')) return;
  let url;
  try { url = new URL(details.url); } catch { return; }
  if (url.protocol !== 'https:') return;
  const record = { tabId: details.tabId, url: details.url, origin: url.origin, errorCode: details.error, timestamp: Date.now() };
  await chrome.storage.session.set({ [String(details.tabId)]: record });
  const { autoOrigins = [] } = await chrome.storage.local.get('autoOrigins');
  const { autoAttempts = {} } = await chrome.storage.session.get('autoAttempts');
  if (!autoOrigins.includes(record.origin)) return;
  const attemptKey = `${details.tabId}:${record.origin}`;
  if (autoAttempts[attemptKey] && Date.now() - autoAttempts[attemptKey] < MAX_AGE_MS) return;
  autoAttempts[attemptKey] = record.timestamp;
  await chrome.storage.session.set({ autoAttempts });
  try {
    const state = await nativeCall('getState', { extensionId: chrome.runtime.id });
    if (!state.ok || !state.origins?.includes(record.origin)) return;
    await nativeCall('executeUnsafe', { origin: record.origin, errorCode: record.errorCode, timestamp: record.timestamp, tabId: record.tabId, automatic: true });
  } catch { }
});

chrome.tabs.onRemoved.addListener((tabId) => { void removeTabError(tabId); });

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === 'popupState') {
    (async () => {
      const tab = message.tab;
      const tabId = Number.isInteger(tab?.id) ? tab.id : -1;
      const stored = tabId >= 0 ? (await chrome.storage.session.get(String(tabId)))[String(tabId)] : null;
      if (stored && !isFresh(stored)) await removeTabError(tabId);
      let host;
      try { host = await nativeCall('getState', { extensionId: chrome.runtime.id }); }
      catch (error) { host = { ok: false, error: error.message }; }
      const { lastTask, autoOrigins = [], recentBypasses = [] } = await chrome.storage.local.get(['lastTask', 'autoOrigins', 'recentBypasses']);
      sendResponse({ record: isFresh(stored) ? stored : null, origins: host.ok ? host.origins : [], chromeDecisions: host.ok ? host.chromeDecisions : [], autoOrigins, recentBypasses, emergencyStatus: host.ok ? host.emergencyStatus : null, hostConnected: !!host.ok, hostError: host.error || '', lastTask: lastTask || null });
    })();
    return true;
  }
  if (message?.type === 'addOrigin') {
    nativeCall('addOrigin', { origin: message.origin }).then(async result => {
      if (result.ok && message.automatic === true) {
        const { autoOrigins = [] } = await chrome.storage.local.get('autoOrigins');
        if (!autoOrigins.includes(message.origin)) autoOrigins.push(message.origin);
        await chrome.storage.local.set({ autoOrigins });
        result.autoEnabled = true;
      }
      sendResponse(result);
    }).catch(error => sendResponse({ ok: false, error: error.message }));
    return true;
  }
  if (message?.type === 'removeOriginCompletely') {
    (async () => {
      const origin = message.origin;
      let state;
      try { state = await nativeCall('getState', { extensionId: chrome.runtime.id }); }
      catch (error) { sendResponse({ ok: false, error: error.message }); return; }
      if (!state.ok) { sendResponse({ ok: false, error: state.error || 'Could not read helper status.' }); return; }
      const approvalExists = state.origins?.includes(origin) === true;
      const decisionExists = state.chromeDecisions?.includes(origin) === true;
      const { recentBypasses = [] } = await chrome.storage.local.get('recentBypasses');
      const wasSubmitted = recentBypasses.some(item => item.origin === origin);
      if (!approvalExists && !decisionExists && !wasSubmitted) {
        sendResponse({ ok: false, error: 'No extension permission or saved Chrome decision was found for this origin.' }); return;
      }
      let currentOrigins = state.origins || [];
      if (approvalExists) {
        const removed = await nativeCall('removeOriginOnly', { origin });
        if (!removed.ok || !removed.originRemoved) { sendResponse({ ok: false, error: removed.error || 'Could not remove extension permission.' }); return; }
        currentOrigins = removed.origins || currentOrigins.filter(value => value !== origin);
      }
      const { autoOrigins = [] } = await chrome.storage.local.get('autoOrigins');
      await chrome.storage.local.set({ autoOrigins: autoOrigins.filter(value => value !== origin) });
      if (!decisionExists && !wasSubmitted) {
        sendResponse({ ok: true, queued: false, approvalRemoved: approvalExists, origins: currentOrigins, chromeDecisionFound: false }); return;
      }
      try {
        const queued = await nativeCall('emergencyRevoke', { origin, extensionId: chrome.runtime.id });
        sendResponse({ ...queued, approvalRemoved: approvalExists, origins: currentOrigins, chromeDecisionFound: true });
      } catch (error) {
        sendResponse({ ok: false, approvalRemoved: approvalExists, origins: currentOrigins, chromeDecisionFound: true, error: error.message });
      }
    })().catch(error => sendResponse({ ok: false, error: error.message || 'Complete removal failed.' }));
    return true;
  }
  if (message?.type === 'removeOrigin' || message?.type === 'removeOriginOnly' || message?.type === 'revokeChromeDecision') {
    const command = message.type;
    (async () => {
      let fields = { origin: message.origin };
      if (command !== 'removeOriginOnly') {
        let tab;
        try {
          if (!Number.isInteger(message.tabId)) throw new Error('The tab ID is invalid.');
          tab = await chrome.tabs.get(message.tabId);
          const [active] = await chrome.tabs.query({ active: true, windowId: tab.windowId });
          if (active?.id !== tab.id) throw new Error('The active tab changed. Retry from the same tab.');
          const currentOrigin = new URL(tab.url || '').origin;
          if (currentOrigin !== message.origin) throw new Error(`Current origin ${currentOrigin || 'unknown'} does not match ${message.origin}.`);
          fields = { ...fields, tabId: tab.id, verifiedTabOrigin: currentOrigin, timestamp: Date.now() };
        } catch (error) {
          if (command === 'revokeChromeDecision') { sendResponse({ ok: false, error: error.message || 'The active Chrome URL does not match the requested origin.' }); return; }
          const only = await nativeCall('removeOriginOnly', { origin: message.origin });
          if (only.originRemoved) {
            const { autoOrigins = [] } = await chrome.storage.local.get('autoOrigins');
            await chrome.storage.local.set({ autoOrigins: autoOrigins.filter(origin => origin !== message.origin) });
          }
          sendResponse({ ...only, chromeDecisionRevoked: false, error: error.message || 'Extension permission removed, but the tab URL could not be verified for Chrome revocation.' }); return;
        }
      }
      const result = await nativeCall(command, fields);
      if ((command === 'removeOrigin' || command === 'removeOriginOnly') && result.originRemoved) {
        const { autoOrigins = [] } = await chrome.storage.local.get('autoOrigins');
        await chrome.storage.local.set({ autoOrigins: autoOrigins.filter(origin => origin !== message.origin) });
      }
      sendResponse(result);
    })().catch(error => sendResponse({ ok: false, error: error.message }));
    return true;
  }
  if (message?.type === 'executeUnsafe') {
    (async () => {
      const record = message.record;
      const tab = message.tab;
      if (!record || !isFresh(record) || record.tabId !== tab?.id || record.origin !== message.origin) {
        sendResponse({ ok: false, error: 'The SSL error for this tab is invalid or expired.' });
        return;
      }
      let targetUrl;
      try {
        const parsed = new URL(record.url);
        if (parsed.protocol !== 'https:' || parsed.origin !== record.origin) throw new Error();
        targetUrl = parsed.href;
      } catch {
        sendResponse({ ok: false, error: 'The recorded SSL error URL is invalid.' });
        return;
      }
      try {
        await nativeCall('executeUnsafe', { origin: record.origin, errorCode: record.errorCode, timestamp: record.timestamp, tabId: record.tabId, targetUrl }, sendResponse);
      } catch (error) { sendResponse({ ok: false, error: error.message }); }
    })();
    return true;
  }
  if (message?.type === 'emergencyRevoke') {
    (async () => {
      const { recentBypasses = [] } = await chrome.storage.local.get('recentBypasses');
      let state;
      try { state = await nativeCall('getState', { extensionId: chrome.runtime.id }); }
      catch (error) { sendResponse({ ok: false, error: error.message }); return; }
      if (!recentBypasses.some(item => item.origin === message.origin) && !state.chromeDecisions?.includes(message.origin)) {
        sendResponse({ ok: false, error: 'This origin was not found in recent requests or saved Chrome exceptions.' });
        return;
      }
      try {
        const result = await nativeCall('emergencyRevoke', { origin: message.origin, extensionId: chrome.runtime.id });
        sendResponse(result);
      } catch (error) { sendResponse({ ok: false, error: error.message }); }
    })();
    return true;
  }
  return false;
});
