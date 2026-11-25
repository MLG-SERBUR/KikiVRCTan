# Windows ChatGPT desktop wait instructions

This file applies only to ChatGPT/Codex operating the Windows desktop UI. This is not general repository guidance.

For code changes in this repository, do not build the app locally. Commit and push the change to `feed-overlay` when requested, then let GitHub Actions build and publish the portable release. Check that the workflow and release asset succeeded before asking the user to use the updater. Do not download, install, or manually copy a local build; the app's updater handles deployment.

When KikiVRCTan's **Restart to update** button is clicked, the updater closes the current process and launches the replacement. A temporarily missing window is expected and does not mean the update failed.

- Wait and refresh the app/window list about every 30 seconds for up to 2 minutes.
- Continue when KikiVRCTan returns; confirm the new version from its window title before inspecting the UI.
- Do not relaunch the old executable or report failure while that wait is in progress.
- If it has not returned after 2 minutes, report that timeout and the last observed state.
