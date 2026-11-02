const $ = (id) => document.getElementById(id);
function updateTitle() { document.title = `${SEM_UI.t('installGuideTitle')} — DA SSL`; }

async function copyId() {
  const id = chrome.runtime.id;
  $('extension-id').textContent = id;
  try {
    await navigator.clipboard.writeText(id);
    $('copy-status').textContent = SEM_UI.t('copySucceeded');
  } catch {
    const field = document.createElement('textarea');
    field.value = id; field.style.position = 'fixed'; field.style.opacity = '0';
    document.body.append(field); field.select();
    const copied = document.execCommand('copy'); field.remove();
    $('copy-status').textContent = SEM_UI.t(copied ? 'copySucceeded' : 'copyFailed');
  }
}

function openChromeExtensions() {
  chrome.tabs.create({ url: 'chrome://extensions/' });
}

async function initializeGuide() {
  await SEM_UI.init();
  $('extension-id').textContent = chrome.runtime.id;
  $('package-path').textContent = SEM_UI.t('guideSetupPath');
  try {
    const response = await fetch(chrome.runtime.getURL('install-location.json'), { cache: 'no-store' });
    const location = await response.json();
    if (typeof location.installCmdPath === 'string' && location.installCmdPath) $('package-path').textContent = location.installCmdPath;
  } catch { }
  updateTitle();
  $('copy-id').addEventListener('click', () => void copyId());
  $('open-chrome').addEventListener('click', openChromeExtensions);
  document.addEventListener('sem-ui-changed', updateTitle);
}

void initializeGuide();
