const $ = (id) => document.getElementById(id);
let data = { origins: [], autoOrigins: [], chromeDecisions: [], recentBypasses: [] };
let emergencyStatus = null;
let statusKey = 'statusLoading';
let statusState = '';
let statusValues = {};
let pendingRemoval = null;

function setStatus(key, state = '', values = {}) {
  statusKey = key; statusState = state; statusValues = values;
  $('status').className = `status ${state}`;
  $('status').querySelector('span:last-child').textContent = SEM_UI.t(key, values);
}

function empty(container, key) {
  const node = document.createElement('div'); node.className = 'empty'; node.textContent = SEM_UI.t(key); container.replaceChildren(node);
}

function removeButton(origin, enabled = true) {
  const button = document.createElement('button'); button.className = 'remove-button'; button.textContent = SEM_UI.t('removeAll'); button.disabled = !enabled;
  button.addEventListener('click', () => removeAll(origin)); return button;
}

async function removeAll(origin) {
  if (!window.confirm(`${SEM_UI.t('removeConfirm')}\n\n${origin}`)) return;
  pendingRemoval = { origin, requestedAt: Date.now() };
  await chrome.storage.local.set({ pendingRemoval });
  setStatus('saving', '', { origin });
  chrome.runtime.sendMessage({ type: 'removeOriginCompletely', origin }, (result) => {
    if (chrome.runtime.lastError || !result?.ok) {
      setStatus(result?.approvalRemoved ? 'removalPartial' : 'removalFailed', 'failed', { origin, error: result?.error || chrome.runtime.lastError?.message || SEM_UI.t('noDetails') });
      pendingRemoval = null; void chrome.storage.local.remove('pendingRemoval'); void loadState(false); return;
    }
    if (result.queued) setStatus('removalQueued', 'ready', { origin });
    else {
      setStatus('removedNoDecision', 'ready', { origin });
      pendingRemoval = null; void chrome.storage.local.remove('pendingRemoval');
    }
    void loadState(false);
  });
}

function renderApproved() {
  const container = $('approved-list'); $('approval-count').textContent = String(data.origins.length);
  if (!data.origins.length) { empty(container, 'emptyApproved'); return; }
  container.replaceChildren();
  for (const origin of data.origins) {
    const item = document.createElement('article'); item.className = 'entry';
    const main = document.createElement('div'); main.className = 'entry-main';
    const label = document.createElement('span'); label.className = 'origin'; label.textContent = origin;
    const detail = document.createElement('span'); detail.className = 'badge'; detail.textContent = SEM_UI.t(data.autoOrigins.includes(origin) ? 'automatic' : 'manual');
    main.append(label, detail); item.append(main, removeButton(origin)); container.append(item);
  }
}

function renderDecisions() {
  const container = $('decision-list'); $('decision-count').textContent = String(data.chromeDecisions.length);
  if (!data.chromeDecisions.length) { empty(container, 'emptyDecisions'); return; }
  container.replaceChildren();
  for (const origin of data.chromeDecisions) {
    const item = document.createElement('article'); item.className = 'entry';
    const main = document.createElement('div'); main.className = 'entry-main';
    const label = document.createElement('span'); label.className = 'origin'; label.textContent = origin;
    const detail = document.createElement('span'); detail.className = 'badge warn'; detail.textContent = SEM_UI.t('savedInChrome');
    main.append(label, detail); item.append(main, removeButton(origin)); container.append(item);
  }
}

function renderHistory() {
  const container = $('history-list'); $('history-count').textContent = String(data.recentBypasses.length);
  if (!data.recentBypasses.length) { empty(container, 'emptyHistory'); return; }
  container.replaceChildren();
  for (const record of data.recentBypasses) {
    const item = document.createElement('article'); item.className = 'entry history-entry';
    const main = document.createElement('div'); main.className = 'entry-main';
    const label = document.createElement('span'); label.className = 'origin'; label.textContent = record.origin || SEM_UI.t('noDetails');
    const badge = document.createElement('span'); badge.className = 'badge'; badge.textContent = SEM_UI.t('submissionAccepted');
    const details = document.createElement('div'); details.className = 'history-data';
    const fields = [
      [SEM_UI.t('sslErrorLabel'), record.errorCode || SEM_UI.t('noDetails')],
      [SEM_UI.t('timeLabel'), SEM_UI.formatDate(record.at)],
      [SEM_UI.t('chromeState'), data.chromeDecisions.includes(record.origin) ? SEM_UI.t('decisionPresent') : SEM_UI.t('decisionMissing')]
    ];
    for (const [title, value] of fields) {
      const cell = document.createElement('div'); cell.className = 'data-cell';
      const fieldLabel = document.createElement('label'); fieldLabel.textContent = title;
      const fieldValue = document.createElement('span'); fieldValue.textContent = value;
      cell.append(fieldLabel, fieldValue); details.append(cell);
    }
    main.append(label, badge, details); item.append(main);
    const canRemove = data.chromeDecisions.includes(record.origin) || data.origins.includes(record.origin);
    item.append(removeButton(record.origin, canRemove)); container.append(item);
  }
}

function renderStatus() {
  if (!pendingRemoval) return false;
  if (!emergencyStatus || !['closing-chrome', 'waiting-for-chrome-close'].includes(emergencyStatus.state)) {
    clearPendingRemoval();
    return false;
  }
  if (pendingRemoval.origin !== emergencyStatus.origin || Number(emergencyStatus.at) < pendingRemoval.requestedAt) {
    clearPendingRemoval();
    return false;
  }
  const values = { origin: emergencyStatus.origin };
  if (emergencyStatus.state === 'closing-chrome' || emergencyStatus.state === 'waiting-for-chrome-close') setStatus('statusRemoving', 'blocked', values);
  else if (emergencyStatus.state === 'completed') { setStatus('statusDone', 'ready', values); clearPendingRemoval(); }
  else if (emergencyStatus.state === 'no-decision-found') { setStatus('statusNoDecision', 'ready', values); clearPendingRemoval(); }
  else if (emergencyStatus.state === 'failed' || emergencyStatus.state === 'timed-out') { setStatus('statusFailed', 'failed', values); clearPendingRemoval(); }
  else return false;
  return true;
}

function clearPendingRemoval() {
  pendingRemoval = null;
  void chrome.storage.local.remove('pendingRemoval');
}

async function loadState(showLoading = true) {
  if (showLoading) setStatus('statusLoading');
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  chrome.runtime.sendMessage({ type: 'popupState', tab }, async (result) => {
    if (chrome.runtime.lastError || !result) {
      $('connection').querySelector('span').textContent = SEM_UI.t('disconnected');
      $('connection').classList.add('offline'); setStatus('hostOffline', 'failed'); return;
    }
    $('connection').querySelector('span').textContent = SEM_UI.t(result.hostConnected ? 'connected' : 'disconnected');
    $('connection').classList.toggle('offline', !result.hostConnected);
    data = {
      origins: Array.isArray(result.origins) ? result.origins : [],
      autoOrigins: Array.isArray(result.autoOrigins) ? result.autoOrigins : [],
      chromeDecisions: Array.isArray(result.chromeDecisions) ? result.chromeDecisions : [],
      recentBypasses: Array.isArray(result.recentBypasses) ? result.recentBypasses : []
    };
    emergencyStatus = result.emergencyStatus;
    const stored = await chrome.storage.local.get('pendingRemoval');
    pendingRemoval = stored.pendingRemoval || null;
    renderApproved(); renderDecisions(); renderHistory();
    const showedRemovalStatus = emergencyStatus ? renderStatus() : false;
    if (!showedRemovalStatus && showLoading) setStatus('statusUpdated', 'ready');
  });
}

$('refresh').addEventListener('click', () => void loadState());
$('close-page').addEventListener('click', async () => {
  const tab = await chrome.tabs.getCurrent();
  if (tab?.id !== undefined) chrome.tabs.remove(tab.id); else window.close();
});
document.addEventListener('sem-ui-changed', () => {
  renderApproved(); renderDecisions(); renderHistory(); setStatus(statusKey, statusState, statusValues);
});
void SEM_UI.init().then(() => void loadState());
