<img src="icon.png" alt="Cursor Indicator icon" width="128">

# Cursor Indicator

A Dalamud plugin that draws configurable particle trails and a cursor ring around the in-game cursor.

## Usage

- `/cursorindicator` opens the settings window. `/cursortrail` remains as a compatibility alias.
- Toggle the trail and cursor ring independently.
- Optionally show the trail or ring only while in combat.
- Optionally hide the cursor ring until shaking the mouse reveals a large ring that homes back to the cursor.
- Adjust particle count, lifetime, size, spacing, ring size, reveal lifetime, shake sensitivity, and color.

The plugin renders through Dalamud/ImGui foreground drawing only. It does not modify game memory.
