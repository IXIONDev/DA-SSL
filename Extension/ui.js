(function () {
  const text = {
    en: {
      brand: 'DA SSL', popupTitle: 'DA SSL', popupSubtitle: 'Simple access management', manageSubtitle: 'Domains and activity',
      settings: 'Settings', language: 'Language', theme: 'Theme', faName: 'English', enName: 'English', light: 'Light', dark: 'Dark',
      connection: 'Helper app', checking: 'Checking…', connected: 'Connected', disconnected: 'Disconnected',
      currentDomain: 'Current domain', sslError: 'SSL status', noHttps: 'Not an HTTPS page', noError: 'No recent error',
      installGuideButton: 'Install guide', uninstallGuideButton: 'Uninstall guide',
      guidePageTitle: 'DA SSL guide', guideSubtitle: 'Installation and removal', guideTabsLabel: 'Guide sections', installTab: 'Install guide', uninstallTab: 'Uninstall guide',
      installGuideTitle: 'Install DA SSL', uninstallGuideTitle: 'Remove DA SSL completely',
      installStep1: 'Go to the folder where you extracted the ZIP and run Install.cmd from its root. The page it opens shows the exact path.',
      installStep2: 'In Chrome, turn on Developer mode, choose Load unpacked, and select the Extension folder beside Install.cmd.',
      installStep3: 'Copy the ID below, return to the installer window, and press Enter to register the helper.',
      installStep4: 'When setup finishes, reload DA SSL at chrome://extensions.',
      installFrequencyTitle: 'Do I need to repeat this every time?',
      installFrequencyBody: 'No. Set up the helper once for this Windows account and extension ID. You do not need to repeat it when Chrome closes or reopens. Repeat setup on another computer or if the extension ID changes.',
      guideExtensionIdTitle: 'Your extension ID', guideIdHelp: 'This ID belongs to this installation. Copy it so the installer can read it from your clipboard.',
      guideReturnToInstaller: 'After copying the ID, return to the installer window. Minimize the browser if needed.',
      guideSetupFile: 'Installer file location', guideSetupPath: '[folder where you extracted the ZIP]\\Install.cmd',
      guidePathHelp: 'Install.cmd is beside the Extension folder, at the package root.',
      copyId: 'Copy ID', copySucceeded: 'ID copied.', copyFailed: 'Could not copy the ID. Select and copy it manually.',
      installOpenChrome: 'Open Chrome extensions',
      uninstallStep1: 'Double-click Uninstall.cmd in the package folder.',
      uninstallStep2: 'The Chrome extensions page opens. Find DA SSL and select Remove.',
      uninstallStep3: 'Return to the command window and press Enter to remove the helper and its Windows registration.',
      uninstallStep4: 'After it finishes, you can delete the extracted package folder.',
      uninstallGuideNote: 'This page explains removal but does not perform it. Run Uninstall.cmd from the extracted package to remove the helper app.',
      revokedError: 'Certificate revoked', certificateWarning: 'Certificate warning',
      reading: 'Reading status…', hostOffline: 'Could not connect to the helper app.', revoked: 'This site’s certificate is revoked. Continuing may be unsafe.',
      notAllowed: 'This domain is not enabled yet.', ready: 'Ready to continue once.', allowDomain: 'Enable domain',
      continueOnce: 'Run without SSL certificate', allowedDomains: 'Enabled domains', openManager: 'History and permissions',
      emptyAllowed: 'No domains enabled.', automatic: 'Automatic handling', manual: 'Only when requested', remove: 'Remove',
      removeAll: 'Remove access', notePopup: 'Removing a domain clears both the extension permission and Chrome’s saved decision.',
      allowConfirm: 'Allow the extension to try once now and act automatically on future SSL errors for this domain?',
      continueConfirm: 'This will ignore this page’s certificate warning. Continue only if you trust this domain.',
      removeConfirm: 'The extension permission and Chrome’s saved decision will be removed. If a Chrome decision exists, Chrome will close and reopen to finish. Unsaved pages may close.',
      saving: 'Saving…', sending: 'Sending request…', sent: 'Request sent.',
      allowedSaved: 'Domain enabled.', removalQueued: 'Chrome will close and reopen to remove access for {origin}.',
      removedNoDecision: 'Access removed.', removalFailed: 'Removal failed: {error}', removalPartial: 'Extension permission removed, but Chrome could not remove its decision: {error}',
      actionFailed: 'The request could not be completed.', manageTitle: 'Browser domains and decisions', managePageTitle: 'Domain manager',
      manageIntro: 'View and manage access and recent activity in one place.', closePage: 'Close this page', refresh: 'Refresh',
      statusLoading: 'Loading…', statusUpdated: 'Information updated.', statusDone: 'Removed {origin}.',
      statusRemoving: 'Chrome is closing to remove access for {origin}…', statusNoDecision: 'Removed access for {origin}; Chrome had no saved decision.',
      statusFailed: 'Removal did not finish.', approvedTitle: 'Enabled domains', approvedDesc: 'Access you granted to the extension',
      decisionsTitle: 'Chrome decisions', decisionsDesc: 'Exceptions saved in the browser', historyTitle: 'Recent activity',
      historyDesc: 'Recent continue requests', emptyApproved: 'No enabled domains.',
      emptyDecisions: 'No saved decisions found.', emptyHistory: 'No activity recorded.',
      savedInChrome: 'Saved in Chrome', submissionAccepted: 'Continue request recorded',
      sslErrorLabel: 'SSL error', timeLabel: 'Time', chromeState: 'Chrome status',
      decisionPresent: 'Exception saved', decisionMissing: 'No exception found', noDetails: 'Not recorded',
      footerManage: 'Select “Remove access” to clear a domain. Chrome will reopen automatically.',
      countLabel: 'items', hostConnectionError: 'The helper app is disconnected.'
    }
  };

  let current = { language: 'en', theme: 'light' };

  function t(key, values = {}) {
    let value = text.en[key] || key;
    for (const [name, replacement] of Object.entries(values)) value = value.replaceAll(`{${name}}`, String(replacement));
    return value;
  }

  function apply() {
    const root = document.documentElement;
    root.lang = 'en';
    root.dir = 'ltr';
    root.dataset.theme = current.theme;
    document.querySelectorAll('[data-i18n]').forEach(element => { element.textContent = t(element.dataset.i18n); });
    document.querySelectorAll('[data-i18n-title]').forEach(element => { element.title = t(element.dataset.i18nTitle); });
    document.querySelectorAll('[data-i18n-aria]').forEach(element => { element.setAttribute('aria-label', t(element.dataset.i18nAria)); });
    const titleKey = document.body.dataset.titleKey;
    if (titleKey) document.title = t(titleKey);
    for (const id of ['theme-select', 'manage-theme-select']) {
      const select = document.getElementById(id); if (select) select.value = current.theme;
    }
    document.dispatchEvent(new CustomEvent('sem-ui-changed'));
  }

  async function init() {
    const saved = await chrome.storage.local.get(['uiTheme']);
    current.language = 'en';
    current.theme = saved.uiTheme === 'dark' ? 'dark' : 'light';
    apply();
    for (const id of ['settings-toggle', 'manage-settings-toggle']) {
      const button = document.getElementById(id);
      if (!button) continue;
      button.addEventListener('click', () => {
        const panel = document.getElementById(id === 'settings-toggle' ? 'settings-panel' : 'manage-settings-panel');
        if (panel) panel.hidden = !panel.hidden;
      });
    }
    for (const id of ['theme-select', 'manage-theme-select']) {
      const select = document.getElementById(id);
      if (select) select.addEventListener('change', async () => { current.theme = select.value === 'dark' ? 'dark' : 'light'; await chrome.storage.local.set({ uiTheme: current.theme }); apply(); });
    }
    chrome.storage.onChanged.addListener((changes, area) => {
      if (area !== 'local' || !changes.uiTheme) return;
      if (changes.uiTheme) current.theme = changes.uiTheme.newValue === 'dark' ? 'dark' : 'light';
      apply();
    });
  }

  globalThis.SEM_UI = { t, init, get language() { return current.language; }, formatDate(value) {
    if (!Number.isFinite(value)) return t('noDetails');
    return new Intl.DateTimeFormat('en-US', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value));
  } };
})();
