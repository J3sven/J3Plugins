# Chocobot

Native Dalamud encounter callouts powered by IINACT.

This is the foundation for a cactbot-like experience without running external ACT. It consumes IINACT `LogLine` events through the MiniParse WebSocket by default, with IPC fallback available, then evaluates JSON trigger definitions and displays native Dalamud encounter alerts. `ChangeZone` events are used only for zone state and timeline reset.

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
- `source`: `LogLine`
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
Timeline cues are supported for imported static timeline triggers, but cactbot's
stateful safe-spot solver is represented as simpler alerts until Chocobot has
native stateful trigger logic.

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
Assets/cactbot-imported-timelines.json
Assets/cactbot-import-report.md
```

It only imports static ID-based cactbot raidboss triggers that Chocobot can
represent today, plus conservative timeline data that can be matched to static
timeline entries. Imported triggers carry structured netlog metadata for event
type and IDs, while retaining raw regex patterns as a compatibility fallback.
Imported timelines sync from observed ability IDs, show the next mechanics in
the upcoming overlay, and promote imported timeline cues to live callouts when
their cue time arrives.

`Conditions.targetIsYou()` is represented as a `targetSelf` runtime check when a
static fallback callout can be derived, using IINACT's primary-player event to
avoid firing personal markers for the whole party.

Simple cactbot role/job checks such as `data.role === 'tank'` or
`data.job === 'BLU'` are imported as local player runtime checks.

Simple boolean cactbot state such as `data.foo = true` and
`condition: (data) => data.foo` is imported as silent state updates and runtime
state conditions. Paired effect state is inferred generically when cactbot
clears a boolean on `LosesEffect` and sets that same boolean true elsewhere in
the same zone. State-gated ability triggers can inherit additional matching
ability IDs from cactbot timeline entries in the same zone. Dynamic output text,
role checks, collectors, geometry solvers, and complex timeline behavior such
as jumps/resync windows are still reported or handled conservatively so missing
encounter coverage can be tackled as Chocobot grows those systems.
