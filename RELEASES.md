# Release Notes

## v0.5.0

**Everywhere**

- At startup and after each G-code load, coppercli asks "Apply the height map you saved for
  this file?" about the map you last saved or loaded for that file. Saving deletes the
  autosave, so until now a restart lost track of the map and the job milled without height
  correction unless you reloaded it from the Probe menu. The map must be complete, measured
  for that file, and fit the current work origin. A map saved before this version is offered
  after you load it once from the Probe menu.
- A finished mill deletes the height map measured for that file, both the autosave and the
  saved `.pgrid`, because the map describes a board that is now milled. Before, the next
  startup offered to apply it. The map stays in memory until you clear it, so you can run
  the same job again in that session.
- The browser now asks the terminal's startup questions, from the same code: reload the last
  file, keep the work origin, keep an unsaved or unfinished height map, apply a saved one.
  Before, it asked only two, in its own windows, which could return on every page load and
  be answered differently in each tab. A question now disappears once answered; "no" to the
  work origin stops it until you zero all three axes again. A startup question never covers
  one already on screen, such as whether to abort milling.
- A tool change asks about the enclosure once instead of twice. Closing the door before you
  press Continue releases the hold on that answer; a door still open when you answer is put
  to you again once you close it. The pause at an `M0` works the same way.
- A failed depth-adjustment restore is reported. coppercli writes the adjustment to the
  work origin and removes it when the run ends. If an alarm prevents that restore, the
  adjustment remains and later jobs cut too deep or too shallow by that amount;
  previously coppercli also forgot the amount.
- The height map no longer steps at the edge of the probed area. A point a fraction of a
  micron outside the grid took the highest point measured anywhere on the board; it now
  takes the nearest measured edge.
- The message shown while the machine restores from the park reads "Resuming...". It read
  "tool moving, spindle starting" on every screen, including a probe, where no spindle runs.
- The door prompt defaults to yes and clears as soon as you answer it. It stayed on screen
  through the release, which looked like the door closure not being detected.
- Stopping a single Z probe lifts in machine coordinates. An interrupted probe lifted to an
  absolute work height, and since a probe starts wherever you jogged to under whatever
  origin was last set, that lift could be a descent.
- Saving the height map keeps it. Save deleted the autosave and with it the map the job was
  using, which turned the Mill button green and cleared the job to cut with no height
  correction.
- An aborted tool change stops the machine and reports whether the tool lifted, as a stopped
  probe and a stopped mill already did. It reported only "cancelled".
- A new tool's Z origin is read back before the run continues. A rejected write left the new
  tool cutting with the old tool's length.
- Two taps of Probe Z send one probe. Both taps used to find the machine free and each sent
  a probe move.
- A probe or outline trace that fails to start no longer blocks later runs. The failure used
  to leave the machine marked busy for the rest of the session, and every later probe and
  outline trace was refused.
- Stop reaches a probe that never started. The machine used to stay marked busy until you
  restarted coppercli.
- The warning before zeroing X or Y describes the map it would discard. A grid with nothing
  measured yet was called "a complete height map" in the browser and "a partly measured" one
  in the terminal.
- The dashboard counts measured points rather than points attempted. A map with a skipped
  point no longer reads as finished, and the browser no longer offers to save a map that the
  mill is refusing at the same moment.
- The file screen offers to apply a height map that exists only in the saved copy. It said
  nothing about such a map, and Mill then refused the job because the map had not been
  applied.
- If the server refuses a take-over, the browser reports why instead of reopening the dialog.
- A connection refused because another program holds the port is reported in coppercli's
  words, not the operating system's.
- Starting with `--server` exits when the port is taken, instead of waiting for a keypress
  on an unattended machine.
- The mill's time estimate no longer starts out negative when the enclosure was open before
  the first line went out.
- The terminal reaches its main menu again. Every startup question - reload the file, trust
  the work origin, keep the height map - returned as soon as you answered yes, so the first
  question repeated indefinitely and the only way out was Escape, which quit.
- A single Z probe in the browser can be stopped. The browser reports the actual failure.
  If the enclosure opens during probing, coppercli stops the machine and lifts the tool;
  previously GRBL kept the move in its planner until the hold ended.
- Homing and Go-to buttons are refused while a probe waits at the enclosure prompt. Homing
  there changes the coordinates the rest of the run measures in, and the run used to carry
  on to the next point afterwards.
- A mill stopped at the enclosure reports that the tool may still be down, as a stopped
  probe already did. It used to report nothing.
- The height map records the machine's current work origin. Zeroing moves G54, but a map
  measured afterwards still recorded the origin from before the zero, so a second pass over
  the same board was refused as a setup that had changed.
- Cancelling a jog-screen Z probe waits for the machine to stop. It returned to the jog keys
  with the probe move still running, and soft-reset GRBL instead of using the stop every
  other cancel uses.
- The server status screen no longer crashes when a client disconnects while the screen is
  redrawing.
- The mill's elapsed time and ETA are measured on a clock that only moves forward. A
  daylight-saving change or an NTP correction moved the estimate by an hour mid-job.
- Save, Apply and Continue read the same height map. Each computed its own answer to
  whether a map exists, so Apply was hidden for a map the mill was telling you to apply.
- A failed feed-rate or depth change is reported instead of doing nothing.
- Taking the machine over holds it for the browser that asked. The browser the take-over
  disconnected used to reconnect first and get the machine back.
- Probing Z by hand no longer exits coppercli when the enclosure opens while the tool is
  descending. The probe's timeout was not caught anywhere, and the program exited with the
  tool at the workpiece.
- A run whose door wait was already canceled no longer spins. It used 100% CPU sending
  progress messages and never ended.
- Escape gets you out of an open door on the connect screen, which had no way out at all.
- The probe screen stays up while the run asks about the enclosure. The browser treated the
  question as the run having finished and returned to the dashboard mid-probe.
- A job whose final retract cannot be confirmed reports that, instead of reporting the job
  finished with the tool where it was. An outline trace does the same.
- Taking the machine over from the terminal no longer competes with the browser for the
  serial port.
- Escape at the baud rate menu keeps the rate you had, instead of setting the last one in
  the list.
- A file whose name contains a quote can be loaded. The name went straight into the list's
  markup, so the quote closed the attribute holding the path and the file could not be
  selected; an angle bracket corrupted the rest of the list.
- One connection that fails while it is being accepted no longer stops the web server.
- The mill screen reads its line count from one source, avoiding flicker between values
  updated at different rates. A reconnect no longer shows an internal phase name.
- A clock change no longer disconnects anything. Every timeout was measured against the wall
  clock, so an NTP correction or a daylight-saving change could drop the browser and the
  terminal at once, or leave a disconnected client undetected.
- The take-over prompt reads the server's answer rather than matching text inside the error
  message. Rewording that message removed the only way to reclaim a machine another browser
  holds.
- Save writes the height map on screen, not the one on disk, so a map measured for another
  board can no longer be written to your file and reported saved. Save is also no longer
  offered for a map loaded from a file: such a map has no saved copy to move, so Save failed
  after you had already chosen where to put it.
- Two probe starts arriving together leave one run. The second passed the same check while
  the first was still setting up, and whichever finished first stopped the other.
- A single Z probe counts as the machine being busy. It runs without a controller behind it,
  so every check reported the machine free while the tool was descending, and a jog or a
  second probe went through.
- A single Z probe that never touches reports that. The failure was discarded and the probe
  appeared to do nothing.
- A failed settings save is reported instead of only reaching the log. The setting looked
  changed until the next launch.
- Recovering with no saved height map reports that, rather than reporting the map was
  measured for a different file.
- Load and Upload keep their icons while they work.
- A probe paused at the enclosure stays paused. Resuming sent its moves into GRBL's planner,
  where they ran when the hold lifted; the mill already refused this, and now every job does.
- The warning before zeroing X or Y covers a height map that exists only in the saved copy.
  It asked about the map in memory, so a map you had not loaded was deleted with no warning.
- Escape ends the wait at an open door at once, instead of up to five seconds later.
- Every refusal to change the height map gives the same message, whichever button you
  pressed, and is shown as a refusal rather than a server failure.
- A work zero whose outcome has no message text is caught before release, in both the
  terminal and the browser.
- The terminal and a running job follow one door policy: which enclosure states you are
  asked about, how many refused releases are enough, and which states are waited out. The
  two used to differ on whether a stray keypress could answer the question and on whether
  Escape got you out.
- A failure shows a sentence instead of an exception. Twelve places in the terminal put a
  file offset or a type name on screen; those details go to the log now.
- The jog screen's position rows are padded to the width they draw, rather than 13
  characters wider.
- The work origin is recorded only once the machine has taken it. An alarmed, sleeping or
  disconnected machine drops a `G10 L20`; coppercli recorded the origin anyway, and for an X
  or Y zero deleted the height map and its saved copy.
- Setting up a grid, loading one from a file or recovering the saved copy is refused during
  a job, as applying and discarding already were. Loading a grid while a probe was running
  deleted the saved copy that run was writing into.
- Probing Z from the jog screen reports what became of the height map, as the other two zero
  keys do. It used to report nothing.
- Zeroing no longer asks you to reload the file when the only failure was deleting the saved
  copy. The G-code was correct, and the message reported otherwise.
- The browser reports why a height map was discarded when you load another board, as the
  terminal does.
- The browser's warning before zeroing X or Y states what it costs: the map and its saved copy
  are deleted and you have to probe again. Zeroing only Z keeps the map.
- A refused session-restore answer is reported instead of being logged and reported as done.
- The enclosure Continue button waits the full settle time. A redraw re-enabled it after one
  status update, which is shorter than a double tap.
- The enclosure message is drawn once in the browser, not in the overlay and again behind it.
- Applying, loading or discarding a height map is refused during a job, as loading a file
  already was. Discarding used to delete the saved copy and then refuse, so you were told the
  map was gone while the machine was still cutting with it; the terminal's Probe screen is
  disabled during a job for the same reason.
- Zeroing on the jog screen reports what became of the height map, and says so only when the
  map changed. It used to claim the map was re-applied even when the source file was
  missing.
- Discard+Start stops when the discard is refused, instead of starting a probe anyway.
- Zeroing X or Y discards a height map that exists only in the saved copy, which the browser
  had already warned would be invalidated.
- A macro stops if a zero leaves the height map wrong for the file, as the terminal and the
  browser already report.
- Discarding a height map reloads the original G-code before dropping the map. A failed
  reload used to report the map discarded while the corrections were still in the file the
  machine would cut; if the original file is gone, the discard is refused and reports why.
- Zeroing X or Y with no height map no longer claims one was discarded.
- Continue Probing and Discard+Start check the machine again when pressed. The menu's
  enabled state is drawn once, so a job started from the browser in between left them live.
- Zeroing in the browser reports what became of the height map, as the terminal does, and
  warns when it could not be re-applied.
- Loading a file over the web opens the path it checked. A relative name was validated
  against the browse directory and then opened from wherever coppercli was started.
- A path of just `~` no longer reports an internal error.
- A machine profile coppercli does not have is refused, instead of being stored and turning
  the tool setter off without reporting it.
- A file path naming another computer is refused by loading and saving too, not only by the
  browser.
- Settings the machine cannot work to are refused. A probe feed of zero, a negative trace
  height or a value that is not a number went into a G-code line unchecked; the terminal and
  the web interface check them now, one bad value refuses the whole change, and a
  hand-edited settings file falls back to the default for anything unusable.
- Stop on the probe screen no longer stops a milling job. With no probe running it reset the
  machine, which aborted the cut and left the job reporting that it was still running.
- Trusting the work zero from a previous session reports a refusal. It used to close the
  window and report nothing.
- The file browser stays on this computer. A path naming another machine sent the request to
  that machine's file server.
- The file a job is cutting cannot be replaced while it runs. Loading a G-code file,
  uploading one, loading a height map and restoring a session each reset the machine's place
  in the file to the start, and all four are refused during a job. Zeroing X or Y is refused
  outright, because it moves the part under the rest of the job; setting Z0 at a tool change
  still works, now leaves the file alone, and reports what became of the height map. It used
  to re-apply the map, which re-cut the whole board with the new tool.
- The door prompt comes from one place. The run raised it and each screen also worked the
  door state out and drew its own, so you saw two windows for the same question: one yellow
  with nothing to press, one blue with a y/n. Each screen now draws what the run sends it.
- The browser's door window has a Stop button. It covers the whole page, so with only
  Continue on it there was no way to abandon a job from there.
- Pressing a key twice cannot answer the next question. Answering a prompt in the terminal
  can raise the next one immediately - a tool change followed by the enclosure - and the
  second keypress went to a question you had not read; anything already typed is now
  discarded when a new prompt appears.
- The browser can no longer release a hold the job is asking about. Its Continue answers the
  job's own question; releasing the hold behind the job used to leave the job waiting for an
  answer that never came.
- A probe stopped at the enclosure shows its question in the browser again after a reload,
  as milling and tool changes already did.
- Prompts and errors are shorter, saying the same thing in fewer words.
- A tool change stops if it cannot confirm the tool lifted. It used to move in X and Y next
  regardless, with the tool possibly still down.
- Pressing R at a door hold no longer closes coppercli. It reports that the door is holding
  and the job stays paused.
- The jog controls are disabled when the machine drops off the link, as they already were
  when it needed attention.
- An outline trace asks about the enclosure before it moves, as a probe and a job do, and a
  trace refused for an unsafe height reports as failed rather than finished.
- Starting a job after opening the enclosure asks you to release the hold. Closing the door
  does not end the hold: the machine parks and waits to be resumed, and Mill used to refuse
  that state and tell you to wait for the machine to stop moving and clear an alarm, neither
  of which applied. The same prompt appears for a tool change, on the jog screen and while
  connecting, and it names which of the three door states you are in: open, closed and
  holding, or restoring from the park.
- Every screen handles an open enclosure the same way. Jog, Probe and Mill open as normal
  and show "Close the door." over the top, which returns if you open the door later, and
  once the door is closed the message becomes "Door closed. Continue?"; the web interface
  shows the same message and offers the same release. Probing used to run until the first
  safety retract and then fail with "could not confirm the tool lifted to a safe height",
  which does not mention the door, and the jog screen reported it only if you pressed a key.
- A long message fits the terminal. The enclosure message is longer than one line, and the
  overlay box cut it off at the border instead of wrapping it.
- Stopping a job at the enclosure no longer retracts the tool afterwards. The retract was
  sent while GRBL held at the door, so it sat in the planner until the hold was released and
  the tool rose as you cleared the door rather than when you pressed Stop; a stopped probe
  and a stopped tool change already handled this.
- Mill is refused while the machine is asleep, as Probe already was, and reports the reason.
- The probe screen no longer redraws hundreds of times a second at the door. With the door
  closed and the machine waiting to be resumed, the screen had nothing to wait for and
  redrew continuously until the machine moved. The mill screen did the same.
- A probe interrupted by the enclosure retracts before it carries on. The machine parks with
  the tool at probe depth and the probe touching, and the run went straight to the next
  point: an XY move that drags the probe across the board, then a probe cycle starting from
  a switch that is already made, which the machine refuses with an alarm. It now lifts to
  the safe height first.
- An alarm from the machine is written to the log with its code, as are rejected commands. A
  run stopped by an alarm reported only that the machine would not move, with nothing
  recording what the machine had sent.
- Resume reports when it will not run. Pressing Resume in the web interface while the
  machine held at the door did nothing and reported success; it now gives the reason: at the
  door, alarmed, disconnected, or not holding.
- Stopping a probe retracts the tool. However the run ends - the Stop button in the
  terminal, the web interface, or a macro - the tool now rises to the probe safe height, and
  coppercli reports when it cannot confirm it got there. Stopping used to leave the tip where
  the last descent put it, and always reported success.
- Force-disconnecting a terminal client stops the machine. Taking the machine over from the
  web interface closed the connection and left GRBL working through its buffer.
- Quitting no longer leaves the machine running. Closing the serial port does not stop GRBL,
  which works through whatever is in its buffer, so quitting the server mid-probe left the
  tool moving after the port closed; coppercli now stops the machine first, however the
  connection is dropped. A machine idle with nothing outstanding, or a port that never
  answered as GRBL, is left alone.
- Stopping a probe no longer blocks the next one. A stop could leave the controller still
  holding the machine, and every later start was refused with "Probing is already running" until
  coppercli was restarted. A run now always ends, and starting or stopping returns the
  controller to idle whatever the last run left behind; milling had the same fault.
- A height map you have not applied stops the job. A finished map in the autosave that had
  not been loaded left milling cleared to run with no height correction while the probe
  screen said "complete"; the mill check, Save and Apply now read the same map the screen
  shows.
- Probing is refused without a work zero, as it already was in the terminal. Grid positions
  are work coordinates, so probing from an unset origin drives the tool to arbitrary XY.
- A saved height map is checked before it is used. A `.pgrid` with impossible extents, too
  few points, a point outside its own grid, or a height that is not a number is refused
  rather than loaded and cut with.
- A point the probe could not reach is reported when it is skipped, rather than showing up
  later as a map that will not apply.
- Recovering a map measured for another job reports that, instead of a server fault.
- Discarding probe data that could not be deleted reports that, instead of reporting success
  and then showing the data again.
- An unexpected error no longer shows its exception text. A disk error, or a port another
  program held, put file paths and byte offsets on screen; the run now reports that it
  stopped and the exception goes to the log. Messages for refusals you can act on are
  unchanged.

**The web interface**

- Server mode connects to the machine at startup and stays connected while it runs. Before,
  it disconnected whenever the last browser dropped its connection, such as a phone locking
  its screen, and five minutes after a job ended with no browser open. Each disconnect made
  coppercli forget homing and work zero. A terminal on another computer can still take the
  machine over, except while a job runs, and the server connects again once that terminal
  leaves. Before, a takeover could stop a running job, and the server stayed disconnected
  until a browser opened the page.
- Server mode opens the serial port you pick in its menu, and saves it as the connection
  menu does. Before, it opened the port saved last.
- A second terminal that connects to the proxy while one is attached is told another client
  is connected. Before, its connection waited unanswered until the first terminal left.
- The pre-mill dialog asks "Probing equipment removed?" as a tick box, and Start is disabled
  until the box is ticked. Before, the question was a second dialog after Start.
- A finished probe opens the save screen, as the terminal does, and both suggest the same
  name: the G-code file's, with `.pgrid`. Saving over an existing map asks first, as the
  terminal does. Before, the browser suggested a date and replaced a file without asking.
- Starting a probe no longer reports that one is already running. The start button ran its
  handler twice on one tap, and the second call was refused by the run the first had started.
- Tracing the outline holds the probe screen. Stop is the only control that does anything
  while the tool traces the outline; everything else is disabled until the trace ends,
  including on a browser that joins or reloads part-way through.
- Tracing the outline no longer opens the probing window. A trace moves the tool around the
  board and measures nothing, so there is no progress to show.
- A second tab of the same browser no longer stops the first from receiving status. Opening
  one left the first tab able to send commands while it no longer received status updates.
- An answer to a prompt names the prompt it answers. A tool change without a tool setter
  raises two prompts in a row, and answering the first publishes the second before the reply
  arrives, so a second tap on Continue answered the "set Z0" prompt unread and the job cut on
  the previous tool's offset. An answer that names another prompt is now rejected, the button
  is disabled when tapped, and a new prompt accepts no answer until it has been on screen
  longer than a double tap.
- Starting a job reports whether it started. Mill start, probe start and the outline trace
  returned success whatever happened, so a start refused for an open door left the browser on
  a milling screen that then reported the job complete.
- A finished job releases the screen. A browser that joined or reloaded mid-job stayed locked
  to the milling screen afterwards, with the back button disabled.
- Probing from a browser keeps the computer awake, as probing from the terminal already did;
  a suspend mid-probe dropped the connection with the probe down. The milling checklist also
  carries the terminal's warning for a computer that cannot be kept awake.
- The pause button and the feed controls follow the job after a reload. They were driven only
  by events the reloading browser had already missed.
- Commands that start a move are refused while a job is driving the machine. Stop, hold,
  resume, unlock and the feed override still work, and a tool change still lets you jog to
  the surface to set Z0.
- The connection survives a busy job. Several threads wrote to the same socket at once, and
  homing blocked the socket that carries the Stop button.
- A job waiting on the operator at a tool change or a program stop counts as running, as an
  outline trace does, so a terminal cannot take the machine over under it. It counted as
  finished.
- Loading another G-code file is refused while a job is running.
- Request bodies and uploads are bounded, a wrong HTTP method is answered rather than
  ignored, and malformed input no longer drops the connection.

**Probing**

- The "probe took too long" warning is replaced by a height check. That warning timed the
  move to the next point rather than the probe itself, because the retract and the move
  before it are sent without waiting and the probe command waits for both, so any longer
  move, such as a jump to a new row, tripped it. Each measured height is now compared
  against the heights measured around it, in both directions, so a tip stopping short on
  debris is caught as well as one pushing past the surface; a bowed board still passes,
  because neighboring points stay close together however far the board moves end to end.
- A rejected height is measured again, not recorded. Continuing after the check had paused
  the run used to write the rejected reading into the map, so a height taken before the board
  was cleared became part of it and the map was reported complete.
- The tool retracts before the run pauses, and the job stops if that retract cannot be
  confirmed. The pause previously left the tip on the copper.
- The web interface reports why probing paused. Grid probing logged its errors and reported
  nothing.
- A probe started from a macro can be stopped with Escape, and is refused while probing is
  already running instead of sending its moves into that run.

**Milling**

- The enclosure prompt no longer returns as soon as it is answered. The machine reports the
  door on its status poll, so the answer was checked against a reading taken before the door
  closed. The job now waits for the machine's reading to catch up, and prompts again only if
  the door is still open.
- Pausing while a tool-change or program-stop prompt is on screen no longer ends the job.
- Stopping a run that has already stopped itself leaves the machine usable. It could leave
  the controller stuck for the rest of the session.
- Milling is offered only when the job can start. An applied height map whose work origin has
  since moved now fails that check, and the screen reports which check failed.

**Elsewhere**

- The height map is drawn in the same colors in the terminal and the browser.
- A single Z probe from the jog screen uses the operator's configured safe height and
  retract rather than defaults.
- Support for uCNC firmware is removed; coppercli targets GRBL 1.1f.

## v0.4.2

- **Stopping during a tool change no longer affects later jobs.** The job runner serves
  the whole session, and an abandoned tool change left a separate pause flag set. Later
  runs missed tool changes and completion: a two-tool job waited without a prompt, while
  a single-tool job cut every line but never finished. Pause state now comes from the
  controller, and each run clears its fields before starting.
- **Each tool probe starts from the current position.** The old tool-setter height remained
  cached across tool changes and was used for a rapid approach. Re-homing, moving the
  setter, or fitting a longer tool could send that move into the setter; even within one
  change, the cached height belonged to the tool being replaced.
- **Stopping during a tool change reports a stop.** The previous result varied with
  the timing of the keypress and could report a failure.
- **A job can pause for a tool change immediately after starting.** The old stream-start
  check treated an early pause as a job that had never started.
- **Abort in the browser's tool-change dialog stops the job.** Previously it ended only
  the tool change, leaving the job holding the machine. Resume is refused during a tool
  change, and Abort also handles a concurrent job shutdown.
- **Stopping at a tool change in the terminal waits for the machine.** The old fixed
  half-second wait could end before the spindle stopped, the tool lifted, or the depth
  adjustment was removed.
- **`M0` and `M1` show a pause prompt.** They previously stopped streaming without a
  message while the progress bar stayed on the last line. The prompt names the pause and
  asks whether to continue or stop. `M2` and `M30` now finish the program with a retract,
  spindle stop, and home.
- **`M6` always pauses for a tool change.** Previously `PauseFileOnHold`, an unrelated
  setting absent from the menu, could let the job continue with the old tool. `M0`, `M1`,
  `M2`, and `M30` still follow that setting.
- **An open enclosure shows a door prompt.** Previously a job started with the door open,
  or homing interrupted by it, waited about a minute and reported only a homing failure.
  The prompt distinguishes an open door from a closed door still holding, and asks again
  if GRBL still reports the door open. Pressing Continue releases the hold; closing the
  door alone does not resume the machine.
- **Waits end when the machine needs operator action.** Idle, height, and move-start waits
  previously ran to their timeout while the machine was held or alarmed, then reported
  a generic failure. They now stop when operator action is needed, and homing reports
  when the door interrupted it.
- **Both screens distinguish an open door from a closed door hold.** GRBL reports both
  as `Door` with different substates. Previously coppercli discarded the substate, so
  closing the door left the display unchanged. It now shows "door open" or
  "door closed — press resume".
- **Failed browser requests explain the action needed.** Previously the browser showed
  its own error text, including for Stop and Abort when the machine might still be moving.
  Those failures now say the machine may still be moving.

## v0.4.1a

- **You can open the web UI by typing its address.** v0.4.1 required a 32-character token
  in the startup link. On a phone, typing the address loaded the page but left the API and
  WebSocket unavailable. The token requirement was removed, so
  `http://192.168.1.5:34001` works from a browser on the network.
  Requests must come from a private address or an address on a subnet shared with the
  machine. Requests that move the machine, start a job, or write a file must come from
  the UI page. Requests addressed to a domain that resolves to the machine are refused,
  and other pages cannot display the UI in a frame.
  Anyone on the same network can still control the machine, including through the raw
  GRBL proxy on port 34000.
- **Reach the web UI by its numeric address, or by a plain machine name.** `mill` and
  `mill.local` work; a dotted domain such as `mill.lan` or one from your router's search
  domain is now refused, because a remote site could resolve one of its domains to your
  machine. If you reached the UI by such a name, use the
  address printed at startup instead.
- **The remaining time estimate can increase.** The old estimate decreased to zero while
  a slow job continued cutting. Weighting the toolpath estimate by work remaining meant
  measured progress could increase the estimate only when the machine ran more than twice
  as slowly as predicted. The new estimate projects measured seconds per line and rises
  when the machine slows. Time spent on the current line is added separately, so one long
  cut does not inflate the estimate for every remaining line.
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
- **Milling uses the shared homing method**: its former copy accepted a rejected `$H` as success. The job then sent `G53` moves without a known machine origin. All callers now use `MachineWait.HomeAsync`.
- **A soft reset clears the homed flag**: aborting resets GRBL while motion is in flight, which is exactly when it loses the position it was tracking. The next job now homes again rather than trusting a stale origin.
- **Aborting a job commands spindle off**: the abort path relied on the reset alone. (The explicit `M5` now goes out after the reset, because ordinary commands are silently discarded while a file is streaming — which is precisely the situation an abort happens in.)
- **An open enclosure door blocks a job from starting**: readiness used to clear a Door by sending Cycle Start, resuming motion because the software decided to rather than because the operator confirmed the machine was clear.
- **Probing cannot hang with the tool down**: a probe waited forever for a reply that a rejected probe command never sends. Probes now time out.
- **Incomplete probe grids are refused**: a skipped probe point left a hole in the height map while the grid still reported 100% complete, so the map was applied and either crashed or silently used a wrong height. Completeness is now measured by what was actually probed.
- **Tool changes are detected consistently**: the serial layer and the milling controller used different rules for what counts as an `M6` line. A line like `T1 M6` was withheld from the machine but never paused the job, so it kept cutting with the previous tool.
- **Full circles are no longer dropped**: an arc that ends where it starts was deleted as a "zero-length move", silently removing drilled holes and circular isolation contours.
- **Files with two comments on a line load again**: comment stripping left the closing parenthesis behind, and the leftover made the next comment look like mismatched parentheses, failing the whole file.
- **Concurrent file loads no longer corrupt each other**: the parser accumulated into shared state, so two loads at once could produce a toolpath spliced from both files.
- **An unrecognized G-code no longer fails the whole file**: its parameter words were left behind and fell through to the motion handler. A `G64 P0.01` path-tolerance line in a pcb2gcode header — which appears before any motion command — aborted the entire load with "no motion mode active".
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
- **The time estimate excludes setup and pauses.** The old calculation divided elapsed
  time since Start by completed lines, including homing and setup. That inflated the
  initial estimate. The estimate now starts with the toolpath duration and adjusts to
  the measured milling rate as lines complete.

### Probing crash

- **Probing no longer dies partway through a run** with "Collection was modified;
  enumeration operation may not execute". The list of points still to measure was public
  and mutable: the display enumerated it on the terminal thread while probing reordered
  and removed from it on another. Whenever a redraw coincided with a point being
  recorded — in practice after a few dozen points — the run was lost. The grid now owns
  that queue and returns a copy, so no caller can enumerate the live list.
- **A failure during probing no longer takes the whole program down.** Cleanup called
  `Reset()` on the controller from a `finally` block; when the failure came from the
  display thread the controller was legitimately still running, so `Reset()` threw
  "Cannot reset: controller is Running" — replacing the real error with its own and
  crashing. Cleanup now stops the controller first and never throws over the original
  problem.

### Height map correctness

- **A height map records its source file and work origin.** That record is saved with the
  map. Code that checks for probe data now reads the map instead of inferring an
  answer from whether an autosave file happens to exist on disk.

  Previously, declining to trust the stored work origin at startup skipped the height-map
  question entirely, so leftover data stayed on disk undecided and was later announced
  as current; answering "no" to
  keeping a finished map did not actually discard it; loading a different file offered to
  apply the previous board's map, defaulting to yes; and a map that had already been
  applied to the toolpath was never re-checked, so moving the work origin afterwards left
  every cutting move carrying corrections measured somewhere else.
- **Milling refuses when the applied height map no longer matches** the loaded file or
  the current work origin — the most dangerous case, because the corrections are already
  applied to every move.
- **The warning when zeroing says what actually happens**: the map is deleted, including
  the saved copy, you will need to probe again, and zeroing only Z keeps it. It also only
  appears when the map describes the current board.
- **Both interfaces use the same startup questions.** The sequence carried over from a previous
  session — reload the file, trust the work origin, resolve a stored height map — was
  written twice, once in the terminal startup and once in the browser client, and the two
  had drifted apart. That divergence is what produced the bug above. Both interfaces now
  ask the same questions, in the same order, with the same consequences.

### Behavior changes

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

- **The serial link stays connected after Reset during a job**: the send queues made individual operations atomic but left a race between checking and removing an item. A Reset from either UI could throw inside the serial worker while GRBL finished buffered moves. Concurrent queues now handle that race, and receive-buffer byte counts are updated with the sent-command record.
- **A failure while shutting the connection down can no longer take the whole program with it**: the teardown ran outside its own error handling on a foreground thread, so an exception there ended the process while the machine was still running.
- **Position readings are consistent**: machine position is three numbers written together but read separately, so another thread could catch X and Y from one status report and Z from the previous one. Those readings decide where the tool is told to go, and one of them was being written straight back as the Z work origin.
- **Timeouts use a monotonic clock**: the machine waits in the controller layer measured against wall-clock time, so a daylight-saving change or a clock correction could stretch a wait by an hour or expire all of them at once.
- **Settings, session and probe files are written whole**: they were written in place, so an interruption left a truncated file. The probe autosave is rewritten after every probed point, so that window came up constantly. A file that cannot be read is now set aside and reported rather than silently replaced by defaults.
- **Removed the unreachable macro-sending subsystem** inherited from OpenCNCPilot — about 180 lines in the serial worker, including an unguarded queue read, that nothing could reach.

### Code quality

- **One command checks that the probe is clear before XY motion.** The check was repeated at four call sites in the terminal and browser. Both interfaces now call the same command, which prevents an XY move while the probe touches the board.
- **The web server checks "is the machine connected" one way.** The same `machine != null && connected` test was hand-copied into a dozen request handlers; consolidating it into one predicate removes the chance of a handler acting on a half-connected machine because it spelled the check differently.
- **One serializer builds browser file data.** Upload, load, and status responses previously built separate objects. Their `path` fields had different meanings; they now use the same serializer.
- Query-string parameter keys are named constants alongside the existing API-path and command constants, rather than string literals scattered through the request router.
- The two probe-status responses (brief and full) derive their state from one shared snapshot, so they can no longer disagree on whether a map exists or has unsaved data.
- One helper chooses the file browser's starting directory: requested directory, last used directory, then current directory. The browse and save dialogs use it.
- Removed a write-only `SkipConfirmation` option that implied the milling controller honored a "skip the depth confirmation" flag; nothing read it. The per-start depth confirmation is a terminal-only presentation step, and the web start is gated by the server-side check instead — the dead flag was removed so the code no longer suggests otherwise.
- Deleted ~200 lines of unused 3-D vector geometry (cross/dot product, rotations, normalization, angle, interpolation) inherited from OpenCNCPilot. The app uses component-wise min/max and magnitude; the compiler found no references to the deleted methods.

### Security

- **The web UI now requires the access link printed at startup.** The server listens on every network interface, so previously anyone on the same network — or any web page the operator happened to visit — could start a job, jog the machine, or upload G-code. Requests without the token, and cross-site requests, are refused. Machine control over the network still works exactly as before; open the printed link. **The raw GRBL proxy on port 34000 is unchanged and still has no authentication** — see the warning in the README.
- **Zeroing accepts only X, Y and Z.** The axis list was interpolated straight into a G-code line, so a newline in it appended commands of the caller's choosing.
- **Nothing can smuggle a second command into one line**: commands built from user input and sent through `SendLine` are refused if they contain control characters or GRBL real-time bytes. (G-code streamed from a loaded file is not filtered — it is the operator's own file.)
- **Uploaded filenames cannot escape the uploads folder.**

### Testing

- **The test suite compiles and runs again.** It had not built since 2026-02-03 — the test doubles were missing three interface members added when the controller layer landed, and nothing ran `dotnet test`, so none of its ~169 tests had executed since. Added a CI workflow that builds with warnings-as-errors and runs the tests on every push, plus a test step to the release build.
- Fixed test doubles that silently discarded every `G53` command and never modeled work offsets, so retracts and offset writes appeared to succeed without being simulated.
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
