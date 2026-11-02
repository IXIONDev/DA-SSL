# 🔐 DA SSL 0.1

DA SSL is a Chrome extension with a Windows helper for managing explicitly approved actions on SSL certificate warning pages.

> [!WARNING]
> Ignoring a certificate warning can expose your connection to interception, credential theft, or malicious content. Use DA SSL only when you trust the site and understand the risk. A certificate error may indicate an active security problem. DA SSL does not verify a website or make an unsafe connection safe.

> [!NOTE]
> **Disclaimer:** DA SSL is provided “as is”, without warranties of any kind. The authors and contributors are not responsible for loss, damage, data disclosure, or other consequences resulting from its use. You are responsible for deciding whether to proceed past a certificate warning and for complying with applicable policies and laws.

## 📥 Download and install from GitHub

1. On the GitHub repository page, select **Code → Download ZIP**. Download the repository ZIP; do not download individual files.
2. In File Explorer, right-click the downloaded ZIP and select **Extract All**. Run the setup only from the extracted folder, not from the ZIP preview.
3. Open the extracted repository folder. Keep `Install.cmd`, `Uninstall.cmd`, `Extension`, `Installer`, and `NativeHost` together in their original layout.
4. Open Chrome and go to `chrome://extensions`. Turn on **Developer mode**.
5. Select **Load unpacked** and choose the `Extension` folder inside the extracted repository. Confirm that the **DA SSL** card appears.
6. Return to the extracted repository folder and double-click `Install.cmd`. The installer opens its guide and registers the Windows helper.
7. The installer detects the extension ID automatically. If it is not detected within 30 seconds, copy the ID shown on the DA SSL card in `chrome://extensions` and follow the installer prompt.
8. When setup finishes, reload DA SSL on `chrome://extensions`.

Chrome requires you to load an unpacked extension manually. The bundled helper is self-contained; you do not need a .NET SDK to install it. Do not move or rename the extracted folder after loading the extension, because Chrome may assign a different ID when its path changes.

## 🧭 Use

- **Enable domain** stores permission for the selected HTTPS origin and may submit one explicitly approved action for the current certificate warning.
- **Run without SSL certificate** submits an action only for a current, recognized SSL error.
- **History and permissions** opens the page for reviewing extension permissions and Chrome decisions.
- Removing a domain attempts to remove the extension permission and matching Chrome decision. Chrome may close and reopen to complete removal; unsaved work can be lost.

## 🗑️ Uninstall

1. Double-click `Uninstall.cmd` in the extracted repository folder.
2. On Chrome’s extensions page, remove **DA SSL**.
3. Return to the command window and press Enter to remove the Windows helper registration and installed helper files.
4. You may then delete the extracted repository folder.

## 🛠️ Build the native helper

The project targets `net10.0-windows` and `win-x64`. To rebuild the bundled helper, install the .NET 10 SDK and run:

```powershell
dotnet publish NativeHost/NativeHost.csproj -c Release -r win-x64 --self-contained true -o NativeHost/Publish
```

The published files are included for the installer. Keep the folder structure intact when preparing a ZIP for distribution.
