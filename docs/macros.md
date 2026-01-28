# coppercli Macro Guide

Macros automate multi-step workflows. Instead of navigating menus for each step, write a script that guides you through the job.

## File Format

- Extension: `.cmacro`
- One command per line
- Lines starting with `#` are comments
- Strings use double quotes: `prompt "message"`
- Paths can be relative (to macro file) or absolute (`~/` for home)

## Commands

| Command | Description |
|---------|-------------|
| `home` | Home all axes |
| `load <file>` | Load G-code file |
| `jog` | Open jog menu for manual positioning |
| `probe z` | Lower spindle until it touches surface |
| `probe grid` | Run multi-point bed leveling |
| `probe apply` | Apply probe grid to loaded G-code |
| `zero xyz` | Set work origin (all axes) |
| `zero z` | Set work origin (Z only) |
| `mill` | Run loaded G-code |
| `prompt "msg"` | Display message, wait for Enter |
| `confirm "msg"` | Yes/No prompt; No aborts macro |
| `echo "msg"` | Print message without waiting |
| `safe` | Move Z to safe height |
| `unlock` | Clear alarm state |
| `wait idle` | Wait for machine to reach idle |

## Example: Two-Stage PCB Job

This macro mills back copper traces, then drills holes with a second bit:

```
# sharkbyte.cmacro
home
load ~/save/cnc/sharkbyte_00_back.ngc
prompt "Jog to lower-left corner of PCB"
jog
prompt "Attach probe clip"
probe z
zero xyz
probe grid
prompt "Remove probe clip, close door"
mill

prompt "Change drill bit"
load ~/save/cnc/sharkbyte_01_drill.ngc
prompt "Attach probe clip"
probe z
zero z
prompt "Remove probe clip, close door"
probe apply
mill
```

### Stage 1: Back Copper

1. **home** — Start from known position
2. **load** — Load the isolation routing file
3. **jog** — User positions spindle at PCB origin
4. **probe z** — Find copper surface (clip must be attached)
5. **zero xyz** — Set work origin at this point
6. **probe grid** — Map surface height variations (clip still attached)
7. **mill** — Run with height compensation

### Stage 2: Drilling

1. **load** — Load the drill file
2. **probe z** — Re-probe with new bit (different length)
3. **zero z** — Update Z origin for new bit; X/Y unchanged
4. **probe apply** — Reuse the grid from stage 1 (same board)
5. **mill** — Drill holes

Note: `probe apply` reuses existing grid data rather than re-probing. The surface topology hasn't changed—only the Z reference needs updating for the new bit length.

## Placeholders

Macros can use `[name:file]` placeholders for files that vary between runs:

```
load [back_file:file]
mill
load [drill_file:file]
mill
```

When run from the menu, each placeholder prompts a file browser. Underscores display as spaces: `back_file` → "Back file:".

From CLI, provide values with `--name`:
```bash
coppercli --macro job.cmacro --back_file ~/back.ngc --drill_file ~/drill.ngc
```

Missing args prompt interactively—you can provide some via CLI and select others in the browser.

## Running Macros

**From menu:** Main Menu → Macro → select file

**From command line:**
```bash
coppercli --macro job.cmacro
coppercli -m ~/macros/pcb.cmacro
coppercli --macro job.cmacro --input_file ~/file.ngc
```

Command-line mode auto-connects using saved settings, runs the macro, and exits.

## Tips

- Always `prompt` before `mill` to confirm the user is ready
- Keep probe clip on through both `probe z` and `probe grid`
- After bit change, only `zero z`—preserve X/Y origin
- Use `probe apply` (not `probe grid`) for subsequent operations on the same board
