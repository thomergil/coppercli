# coppercli Macro Guide

A macro runs a sequence of steps you would otherwise select from the menus one at a time.
It stops at each `prompt`, `confirm` and `jog` for you to act.

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

1. **home** — start from a known position
2. **load** — load the isolation routing file
3. **jog** — you position the spindle at the PCB origin
4. **probe z** — find the copper surface (the clip must be attached)
5. **zero xyz** — set the work origin at this point
6. **probe grid** — measure the surface height across the board (clip still attached)
7. **mill** — run with height correction

### Stage 2: Drilling

1. **load** — load the drill file
2. **probe z** — re-probe with the new bit, which has a different length
3. **zero z** — update the Z origin for the new bit; X and Y are unchanged
4. **probe apply** — reuse the grid from stage 1 (same board)
5. **mill** — drill the holes

`probe apply` reuses existing grid data instead of probing again. The surface has not
changed between stages; only the Z reference needs updating for the new bit length.

## Placeholders

A `[name:file]` placeholder stands for a file that differs between runs:

```
load [back_file:file]
mill
load [drill_file:file]
mill
```

From the menu, each placeholder opens a file browser. Underscores display as spaces:
`back_file` becomes "Back file:".

From the command line, pass values with `--name`:
```bash
coppercli --macro job.cmacro --back_file ~/back.ngc --drill_file ~/drill.ngc
```

A placeholder with no value on the command line opens the file browser, so you can pass
some on the command line and pick the rest in the browser.

## Running Macros

**From menu:** Main Menu → Macro → select file

**From command line:**
```bash
coppercli --macro job.cmacro
coppercli -m ~/macros/pcb.cmacro
coppercli --macro job.cmacro --input_file ~/file.ngc
```

From the command line, coppercli connects using the saved settings, runs the macro, and
exits.

## Tips

- Put a `prompt` before every `mill`, so the job waits until you are ready
- Keep the probe clip on through both `probe z` and `probe grid`
