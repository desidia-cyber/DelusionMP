DelusionMP -- Dread Delusion co-op
================================

START
  Double-click DelusionMP-Launcher.exe to open the launcher window (host/join, LAN/Steam, name,
  partner/password, and this mod's settings) and press Launch. It edits DelusionMP-Coop.ini and
  the mod config in the prepared co-op game. Keep the launcher window open while you play.

  Without the window: double-click DelusionMP-Coop.exe. It uses DelusionMP-Coop.ini (created on
  first start) for name, host/join, network, address/partner, and game path.

  First start:
    name   = how your partner sees you
    mode   = host (you start the session) or join
    network = steam (recommended, no port forwarding) or lan
    game   = your Dread Delusion install (folder containing windows_content\Dread Delusion.exe)

STEAM (identical password)
  Both players run the Steam client. Set network = steam.
  - Co-op password: enter the same password on both machines. The host opens a Steam lobby keyed
    by a hash of the password and the joiner finds it by that password (you do not need to be Steam
    friends). Use a long, unique password; only its hash is used for discovery.
  - Steam friends: leave the password empty. The joiner can leave Partner empty to join the one
    Steam friend currently playing Dread Delusion, or enter a Steam name / SteamID64.
  - Steam account/identity: "Dread Delusion" uses your Steam copy (app 1574240). "Spacewar" uses
    Valve's free test app 480 for a copy not bought on Steam; Steam will show you playing Spacewar.
  Nothing needs to be opened on your router when using Steam.

LAN
  Set network = lan. The host forwards/opens UDP port 27050 (or your chosen port). The joiner sets
  Partner address to the host's IP/hostname.

VOICE CHAT
  Proximity voice chat is built into the mod, ported from DollsMP's voice design to Unity. The
  microphone is captured in-game, sent as 20 ms PCM frames over the co-op session, jitter-buffered
  and played back at the partner's position with distance falloff. The Player page's Chat and
  voice settings are written into the mod config and take effect on the next start:
    ChatEnabled / ChatKey   text chat (T opens, Enter sends, Esc cancels)
    VoiceChat               master on/off
    VoiceMode / VoiceKey    hold the key to talk (V) or always-on with a noise gate
    VoiceRangeM             proximity range in metres; 0 = no proximity
    VoiceVolumePct          partner's volume
    VoiceMicPct             microphone gain
    VoiceAwayPct            partner's volume when away (another level/menu)

RULES / MOD SETTINGS
  The Rules tab reflects this mod's BepInEx settings (delusionmp.coop.cfg): exclusive unique shop
  items, death debuffs, max stacks, percent per stack, respawn delay and respawn at partner. In a
  session the host's values apply to both players. The Player tab holds your own look (model and
  outfit) and chat/voice settings, which are never sent to your partner.

SAVES AND GAME COPY
  The launcher prepares an isolated co-op game copy under this folder and keeps it separate from
  your normal install. The co-op save is redirected to its own folder; your single-player save is
  never changed.

LOGS
  Double-click DelusionMP-Share-Logs.cmd to collect launcher/BepInEx logs into a zip for support.

CONTENTS
  DelusionMP-Launcher.exe   settings window
  DelusionMP-Coop.exe       launcher/session runner
  DelusionMP-Coop.ini       launcher settings (created on first start)
  DelusionMP-Share-Logs.cmd collect a bug-report log bundle
  bin\dollsmp.exe           relay
  bin\dollsmp-sidecar.exe   sidecar + voice
  bin\steam_api64.dll       Steam API (pinned)
  mod\DelusionMP.dll        DelusionMP mod (latest, also installed into the co-op copy)
  tools\bepinex-payload\... BepInEx IL2CPP loader + the mod, installed into the co-op copy
