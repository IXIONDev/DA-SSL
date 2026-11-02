const params = new URLSearchParams(location.search);
const packageRoot = params.get('package') || '';
const extensionId = params.get('extensionId') || '';
const idStatus = params.get('idStatus') || '';
const command = document.body.dataset.command || 'Install.cmd';
const path = packageRoot ? `${packageRoot}\\${command}` : `[extracted package folder]\\${command}`;

function setLanguage(language) {
  const selected = 'en';
  document.documentElement.lang = selected;
  document.documentElement.dir = selected === 'fa' ? 'rtl' : 'ltr';
  const titleKey = selected === 'fa' ? 'titleFa' : 'titleEn';
  document.title = document.body.dataset[titleKey];
  document.querySelectorAll('[data-lang]').forEach(section => { section.hidden = section.dataset.lang !== selected; });
  document.querySelectorAll('[data-fa][data-en]').forEach(node => { node.textContent = node.dataset[selected]; });
  document.querySelectorAll('[data-set-lang]').forEach(button => { button.classList.toggle('active', button.dataset.setLang === selected); });
}

document.querySelectorAll('.package-path').forEach(node => { node.textContent = packageRoot || '[extracted package folder]'; });
document.querySelectorAll('.command-path').forEach(node => { node.textContent = path; });
if (extensionId) {
  document.getElementById('extension-id').textContent = extensionId;
  document.getElementById('extension-id-card').hidden = false;
  document.getElementById('copy-extension-id').addEventListener('click', async () => {
    let copied = false;
    try { await navigator.clipboard.writeText(extensionId); copied = true; }
    catch {
      const field = document.createElement('textarea'); field.value = extensionId; field.style.position = 'fixed'; field.style.opacity = '0';
      document.body.append(field); field.select(); copied = document.execCommand('copy'); field.remove();
    }
    const status = document.getElementById('copy-status');
    status.textContent = copied
      ? ('ID copied.')
      : ('Copy failed; select the ID manually.');
  });
}
if (idStatus && !extensionId) document.getElementById('id-alert')?.classList.add('show');
document.querySelectorAll('[data-set-lang]').forEach(button => button.addEventListener('click', () => setLanguage(button.dataset.setLang)));
setLanguage('en');
