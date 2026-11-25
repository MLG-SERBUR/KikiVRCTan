# Windows ChatGPT desktop wait instructions

This file applies only to ChatGPT/Codex operating the Windows desktop UI. This is not general repository guidance.

When KikiVRCTan's **Restart to update** button is clicked, the updater closes the current process and launches the replacement. A temporarily missing window is expected and does not mean the update failed.

- Wait and refresh the app/window list about every 30 seconds for up to 2 minutes.
- Continue when KikiVRCTan returns; confirm the new version from its window title before inspecting the UI.
- Do not relaunch the old executable or report failure while that wait is in progress.
- If it has not returned after 2 minutes, report that timeout and the last observed state.
