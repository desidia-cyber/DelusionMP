DelusionMP -- Dread Delusion Co-op (DLL-Only Mod)
=================================================

INSTALL (NO .EXE FILES)
  1. Open your Dread Delusion game folder, then open the `windows_content` folder
     (the folder containing `Dread Delusion.exe`).
  2. Extract ALL files from this zip directly into `windows_content` so that
     `winhttp.dll`, `doorstop_config.ini`, `dotnet\`, and `BepInEx\` sit next to
     `Dread Delusion.exe`.
  3. Launch Dread Delusion normally (from Steam or directly).
     The very first launch takes ~10-15 seconds while BepInEx generates game bindings
     (`BepInEx\interop\`); subsequent launches are instant.
     Both players must install the same DelusionMP version.

PLAYING
  1. On the Title Screen, click **Co-op** (right below Start Game).
  2. In the **Session** tab:
     - Enter your **Player Name**.
     - Choose **Steam (No Port Forward)** or **LAN (Direct IP)**.
       * Steam (Co-op Password): enter the same password on both PCs (verify the 8-char
         Lobby Code matches). You do not need to be Steam friends.
       * Steam (Friends): leave Password mode off and click **Scan Friends** or enter your
         friend's Steam name / SteamID64 (blank auto-joins the friend in Dread Delusion).
       * Steam App: use "Dread Delusion (1574240)" for Steam copies, or "Spacewar (480)"
         for non-Steam copies.
       * LAN: host binds UDP port (default 27050); joiner enters the host's IP and port.
     - Click **Host Session** (host) or **Join Session** (joiner).
  3. Click **Load Save** (or Return -> Start Game):
     - The host loads or starts a save.
     - The joiner loads any save and is synced into the host's world, story, and journal.
  4. You can also open **Co-op** from the in-game Pause Menu (Esc) at any time to check
     connection status, adjust voice/avatar/rules, or disconnect.

IN-GAME CONTROLS (configurable in Co-op -> Player & Voice)
  - T   : Text chat (Enter sends, Esc cancels)
  - V   : Push-to-talk proximity voice chat (or switch to Open Mic)
  - F8  : Teleport to partner

RULES, SAVES & BACKUPS
  - **Separate Co-op Saves** (enabled by default): while hosting or joining, saves are
    stored in `Dread Delusion MP1` so your single-player saves are never touched.
  - **Rules** tab: host rules (death debuffs, max stacks, respawn delay, respawn at partner,
    exclusive unique shop items) are synced to both players automatically.
  - **Backups & Logs** tab: create/restore/delete co-op save backups, copy your single-player
    save into the co-op save folder, or click **Export Logs (.zip)** to pack privacy-cleaned
    logs for bug reports.

UNINSTALL
  Delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `dotnet\`, and `BepInEx\`
  from `windows_content`. Your saves are untouched.
