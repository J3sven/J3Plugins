<img src="Assets/chocobot-icon.png" alt="Chocobot icon" width="128">

# Chocobot

Chocobot is a Dalamud plugin that shows cactbot-style encounter callouts directly in game, powered by IINACT log events.

It does not replace IINACT. IINACT still reads the combat log; Chocobot connects to IINACT, evaluates included encounter trigger data, and renders native Dalamud alerts, upcoming timeline countdowns, and optional text-to-speech.

## Requirements

- Final Fantasy XIV with Dalamud installed.
- IINACT installed, enabled, and receiving log events.
- IINACT's WebSocket server enabled. The default address is usually `ws://127.0.0.1:10501`.

## Install

1. Open Dalamud settings in game.
2. Go to `Experimental`.
3. Add this custom plugin repository:

   ```text
   https://raw.githubusercontent.com/J3sven/J3Plugins/main/repo.json
   ```

4. Save the repository list.
5. Open the Dalamud Plugin Installer.
6. Search for `Chocobot`.
7. Install and enable the plugin.

## Set Up IINACT

1. Install and enable IINACT in Dalamud if you have not already.
2. Open IINACT's settings.
3. Enable the WebSocket server.
4. Keep the server on the default local address unless you have a reason to change it.
5. Enter an instance or start an encounter so IINACT starts sending log events.

Chocobot uses the WebSocket connection by default and falls back to Dalamud IPC when configured to do so. If it does not connect, run `/chocobot` and press `Reconnect`.

## Usage

- `/chocobot` opens the settings window.
- `/chocobot test` shows a test visual alert.
- `/chocobot testtts` plays a text-to-speech test.
- `/chocobot reload` reloads trigger and timeline data.
- `/chocobot reconnect` reconnects to IINACT.
- `/chocobot on` enables callouts.
- `/chocobot off` disables callouts.

The settings window lets you enable or disable alerts, lock the overlay, toggle click-through mode, show the ready panel, enable debug details, choose WebSocket or IPC transport, reconnect to IINACT, test alerts, test TTS, and adjust alert count, opacity, and scale.

## What It Shows

- Large top-screen callouts for active mechanics.
- A ready/upcoming overlay with countdowns for pending cues and imported timelines.
- Optional spoken callouts.
- Zone-scoped encounter triggers and timeline syncs imported from cactbot data where Chocobot can represent them safely.

On Linux, Chocobot uses `spd-say`, `espeak-ng`, or `espeak` for text-to-speech. On Windows, it uses `System.Speech` when SAPI voices are available.

## Troubleshooting

If Chocobot shows no alerts:

- Make sure IINACT is installed and enabled.
- Make sure IINACT is receiving log events.
- Make sure IINACT's WebSocket server is enabled.
- Open `/chocobot` and check the connection status.
- Press `Reconnect`.
- Try the IPC transport if WebSocket is unavailable.
- Use `Test alert` to confirm the visual overlay is working.

If timeline countdowns do not appear:

- Make sure you are in a supported encounter zone.
- Start the encounter from the beginning when possible, because timelines need an observed sync event.
- Wipe or leave the instance to reset stale timeline state.

Chocobot is intentionally conservative when importing cactbot data. Static triggers, many personal-target callouts, role/job checks, simple state conditions, and static timelines are supported. Highly dynamic JavaScript logic, geometry solvers, and complex party assignment logic may be missing until native support exists.
