# Release Notes

## Unreleased

- **Abandoning a tool change no longer breaks every job after it.** Stopping at a tool
  change instead of completing it left the job still believing it was paused, and the same
  job runner serves the whole session. Every run afterwards had two of its own checks
  quietly switched off: it could no longer notice a tool change, and it could no longer
  notice that it had finished. Milling the same two-tool board again stopped at the tool
  change with no prompt and sat there; a single-tool board would have cut every line and
  then never reported itself done. Nothing announced a problem, because as far as the
  software was concerned there wasn't one. Whether a job is paused is now read from the
  job's own state rather than tracked separately alongside it, and everything describing a
  run is cleared before the next one starts, however the last one ended.
- **A tool change no longer trusts the previous one's measurements.** The height the tool
  setter was last found at was kept for the rest of the session and used to drive a rapid
  approach. Re-home the machine, move the setter, or fit a longer tool, and that approach
  aimed at a height that no longer existed. Worse, within a single tool change that
  remembered height came from the tool being replaced, so fitting a longer one drove it
  into the setter at rapid speed. The shortcut is gone; every probe now seeks from where
  the tool actually is.
- **Stopping at a tool change is reported as stopped, not failed.** Whether abandoning one
  was reported as an ordinary stop or as a failure with an error came down to the moment
  the keypress landed.
- **A job whose tool change comes early now starts.** A job that reached its first tool
  change within a moment of starting was told it had never started, because the check for
  "is this streaming yet?" could not tell a job that had already paused from one that
  never began.
- **From the browser: answering the tool-change prompt with Abort now stops the job.** It
  ended the tool change but left the job itself hanging, holding the machine and the
  screen. Resume is also refused while a tool change is still under way, instead of
  restarting the file with the spindle still moving; and aborting no longer fails outright
  when it and the job's own shutdown arrive together.
- **From the terminal: stopping at a tool change waits for the machine to settle.** It
  waited a fixed half second and carried on, which is not always long enough for the
  spindle to stop, the tool to lift, and the depth adjustment to be taken back out.
- **A pause in the file asks you what to do instead of quietly stopping.** An `M0` or `M1`
  part-way through a job stopped the stream and then nothing happened at all: no prompt,
  no message, and a progress bar frozen at whatever line it had reached, looking exactly
  like a job that had died. It now says where it paused and waits for you to continue or
  stop. An `M2` or `M30` means the program is over, so the job finishes properly — retract,
  spindle off, home — rather than sitting there.
- **A tool change always pauses.** Whether an `M6` stopped the job was quietly governed by
  the `PauseFileOnHold` setting, which is about feed holds, appears in no menu, and reads
  as unrelated. Turned off, coppercli swallowed the tool change and carried on cutting with
  the tool still in the spindle. A tool change is not a preference; it now always pauses.
  `M0`, `M1`, `M2` and `M30` still follow that setting.

## v0.4.1a

- **Typing the machine's address opens the web UI again.** v0.4.1 put a per-run token in
  the link printed at startup and refused any request without it. That worked on a
  desktop, where the link is there to click, and failed on a phone, where the address is
  typed by hand and nobody types 32 hex characters: the page loaded and then sat there
  dead, because the token guard covered the API and the WebSocket but not the page
  itself. The token is gone, and `http://192.168.1.5:34001` works from any browser on the
  network.
  What replaces it costs no keystrokes. A request is refused unless it comes from an
  address on a private network or one this machine shares a subnet with, so a forwarded
  port no longer exposes the mill to the internet. Anything that can move the machine,
  start a job, or write a file is refused unless it was issued by the UI's own page, and
  any request addressed to a domain that merely resolves to your machine is refused
  outright — that being the trick a remote page would otherwise use to slip past the
  first check. The UI also refuses to be displayed inside a frame, so no page can hide it
  under your thumb.
  Anyone sharing your network can still drive the machine — as they could before v0.4.1,
  and as the port 34000 bridge has always allowed.
- **Reach the web UI by its numeric address, or by a plain machine name.** `mill` and
  `mill.local` work; a dotted domain such as `mill.lan` or one from your router's search
  domain is now refused, because accepting those is exactly what would let a remote site
  aim a domain of its own at your machine. If you reached the UI by such a name, use the
  address printed at startup instead.
- **The estimated time remaining can go up as well as down.** It could only ever count
  down: a job running behind showed a shrinking ETA all the way to zero and then carried
  on cutting. The estimate leaned on the toolpath's own duration guess in proportion to
  how much of the job was left, and the arithmetic of that meant the measured pace could
  only raise the figure if the machine was running more than twice as slow as predicted —
  anything less, and the guess simply counted itself down. It now measures the pace the
  machine is actually keeping and projects that, so an ETA that starts at ten minutes will
  say fifteen if that is what the job is going to take, and rises if the machine slows
  part-way through. Time spent waiting on the current line is added on its own rather than
  multiplied across every line still to come, so one long cut no longer inflates the whole
  estimate.
- Build workflow: updated the GitHub Actions in use, which were pinned to versions running
  on a deprecated Node runtime.

## v0.4.1

### Safety Fixes

- **Machine-coordinate blocks are no longer turned into work-coordinate moves**: `G53`, `G10`, `G92`, `G43.1`, `G38.x`, `G28` and `G30` had their command word stripped while their axis words were left behind, so a line like `G53 G0 Z-1` — a retract to near the top of machine travel — was re-emitted as `G0 Z-1` in work coordinates: a rapid 1mm *below* the copper surface. These blocks are now preserved and sent exactly as written.
- **G-code is always written with a `.` decimal separator**: on a locale that uses a decimal comma (German, French, Dutch, Spanish and most of Europe and Latin America), every coordinate the controllers sent was formatted as e.g. `Z-1,000`. GRBL rejects that, and because nothing checked for a rejection the safety retract silently did nothing and milling continued.
- **A failed safety retract now stops the job**: retracting Z reported success whether or not the tool actually lifted. Milling and probing now refuse to make the following XY move unless the retract is confirmed.
- **Depth adjustment no longer accumulates**: re-milling the same file applied the adjustment on top of the previous run's, so two passes at −0.05mm cut 0.10mm deep while the display still read −0.05. The Z origin is now restored when a job ends.
- **Homing is no longer assumed across a reconnect**: the "machine is homed" flag was never cleared, so after a power cycle or replug milling would skip homing and run every machine-coordinate move against a coordinate system that no longer existed.
- **The enclosure door no longer auto-resumes the machine**: when GRBL reported the safety interlock open, coppercli automatically sent Cycle Start, restarting the spindle and resuming motion. Resuming after the door has been opened is now the operator's decision.
- **A tool change's length compensation survives to the end of the job**: the depth adjustment was taken back out by rewriting the Z origin to the value captured before the job started. A tool change part-way through legitimately rewrites that same origin to compensate the new tool's length, and the end-of-job restore discarded it — so the next plunge was off by the difference between the two tools. The adjustment is now taken back out relative to whatever the origin has become.
- **A move the machine made but coppercli could not model no longer deletes the file's own recovery move**: after a `G53` retract the toolpath model still believed Z was where it had been, so a following `G0 Z5` looked like a move to where the tool already was and was dropped — leaving the next cut to run at the retract depth.
- **`G28`/`G30` in a file are refused instead of run**: the parser warned "may crash into workpiece" and then let the command through to the machine, which would rapid to its stored home position across whatever is clamped to the bed. The warning stands; the block no longer reaches GRBL, and its axis words no longer become an ordinary move either.
- **Milling homes through the same code as everything else**: it had its own copy that accepted a rejected `$H` as success — the machine never moved, but the job proceeded to run `G53` moves against an origin that was never established. `CLAUDE.md` names homing as the worked example of a single source of truth; there were two.
- **A soft reset clears the homed flag**: aborting resets GRBL while motion is in flight, which is exactly when it loses the position it was tracking. The next job now homes again rather than trusting a stale origin.
- **Aborting a job commands spindle off**: the abort path relied on the reset alone. (The explicit `M5` now goes out after the reset, because ordinary commands are silently discarded while a file is streaming — which is precisely the situation an abort happens in.)
- **An open enclosure door blocks a job from starting**: readiness used to clear a Door by sending Cycle Start, resuming motion because the software decided to rather than because the operator confirmed the machine was clear.
- **Probing cannot hang with the tool down**: a probe waited forever for a reply that a rejected probe command never sends. Probes now time out.
- **Incomplete probe grids are refused**: a skipped probe point left a hole in the height map while the grid still reported 100% complete, so the map was applied and either crashed or silently used a wrong height. Completeness is now measured by what was actually probed.
- **Tool changes are detected consistently**: the serial layer and the milling controller used different rules for what counts as an `M6` line. A line like `T1 M6` was withheld from the machine but never paused the job, so it kept cutting with the previous tool.
- **Full circles are no longer dropped**: an arc that ends where it starts was deleted as a "zero-length move", silently removing drilled holes and circular isolation contours.
- **Files with two comments on a line load again**: comment stripping left the closing parenthesis behind, and the leftover made the next comment look like mismatched parentheses, failing the whole file.
- **Concurrent file loads no longer corrupt each other**: the parser accumulated into shared state, so two loads at once could produce a toolpath spliced from both files.
- **An unrecognised G-code no longer fails the whole file**: its parameter words were left behind and fell through to the motion handler. A `G64 P0.01` path-tolerance line in a pcb2gcode header — which appears before any motion command — aborted the entire load with "no motion mode active".
- **Aborting a probe or a tool change lifts the tool**: both had exit paths that returned without retracting Z, leaving the tool resting on the board or the tool setter.
- **Milling stops when the machine alarms**: the monitor loop had no alarm check and kept reporting normal progress on a machine that had already stopped.
- **The web API enforces the same pre-mill checks as the TUI**: `/api/mill/start` validated only connection and file, so a direct request could start a job with an incomplete or unapplied height map, skipping the checks the terminal UI blocks on.
- **Loading a replacement height map no longer stacks corrections**: applying a height map adds the interpolated surface to every cutting Z. Loading a second map over an already-applied one without first restoring the un-corrected G-code added both surfaces together, cutting roughly twice as deep. The web path reloaded the original file first; the terminal path did not, so the same action cut correctly from the browser and too deep from the terminal. Both now go through one loader that restores the original before the new map is applied. (Covered by a regression test.)
- **A disconnect clears the stored work zero on every path, not just one**: only the terminal's Connect/Disconnect screen reset "work zero is set" when you disconnected. Disconnecting from the main menu or the web UI left it set, so after reconnecting — where the machine may have been moved or power-cycled — milling and probing treated an origin the machine no longer holds as trusted. The reset is now centralized to the disconnect itself, mirroring how the homed flag is already cleared.

### Milling start and progress

- **Milling no longer hangs at "Idle" when a job cannot start streaming.** Completion was
  inferred from "reached the end of the file", which a job that never started never does -
  so if the file failed to begin (for instance because a probe run left the machine in
  probe mode), the controller sat idle for ever with nothing reported. The controller now
  returns the machine to a known mode before starting, confirms the stream actually began,
  and fails with a clear message if it did not.

- **The estimated time remaining is stable and sensible.** It used to be computed from
  time-elapsed-since-you-pressed-go divided by lines done - but "elapsed" included homing
  and setup, so the first figure was wildly inflated and then lurched downward. It now
  starts from the toolpath's own duration estimate, holds near that guess while the job
  gets going, and eases toward the measured pace as it progresses, landing on the truth by
  the end. The clock counts milling only, excluding setup and pauses.

### Probing crash

- **Probing no longer dies partway through a run** with "Collection was modified;
  enumeration operation may not execute". The list of points still to measure was public
  and mutable: the display enumerated it on the terminal thread while probing reordered
  and removed from it on another. Whenever a redraw coincided with a point being
  recorded — in practice after a few dozen points — the run was lost. The grid now owns
  that queue and hands out a copy, so no caller can enumerate the live list.

- **A failure during probing no longer takes the whole program down.** Cleanup called
  `Reset()` on the controller from a `finally` block; when the failure came from the
  display thread the controller was legitimately still running, so `Reset()` threw
  "Cannot reset: controller is Running" — replacing the real error with its own and
  crashing. Cleanup now stops the controller first and never throws over the original
  problem.

### Height map correctness

- **A height map now knows what it describes.** It records the file it was measured for
  and the work origin it was measured from, and that record is saved with it. Everything
  that asks "do I have probe data?" now asks the map itself, instead of inferring an
  answer from whether an autosave file happens to exist on disk.

  This fixes a family of problems with one cause. Previously: declining to trust the
  stored work origin at startup skipped the height-map question entirely, so leftover
  data stayed on disk undecided and was later announced as current; answering "no" to
  keeping a finished map did not actually discard it; loading a different file offered to
  apply the previous board's map, defaulting to yes; and a map that had already been
  applied to the toolpath was never re-checked, so moving the work origin afterwards left
  every cutting move carrying corrections measured somewhere else.

- **Milling refuses when the applied height map no longer matches** the loaded file or
  the current work origin — the most dangerous case, because the corrections are already
  baked into every move.

- **The warning when zeroing says what actually happens**: the map is deleted, including
  the saved copy, you will need to probe again, and zeroing only Z keeps it. It also only
  appears when the map genuinely describes the board in hand.

- **Startup questions come from one place.** The sequence carried over from a previous
  session — reload the file, trust the work origin, resolve a stored height map — was
  written twice, once in the terminal startup and once in the browser client, and the two
  had drifted apart. That divergence is what produced the bug above. Both interfaces now
  ask the same questions, in the same order, with the same consequences.

### Behaviour changes to be aware of

- **A machine that cannot home can no longer start a job.** Homing used to be treated as
  successful even when GRBL rejected the `$H` — which is what happens when homing is
  disabled (`$22=0`) or there are no limit switches. Milling then went ahead with machine
  coordinates that were never established, which is what every safety retract is measured
  against. That is now refused. The message names the cause the machine reported, so a
  controller with homing switched off says so and tells you which setting to change,
  rather than reporting a generic failure. There is deliberately no way to skip homing:
  without it, `G53` retracts have no reference to retract to.
- **An open enclosure door blocks a job and no longer clears itself.** Previously the
  software sent Cycle Start and carried on. Close the door and start again.
- **Tool-setter defaults changed for profiles that did not specify them**: retract
  3 → 10 mm, slow probe feed 200 → 50 mm/min, fast feed 800 → 500 mm/min. These now come
  from the same constants the code falls back to elsewhere; the two sets had drifted
  apart. Profiles that set these values explicitly (including the bundled Nomad 3) are
  unaffected.
- **Numbers and dates display in the invariant format** regardless of system locale. This
  follows from forcing G-code to use a `.` decimal separator process-wide.

### Reliability

- **The serial link no longer drops when you hit Reset mid-job**: the send queues made each operation atomic but not the check-then-take sequence around it, so a Reset arriving from the UI or web at the wrong instant threw inside the serial worker and tore down the connection — leaving GRBL to finish its buffered moves with nothing attached. The queues are now genuinely concurrent, and the byte accounting that keeps GRBL's receive buffer from overflowing is updated as one unit with the record of what was sent.
- **A failure while shutting the connection down can no longer take the whole program with it**: the teardown ran outside its own error handling on a foreground thread, so an exception there ended the process while the machine was still running.
- **Position readings are consistent**: machine position is three numbers written together but read separately, so another thread could catch X and Y from one status report and Z from the previous one. Those readings decide where the tool is told to go, and one of them was being written straight back as the Z work origin.
- **Timeouts use a monotonic clock**: the machine waits in the controller layer measured against wall-clock time, so a daylight-saving change or a clock correction could stretch a wait by an hour or expire all of them at once.
- **Settings, session and probe files are written whole**: they were written in place, so an interruption left a truncated file. The probe autosave is rewritten after every probed point, so that window came up constantly. A file that cannot be read is now set aside and reported rather than silently replaced by defaults.
- **Removed the unreachable macro-sending subsystem** inherited from OpenCNCPilot — about 180 lines in the serial worker, including an unguarded queue read, that nothing could reach.

### Code quality

- **The "don't move XY while the probe is touching the board" guard has one home.** It was written out at four call sites across the terminal and web front ends; a fifth mover could easily have been added without it, dragging the probe tip sideways across the copper. Both front ends now call a single guarded move in the command layer.
- **The web server checks "is the machine connected" one way.** The same `machine != null && connected` test was hand-copied into a dozen request handlers; consolidating it into one predicate removes the chance of a handler acting on a half-connected machine because it spelled the check differently.
- **File information reaches the browser through one serializer.** The upload, load and status responses each built the same object by hand, with the fields already drifting (the same `path` field meant the full path in one and just the filename in another). They now share one shape.
- Query-string parameter keys are named constants alongside the existing API-path and command constants, rather than string literals scattered through the request router.
- The two probe-status responses (brief and full) derive their state from one shared snapshot, so they can no longer disagree on whether a map exists or has unsaved data.
- The file browser's "where do I open" precedence (requested directory, else last used, else current) lives in one helper instead of being copied between the browse and save-location entry points.
- Removed a write-only `SkipConfirmation` option that implied the milling controller honoured a "skip the depth confirmation" flag; nothing read it. The per-start depth confirmation is a terminal-only presentation step, and the web start is gated by the server-side preflight instead — the dead flag was removed so the code no longer suggests otherwise.
- Deleted ~200 lines of unused 3-D vector geometry (cross/dot product, rotations, normalisation, angle, interpolation) inherited from OpenCNCPilot; a PCB height-map tool only ever uses the component-wise min/max and magnitude, and the compiler confirms nothing else referenced the rest.

### Security

- **The web UI now requires the access link printed at startup.** The server listens on every network interface, so previously anyone on the same network — or any web page the operator happened to visit — could start a job, jog the machine, or upload G-code. Requests without the token, and cross-site requests, are refused. Machine control over the network still works exactly as before; open the printed link. **The raw GRBL proxy on port 34000 is unchanged and still has no authentication** — see the warning in the README.
- **Zeroing accepts only X, Y and Z.** The axis list was interpolated straight into a G-code line, so a newline in it appended commands of the caller's choosing.
- **Nothing can smuggle a second command into one line**: commands built from user input and sent through `SendLine` are refused if they contain control characters or GRBL real-time bytes. (G-code streamed from a loaded file is not filtered — it is the operator's own file.)
- **Uploaded filenames cannot escape the uploads folder.**

### Testing

- **The test suite compiles and runs again.** It had not built since 2026-02-03 — the test doubles were missing three interface members added when the controller layer landed, and nothing ran `dotnet test`, so none of its ~169 tests had executed since. Added a CI workflow that builds with warnings-as-errors and runs the tests on every push, plus a test step to the release build.
- Fixed test doubles that silently discarded every `G53` command and never modelled work offsets, so retracts and offset writes appeared to succeed without being simulated.
- Added regression tests covering each safety fix above.

## v0.3.1

### New Features

- **Depth adjustment for re-milling**: During the safety confirmation before milling, press ↑/↓ to adjust cut depth by ±0.02mm (up to ±1mm). Useful for re-milling boards that didn't cut deep enough the first time.
- **Save probe data to file**: New "Save to File" option in probe menu, available when probing is complete.

### Changes

- **Disabled menu items show reasons**: Menu items now explain why they're disabled (e.g., "Jog [j] (connect first)", "Mill [m] (apply probe data first)").
- **Menu mnemonic format**: Changed from `(x)` to `[x]` for better readability.
- **Settings mnemonic**: Changed from `t` to `s`.
- Renamed "Network (TCP/IP)" to "Network" in connection menu.
- "Press Enter to continue" prompts now also accept Escape or Q.
- Declining to overwrite when saving probe data now re-prompts for a filename instead of canceling.

## v0.3.0

### Safety Improvements

- **Emergency stop (X key) now uses machine coordinates**: Previously sent `G0 Z6` in work coordinates, which could plunge the tool if work zero was set incorrectly. Now sends `G53 G0 Z-1` to retract to near top of machine travel regardless of work coordinate offset.
- **Defense in depth for coordinate systems**: All manual moves (jog presets, probe moves, tool change) now explicitly send G90 (absolute mode) before executing. Prevents dangerous behavior if G-code left machine in G91 (incremental) mode.
- **State initialization before milling**: Sends G90 G17 (absolute mode, XY plane) before starting any G-code file to establish known machine state.
- **Dangerous G-code detection**: Parser now warns about G28/G30 (home commands that may crash into workpiece) and G20 (imperial units that may cause coordinate confusion).
- **Pre-mill safety check**: If loaded file contains dangerous commands or uses imperial units, displays warnings and asks for confirmation before running. Defaults to NO.
- **Tool change uses machine coordinates**: All safety retracts during M6 tool change use G53 (machine coordinates) for predictable behavior.
- **Homing required before milling**: If machine hasn't been homed, milling will automatically home first. Without homing, machine coordinates are undefined and safety retracts could move in the wrong direction.

### New Features

- **Tool change support (M6)**: Automatic tool change handling during milling. When the G-code contains M6, coppercli pauses, guides you through the tool change, and automatically compensates for the new tool length.
  - **With tool setter**: If your machine has a tool setter (probe button), coppercli measures both tools and calculates the Z offset automatically. No need to re-zero.
  - **Without tool setter**: Prompts you to probe the PCB surface with the new tool to re-establish Z zero.
  - **M0 after M6 skipped**: If M0 (program pause) immediately follows M6 (as pcb2gcode generates), the redundant M0 is skipped. This allows coppercli to work with pcb2gcode's native tool change format without requiring `nom6=1`.
- **Machine profiles**: Select your CNC machine in Settings to auto-configure tool setter position. Built-in profiles for Carbide 3D (Nomad 3, Shapeoko), Sienci (LongMill), OpenBuilds (LEAD, MiniMill), SainSmart/Genmitsu, Inventables (X-Carve), and generic 3018/6040 machines. Add custom machines in `machine-profiles.yaml`.
- **Machine profile warning**: Main menu displays selected machine profile. If no profile is selected, shows warning in red. Before milling, displays confirmation overlay if no profile is configured.
- **Sleep prevention**: Prevents system idle sleep during milling and probing. Uses `SetThreadExecutionState` on Windows, `caffeinate` on macOS, and `systemd-inhibit` on Linux. In network mode, warns if sleep prevention is unavailable since system sleep could disconnect and leave machine in unknown state.
- **Tool setter setup**: Settings menu includes interactive jog-based setup to configure or override tool setter position.
- **Macros**: New macro system for automating repetitive workflows. Create `.cmacro` files with G-code, prompts, and comments. Access via main menu or run directly with `--macro` / `-m` command-line flag.
- **Macro placeholders**: Use `[name:file]` syntax for files that vary between runs. Prompts file browser at runtime, or pass via CLI with `--name path`.
- **File browser filter**: Press `/` to filter files by name. Type to narrow results, Backspace to edit, Esc to clear.

### Changes

- **Tool setter Y coordinate now optional**: For machines with moving beds (like Nomad 3), only the X coordinate is needed to reach the tool setter. Y can be omitted in `machine-profiles.yaml` to avoid unnecessary bed movement.
- **Feed override during milling**: Press `+` to increase feed rate 10%, `-` to decrease 10%, `0` to reset to 100%. Shows current override in status line when not 100%.
- **Vim-style jog multiplier**: Press a digit (1-5 in Fast mode, 1-9 in other modes) before a jog direction to multiply the distance. For example, in Normal mode (1mm), pressing `3→` jogs 3mm right.
- **Jog menu shows machine position**: Now displays both work and machine coordinates.
- **Jog menu key changes**: Some keys changed to support vim-style multipliers and HJKL navigation:
  - `H` → `M` for Home (H is now vim-style left)
  - `1` → `B` for go to Z+1mm
  - `6` → `T` for go to Z+6mm (retract height)
  - `X` → `N` for go to X0 Y0 (origin)
  - Added `HJKL` for vim-style X/Y jogging
- **Clearer settling message**: During milling startup, the settling overlay now shows "Waiting for idle." when the machine is still moving, instead of a static countdown that never progresses.
- **Faster settling**: Reduced post-idle settle time from 10s to 5s.
- **Proxy auto-recovery**: Serial proxy now auto-recovers after system suspend/resume by detecting unhealthy state and attempting to reconnect.
- **Connection handling**: "Port opened but no GRBL response" now auto-disconnects instead of prompting.
- **G-code compatibility**: G53, G10, G28, G30, G38.x, G43.1, G94 no longer produce parser warnings. G93 (inverse time feed rate) produces a warning since height map and time estimates assume G94.
- **Proxy no longer experimental**: Proxy mode has been tested and the [experimental] tag removed from the menu.
- **T codes parsed with comments**: Tool change commands now extract tool name from comments (e.g., `T2 (1/8" End Mill)`) for display during tool changes.
- **Tool change uses Y to confirm**: Changed from P to Y for consistency with other confirmation prompts.

### Bug Fixes

- **Proxy network disconnect**: When network connection is lost during milling via proxy, the proxy now sends soft reset (in addition to feed hold) to fully stop the machine and turn off the spindle. Previously, spindle kept spinning.
- **Mill startup Z safety**: Before starting a G-code file, coppercli now raises Z to safe height (machine coordinates) to prevent dragging across workpiece if Z was left low from previous operation.
- **Windows NuGet restore**: Added troubleshooting note to README for `NETSDK1064: Package System.IO.Ports was not found` error - run `dotnet restore` first.

## v0.2.3

### New Features

- **Network auto-detect**: Network (TCP/IP) connection menu now includes auto-detect option that scans the local network for devices. Configurable port (default 34000) and subnet mask (/16 to /24, default /24).
- **Probe color legend**: Probing display now shows a live color legend indicating Z values for the low (blue), mid (green), and high (red) colors.

### Changes

- Renamed "Traverse Outline" to "Trace Outline" for clarity.
- Renamed "Ethernet" to "Network (TCP/IP)" in connection menu.
- **Flicker-free jog menu**: Jog menu now uses in-place redraw instead of clearing the screen, eliminating flicker especially over network connections.
- **Simplified probe in jog menu**: The P (probe) command no longer prints verbose status messages; just watch the Z position update.
- **Consistent confirmation prompts**: All y/n prompts now respond immediately on keypress without requiring Enter.
- **Consistent input prompts**: All open-ended prompts now show `>` prefix via `MenuHelpers.Ask` wrapper.
- **File browser shortcuts**: Limited to 36 items (1-9, 0, A-Z). Items beyond use arrow navigation only - no more weird characters.

### Bug Fixes

- **File browser crash in small terminals**: Fixed crash when file browser had more items than fit in the terminal window. Menu now scrolls gracefully with "more above/below" indicators, and supports PageUp/PageDown/Home/End for faster navigation.
- **Auto-clear alarm on connect**: Alarm state is now silently cleared when connecting, before offering to home. Door state still prompts user to close the door.
- **Proxy safety on disconnect**: Proxy now sends feed hold (`!`) when a client disconnects, stopping any in-progress movement.
- **Menu auto-selection bug**: Fixed bug where status changes during menu display could auto-select the first menu option (e.g., auto-triggering probing).
- **Door open on boot**: Fixed error messages when connecting with door open at power-on. GRBL may boot into Alarm state (not Door state) in this scenario; the homing flow now handles both states gracefully by prompting the user to close the door before attempting to unlock.
- **Double brackets in menus**: Fixed `[[experimental]]` displaying literally instead of `[experimental]` in menu items.
- **Connection errors suppressed**: Transient "Error while Parsing Status Message" during initial connection is now suppressed.

## v0.2.2

### New Features

- **Proxy [experimental]**: New menu option to act as a serial-to-TCP bridge, allowing remote GRBL clients to connect over the network. Displays local IP addresses for easy client connection.
- **Command-line arguments**:
  - `--proxy` or `-p`: Start directly in proxy mode using saved serial settings
  - `--port <number>`: Override the default TCP port (34000) for proxy mode
  - `--headless` or `-H`: Run proxy without TUI (for services/scripts)
  - `--debug` or `-d`: Enable debug logging
- **Auto-reconnect remembers connection type**: Last successful connection type (Serial or Ethernet) is now remembered and used for auto-reconnect on startup.

### Bug Fixes

- **File browser crash with special characters**: Fixed crash when browsing directories/files containing `[` or `]` characters (Spectre.Console markup escape issue).
- **Windows installer terminal behavior**: Terminal window now closes when exiting the program instead of leaving a cmd prompt open.
- **Session restore respects rejection**: When declining to reload a G-code file on startup, it's now cleared from session so it doesn't keep asking. Last browse directory is preserved.

## v0.2.1

### New Features

- **File browser timestamps**: File browser now shows modification timestamps for each file.
- **T commands ignored**: G-code T (tool change) commands are now silently ignored instead of generating warnings.
- **Logging infrastructure**: New Logger class writes to `coppercli.log` for debugging. Off by default; enable via Settings > Toggle Debug Logging. During milling, logs TX/RX, state changes, and mode changes.
- **Overlay on map**: Hold/Alarm/Settling overlay now drawn on top of the position map instead of replacing it.
- **X=Stop in overlay**: Overlay box now shows X=Stop option.
- **Probe status visibility**: Main menu now shows whether probe points have been applied (green "applied" / yellow "not applied").

### Bug Fixes

- **Full-circle arc fix**: Always output X/Y coordinates for arc commands, fixing GRBL error:33 on helical full-circle arcs (milldrilling).
- **Resume fix**: Improved resume logic to properly distinguish between Hold state (needs CycleStart) and Manual mode after M0 (needs FileStart).
- **Filename preserved**: Fixed GCodeFile methods (Split, ArcsToLines, ApplyProbeGrid, RotateCW) to preserve filename when creating transformed copies.
