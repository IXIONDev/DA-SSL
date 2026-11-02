const $ = (id) => document.getElementById(id);
let activeTab = null;
let activeOrigin = null;
let record = null;
let origins = [];
let autoOrigins = [];
let hostConnected = false;
let statusKey = 'reading';
let statusState = '';

function updateInstallGuideAvailability(hostError = '') {
  const message = String(hostError).toLowerCase();
  const setupMissing = /native messaging host.*(not found|forbidden)/.test(message) || /access to the specified native messaging host is forbidden/.test(message);
  $('install-help-toggle').hidden = hostConnected || !setupMissing;
}

function setStatus(key, state = '', values = {}) {
  statusKey = key; statusState = state;
  $('status').className = `status ${state}`;
  $('status').querySelector('span:last-child').textContent = SEM_UI.t(key, values);
}

function updateControls(hasError = false) {
  const approved = !!activeOrigin && origins.includes(activeOrigin);
  $('add').disabled = !activeOrigin || approved || !hostConnected;
  $('execute').disabled = !hasError || !approved || !hostConnected;
}

function renderOrigins() {
  const list = $('origins'); list.replaceChildren();
  $('domain-count').textContent = String(origins.length);
  if (!origins.length) {
    const li = document.createElement('li'); li.className = 'empty'; li.textContent = SEM_UI.t('emptyAllowed'); list.append(li); return;
  }
  for (const origin of origins) {
    const li = document.createElement('li'); const row = document.createElement('div'); row.className = 'origin-row';
    const info = document.createElement('div'); info.className = 'origin-info';
    const label = document.createElement('span'); label.className = 'origin-label'; label.textContent = origin;
    const meta = document.createElement('small'); meta.textContent = SEM_UI.t(autoOrigins.includes(origin) ? 'automatic' : 'manual');
    info.append(label, meta);
    const remove = document.createElement('button'); remove.className = 'remove-button'; remove.textContent = SEM_UI.t('remove');
    remove.addEventListener('click', () => requestFullRemoval(origin));
    row.append(info, remove); li.append(row); list.append(li);
  }
}

function requestFullRemoval(origin) {
  if (!window.confirm(`${SEM_UI.t('removeConfirm')}\n\n${origin}`)) return;
  setStatus('saving');
  chrome.runtime.sendMessage({ type: 'removeOriginCompletely', origin }, (result) => {
    if (chrome.runtime.lastError || !result?.ok) {
      if (result?.approvalRemoved) {
        origins = result.origins || origins.filter(value => value !== origin);
        autoOrigins = autoOrigins.filter(value => value !== origin);
        renderOrigins();
      }
      setStatus(result?.approvalRemoved ? 'removalPartial' : 'removalFailed', 'failed', { error: result?.error || chrome.runtime.lastError?.message || SEM_UI.t('noDetails') }); return;
    }
    origins = result.origins || origins.filter(value => value !== origin);
    autoOrigins = autoOrigins.filter(value => value !== origin);
    renderOrigins();
    if (result.queued) { setStatus('removalQueued', 'ready'); window.close(); }
    else setStatus('removedNoDecision', 'ready');
  });
}

async function initialize() {
  await SEM_UI.init();
  [activeTab] = await chrome.tabs.query({ active: true, currentWindow: true });
  try { const url = new URL(activeTab?.url || ''); if (url.protocol === 'https:') activeOrigin = url.origin; } catch { }
  $('host').querySelector('span').textContent = SEM_UI.t('checking');
  $('domain').textContent = activeOrigin || SEM_UI.t('noHttps');
  chrome.runtime.sendMessage({ type: 'popupState', tab: activeTab }, (result) => {
    if (chrome.runtime.lastError || !result) {
      $('host').querySelector('span').textContent = SEM_UI.t('disconnected');
      $('host').classList.add('offline'); hostConnected = false; updateControls(false);
      updateInstallGuideAvailability();
      setStatus('hostOffline', 'failed'); return;
    }
    record = result.record;
    origins = Array.isArray(result.origins) ? result.origins : [];
    autoOrigins = Array.isArray(result.autoOrigins) ? result.autoOrigins : [];
    hostConnected = !!result.hostConnected;
    updateInstallGuideAvailability(result.hostError);
    $('host').querySelector('span').textContent = SEM_UI.t(result.hostConnected ? 'connected' : 'disconnected');
    $('host').classList.toggle('offline', !result.hostConnected);
    const hasError = !!record && record.tabId === activeTab?.id && record.origin === activeOrigin && Date.now() - record.timestamp <= 90_000;
    $('error').textContent = hasError ? SEM_UI.t(record.errorCode === 'net::ERR_CERT_REVOKED' ? 'revokedError' : 'certificateWarning') : SEM_UI.t('noError');
    updateControls(hasError); renderOrigins();
    if (!result.hostConnected) setStatus('hostOffline', 'failed');
    else if (hasError && record.errorCode === 'net::ERR_CERT_REVOKED') setStatus('revoked', 'blocked');
    else if (!hasError) setStatus('noError');
    else if (!origins.includes(activeOrigin)) setStatus('notAllowed', 'blocked');
    else setStatus('ready', 'ready');
  });
}

$('add').addEventListener('click', () => {
  if (!activeOrigin || !window.confirm(`${SEM_UI.t('allowConfirm')}\n\n${activeOrigin}`)) return;
  setStatus('saving');
  chrome.runtime.sendMessage({ type: 'addOrigin', origin: activeOrigin, automatic: true }, (result) => {
    if (chrome.runtime.lastError || !result?.ok) { setStatus('actionFailed', 'failed'); return; }
    origins = result.origins || origins; if (!autoOrigins.includes(activeOrigin)) autoOrigins.push(activeOrigin);
    renderOrigins(); updateControls(!!record && record.tabId === activeTab?.id && record.origin === activeOrigin && Date.now() - record.timestamp <= 90_000);
    if (record && record.tabId === activeTab?.id && record.origin === activeOrigin && Date.now() - record.timestamp <= 90_000) {
      setStatus('sending');
      chrome.runtime.sendMessage({ type: 'executeUnsafe', record, tab: activeTab, origin: activeOrigin }, (execution) => {
        if (chrome.runtime.lastError || !execution?.ok || !execution?.queued) {
          setStatus('allowedSaved', 'ready');
          return;
        }
        setStatus('sent', 'ready'); window.close();
      });
    } else setStatus('allowedSaved', 'ready');
  });
});

$('execute').addEventListener('click', () => {
  if (!window.confirm(SEM_UI.t('continueConfirm'))) return;
  $('execute').disabled = true; setStatus('sending');
  chrome.runtime.sendMessage({ type: 'executeUnsafe', record, tab: activeTab, origin: activeOrigin }, (result) => {
    if (chrome.runtime.lastError || !result?.ok || !result?.queued) { setStatus('actionFailed', 'failed'); updateControls(true); return; }
    setStatus('sent', 'ready'); window.close();
  });
});

$('manage').addEventListener('click', () => { chrome.tabs.create({ url: chrome.runtime.getURL('manage.html') }); window.close(); });
function openGuide(mode) {
  chrome.tabs.create({ url: chrome.runtime.getURL(`guide.html?mode=${mode}`) });
  window.close();
}
$('install-help-toggle').addEventListener('click', () => openGuide('install'));
document.addEventListener('sem-ui-changed', () => { renderOrigins(); setStatus(statusKey, statusState); });
void initialize();
