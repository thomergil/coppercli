# coppercli Macro Guide

Macros automate multi-step PCB milling workflows. Instead of manually navigating menus for each step, you write a simple script that guides you through the entire job.

## Quick Start

Create a file with the `.cmacro` extension:

```
# my-job.cmacro
load copper.ngc
prompt "Jog to lower-left corner of PCB"
jog
prompt "Attach probe clip and close door"
probe z
zero xyz
prompt "Remove probe clip, close door"
mill
prompt "Job complete!"
```

The workflow:
1. User jogs to XY position in the jog menu
2. `probe z` lowers spindle until it touches copper
3. `zero xyz` sets work zero and raises Z to safe height
4. `mill` runs the job

Run it:
- **Via menu:** Main Menu → Macro → select your file
- **Via command line:** `coppercli --macro my-job.cmacro`

## File Format

- One command per line
- Lines starting with `#` are comments
- Empty lines are ignored
- Strings use double quotes: `prompt "Hello world"`
- File paths can be relative (to the macro file) or absolute

## Commands

### File Operations

#### `load <filename>`
Loads a G-code file into coppercli.

```
load copper.ngc           # relative to macro location
load ~/gcode/drill.ngc    # absolute path
load "../other/file.nc"   # relative path with directory
```

### Movement

#### `jog`
Opens the interactive jog menu. Use this to manually position the machine. The macro continues when you exit the jog menu (press Escape or Back).

```
prompt "Position the spindle over the lower-left corner"
jog
```

#### `home`
Homes all axes. Waits for homing to complete before continuing.

```
home
prompt "Machine homed"
```

#### `safe`
Moves Z to the safe height (6mm above work zero). Use this before XY moves to avoid collisions.

```
safe
prompt "Now moving to next position"
```

#### `zero [axes]`
Sets work zero for specified axes. If no axes specified, zeros all (XYZ).

```
zero xyz    # zero all axes
zero z      # zero only Z
zero xy     # zero X and Y
```

#### `unlock`
Clears an alarm state. Use only when you know it's safe to continue.

```
unlock
prompt "Alarm cleared - verify machine position"
```

### Probing

#### `probe z`
Lowers the spindle until it touches the workpiece surface, then stops. The spindle stays at the touch position so you can call `zero xyz` to set work zero.

```
prompt "Attach probe clip"
probe z
zero xyz
prompt "Remove probe clip"
```

Note: `zero xyz` automatically raises Z to safe height after zeroing.

#### `probe grid`
Opens the full probe grid menu for multi-point bed leveling. Use this for warped boards that need height compensation. Requires work zero to be set first.

```
probe grid
```

#### `probe apply`
Applies collected probe grid data to the loaded G-code. Required after `probe grid` before milling.

```
probe grid
probe apply
mill
```

### Execution

#### `mill`
Runs the currently loaded G-code file. Opens the milling display with pause/resume/stop controls. The macro continues when milling completes (or is stopped).

```
load copper.ngc
mill
prompt "Milling complete!"
```

### User Interaction

#### `prompt "message"`
Displays a message and waits for the user to press Enter. Use this for steps that require human action.

```
prompt "Change to 0.8mm drill bit"
prompt "Close the enclosure door"
prompt "Verify the board is secured"
```

**Important:** Always use `prompt` before operations that could be dangerous if the user isn't ready. Never trust automatic door detection - it can be fooled with a magnet.

#### `confirm "message"`
Displays a Yes/No prompt. If the user answers No, the macro aborts.

```
confirm "Ready to start milling?"
confirm "Did you verify the tool is correct?"
```

#### `echo "message"`
Prints a message without waiting. Use for status updates.

```
echo "Starting copper isolation..."
load copper.ngc
mill
echo "Copper isolation complete"
```

### Flow Control

#### `wait idle`
Waits for the machine to reach idle state. Useful after sending commands that take time to complete.

```
home
wait idle
echo "Homing complete"
```

## Complete Example: Two-Sided PCB

```
# two-sided-pcb.cmacro
# Mills copper traces, then drills holes

# === SETUP ===
prompt "Secure PCB to bed with double-sided tape"
prompt "Jog to lower-left corner of board"
jog

# === COPPER MILLING ===
load traces.ngc
prompt "Attach probe clip"
probe z
zero xyz
prompt "Remove probe clip and CLOSE DOOR"
mill

# === DRILLING ===
load holes.ngc
prompt "Change to 0.8mm drill bit, attach probe clip"
probe z
zero z
prompt "Remove probe clip and CLOSE DOOR"
mill

prompt "Job complete! Remove board carefully."
```

## Complete Example: With Probe Grid

```
# warped-board.cmacro
# Uses probe grid for height compensation on warped boards

load copper.ngc
prompt "Secure warped PCB and jog to lower-left corner"
jog
prompt "Attach probe clip"
probe z
zero xyz

prompt "Attach probe clip for grid probing"
probe grid
probe apply

prompt "Remove probe clip and close door"
mill

prompt "Done!"
```

## Tips

### Safety First
- Always use `prompt` before `mill` to ensure the user has closed the door and is ready
- Never rely on automatic door detection - use explicit prompts
- Include prompts for tool changes to prevent accidents

### File Paths
- Relative paths are resolved from the macro file's directory
- Keep your G-code files next to your macro for portability
- Use `~/` for home directory paths

### Workflow Design
- Group related operations together
- Add clear prompts explaining what the user needs to do
- Use `echo` for progress updates that don't need confirmation

### Error Handling
- If a command fails, the macro stops and shows an error
- Press Escape at any prompt to abort the macro
- After an abort, the machine state is preserved - you can manually continue or start over

## Running Macros

### From the Menu
1. Connect to your machine (or let it auto-connect)
2. Main Menu → Macro (press 'r')
3. Browse to your .cmacro file
4. Select it to run

### From Command Line
```bash
coppercli --macro job.cmacro
coppercli --macro ~/macros/pcb-workflow.cmacro
```

The command-line mode will auto-connect using your saved connection settings, run the macro, and exit when complete.

## Troubleshooting

**"File not found"** - Check that the path is correct. Relative paths are relative to the macro file, not your current directory.

**"Not connected"** - Make sure you have saved connection settings. Run coppercli normally first to configure your connection.

**"Machine in alarm state"** - The machine hit a limit or encountered an error. Use `unlock` in your macro only if you're sure it's safe, or handle it manually.

**Macro stops unexpectedly** - Check for typos in command names. Unknown commands cause the macro to fail at parse time.
