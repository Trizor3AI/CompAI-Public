# Install CompAI

CompAI 0.53.23 is a beta for **64-bit Windows**. Start FFXIV through XIVLauncher with Dalamud enabled. If you need the launcher first, see the [official XIVLauncher setup guide](https://goatcorp.github.io/).

## Add CompAI to Dalamud

Copy this repository address. Dalamud uses it to find CompAI and its updates.

```text
https://raw.githubusercontent.com/Trizor3AI/CompAI-Public/main/pluginmaster.json
```

1. In the game chat, type `/xlsettings` and press **Enter**.
2. Open **Experimental → Custom Plugin Repositories**. If this is your first custom repository, read and acknowledge Dalamud’s notice to see the address field.
3. Paste the address into the empty row and click **+**. Make sure the new entry is enabled, click **Save** (the disk icon), then close Settings.
4. Type `/xlplugins`. Under **All Plugins**, search for **CompAI** by **Trizor3** and click **Install**. Dalamud downloads the full package, including the software CompAI uses to connect to your AI account.

## Sign in and start chatting

1. Open `/compai settings` and select **Account**. Choose **ChatGPT / Codex** or **Grok (Beta)** and click the sign-in button for that provider.
2. Finish signing in on the provider’s website. Back in the game, click **Refresh status** if the account still looks disconnected.
3. Open `/compai` and try “What are retainer ventures?” You can leave every game reader off for this. To ask about your own items or quest progress, open **Settings → Access** and enable the categories you want to use.

## Updates

Leave the CompAI repository enabled. Open `/xlplugins`, choose **Installed Plugins** and expand **CompAI**. When an update is available, click its download arrow. Hovering over it shows the version being offered. After updating, open `/compai settings` to see your installed version.

**Moving from a private test feed?** Dalamud remembers the address you installed from. Export any chats you want a backup of, uninstall the test copy through `/xlplugins`, and keep your saved data. Disable the old repository in `/xlsettings`, then follow the installation steps above to get future updates from the public feed.

## Troubleshooting

- **The game doesn’t recognise `/xlplugins`.** Check that the in-game addon is enabled in XIVLauncher, then launch the game through XIVLauncher. [XIVLauncher help](https://goatcorp.github.io/)
- **CompAI doesn’t appear in search.** Check that the saved address ends in `/main/pluginmaster.json` and its row is enabled. Close and reopen `/xlplugins`, then search under **All Plugins**.
- **Sign-in or replies fail.** Open **Settings → Account → Refresh status**. If you report the problem, include your plugin version, provider and the short error. Review anything you share publicly and keep login codes and account files private. [Report a CompAI issue](https://github.com/Trizor3AI/CompAI-Public/issues)
- **You need to reinstall.** Use the same custom repository in Dalamud. A normal reinstall kept saved chats and settings in our Windows test. **Settings → Storage** shows where your data lives if you want to back it up first.

[Back to CompAI](README.md)
