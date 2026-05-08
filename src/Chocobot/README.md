# Chocobot

Native Dalamud encounter callouts powered by IINACT.

This is the foundation for a cactbot-like experience without running external ACT. It consumes IINACT `LogLine` and `ChangeZone` events through the MiniParse WebSocket by default, with IPC fallback available, then evaluates JSON trigger definitions and displays native Dalamud encounter alerts.

The mascot direction is a cute robotic chocobo: small, readable, and distinct from cactbot while still making the callout role obvious.

Visual alerts are rendered by Chocobot's own overlay instead of native quest toasts. Upcoming mechanics appear in the ready overlay with countdowns; live cues use the large top-screen callout. Spoken alerts use `spd-say`/`espeak-ng`/`espeak` on Linux and Windows `System.Speech` when SAPI voices are present.

## Commands

```text
/chocobot
/chocobot test
/chocobot testtts
/chocobot reload
/chocobot reconnect
/chocobot on
/chocobot off
```

## Trigger Packs

Trigger definitions are loaded from:

```text
Assets/*.json
```

Current fields:

- `id`: unique trigger id
- `source`: `LogLine` or `ChangeZone`
- `zone`: optional zone name filter
- `pattern`: regular expression
- `info`: default text
- `alert`: alert text
- `duration`: alert duration in seconds
- `countdown`: optional seconds to show in the upcoming list before the cue becomes a live callout; use this only when the trigger event arrives before the desired callout
- `speak`: optional spoken alert toggle
- `suppress`: optional per-trigger debounce window in seconds

Named regex capture groups can be referenced in alert text as `$groupName`.
Numbered regex capture groups can be referenced as `$1`, `$2`, and so on.

The included Sophia Extreme seed pack is adapted from cactbot's
`ui/raidboss/data/03-hw/trial/sophia-ex.ts` encounter triggers. Chocobot only
uses reactive log-line triggers right now, so log-line cues fire immediately.
Timeline-only calls and cactbot's stateful safe-spot solver are represented as
simpler alerts until the native timeline/state engine exists.

## cactbot Import Flow

Regenerate the conservative cactbot import pack from a local cactbot checkout:

```text
rustc tools/chocobot_import_cactbot.rs -o /tmp/chocobot_import_cactbot
/tmp/chocobot_import_cactbot --cactbot-dir /path/to/cactbot
```

Or download the current cactbot `main` archive into a temporary directory:

```text
rustc tools/chocobot_import_cactbot.rs -o /tmp/chocobot_import_cactbot
/tmp/chocobot_import_cactbot --download
```

The importer writes:

```text
Assets/cactbot-imported-triggers.json
Assets/cactbot-import-report.md
```

It only imports static ID-based cactbot raidboss triggers that Chocobot can
represent today. Dynamic output text, role checks, state collectors, geometry
solvers, and timeline triggers are reported as skipped so missing encounter
coverage can be tackled as Chocobot grows those systems.
