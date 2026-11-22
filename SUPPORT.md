# Help with CompAI

If CompAI is missing from the plugin installer or you’re setting it up for the first time, start with the [install guide](INSTALL.md). For a problem after installation, try the steps below.

## Try this first

| What happened? | What to try |
| --- | --- |
| A reply is stuck | Click **Cancel** and wait for it to finish stopping. Send the question again when you’re ready. |
| Sign-in or allowance is unavailable | Open `/compai settings` → **Account** and click **Refresh status**. Follow the sign-in instructions if asked. If the allowance still shows **unavailable**, the amount remaining is unknown. |
| CompAI can’t find an item, balance or menu | Check the matching permission in **Settings → Access**. Hover over its status for help. For a reader marked **Open to refresh**, open the relevant game menu and let it load before asking again. Saved observations can expire. |
| The provider says you’ve reached a limit | Check when the provider says your allowance resets, or follow the instructions on its account page. |
| CompAI reports missing files or a runtime mismatch | Reinstall the full package through `/xlplugins`. The plugin and its `companion` folder need to come from the same release. The [installation steps](INSTALL.md) show which repository to use. |
| A chat can’t be saved | Keep CompAI open. Select the affected chat and click **Export this chat** to keep a copy. **Settings → Storage** shows where your data lives. Check that your disk has space. |

Once any unsaved chats are safe, cancel active requests and try disabling and re-enabling **CompAI** in `/xlplugins`. If it still won’t load, leave it disabled and report the short error.

## Report a problem

Use [CompAI’s GitHub issues](https://github.com/Trizor3AI/CompAI-Public/issues). Include these details where you can.

- Your CompAI version and whether the problem started after installing or updating. You can find the version at the top of `/compai settings`.
- What you did, what you expected and what happened instead.
- The short error message. Use **Copy error** in **Settings → Account** if it appears, or open the failed request’s details in chat.
- The provider and model if the problem involves a reply or sign-in. You can leave out your account name and email address.
- **Settings → Diagnostics → Copy support summary** copies versions and status information to your clipboard. You choose whether to paste it into the report. It excludes chat text, account labels, character identifiers and local paths.

For a slow reply, **Copy measurements** on the same Diagnostics tab gives timings and token counts after the request finishes or fails. If CompAI won’t open at all, the version and short Dalamud error are enough to start.

GitHub issues are public. Only copy a **Reader diagnostic** if someone asks for it, with the relevant menu open. Read it first and crop any screenshots. They can include character names, visible game text or conversations. Keep passwords, sign-in codes, tokens, account files, full configuration folders and unreviewed logs private.

## Recover after an update

1. If CompAI opens, check for a save warning and export any important chat with unsaved changes.
2. Cancel any active requests. If you’re backing up or restoring files manually, find the folders in **Settings → Storage**, then close FFXIV normally before copying. Keep backups private because they can contain chats and game information.
3. For a normal installation, reinstall CompAI through `/xlplugins` using the same repository. Keep your saved-data folders and the **Private provider profile** folder shown in **Settings → Storage**. Older installations may have `Inspector` in that folder’s path. It can still hold your sign-in.
4. Open CompAI again and check your settings and chats. If the update still fails, leave the plugin disabled and report the version and short error. For the available versions and their release notes, see [CompAI releases](https://github.com/Trizor3AI/CompAI-Public/releases).

A normal uninstall and reinstall kept saved chats and settings in our Windows test. Keep a backup before manual recovery. **Reset plugin data and reload** can erase saved data. Use it only if you intend to start over and have kept anything you want to save. To revoke provider-side access, use the provider’s account website.

If you installed a development build manually, use a complete matching plugin folder, including `companion`. See the [build guide](BUILDING.md).
