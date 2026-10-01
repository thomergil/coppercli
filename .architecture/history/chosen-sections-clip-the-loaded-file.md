# Milling chosen sections: a transform of the loaded file, split at the section lines

**Date:** 2026-09-30 · **Asked for by:** Thomer (owner): re-mill chosen sections of the board.
He chose the keys: ↑/↓ add or remove a horizontal line, →/← a vertical one.

**Decided:** The operator divides the area the file cuts into equal columns and rows, 1 to
`Constants.MaxSectionsPerAxis` each, and chooses sections. The run mills only the cuts inside
them. Choosing none or all mills the whole board (`BoardSections.IsWholeBoard`). The sections
are a fourth input to `AppState.PutFileOnMachine`, applied first: `KeepSections`, then
`OffsetCutDepth`, then `ApplyProbeGrid`. The depth and the map therefore apply to the clipped
file, and the map still raises rapids by its highest point.

The area is `GCodeFile.CuttingBounds`, now in Core. It was a private helper in
`CncWebServer`, and the terminal's mill grid used different bounds, so the two screens would
have divided different areas.

**Tried and dropped:**
- Clipping at stream time in `MillingController`. Rule `machine-gcode-built-in-one-place`
  rules it out: the machine's G-code is built only by `PutFileOnMachine`, and a correction is
  a transform of the loaded file.
- Keeping or dropping whole paths instead of splitting them at the section lines. A
  ground-plane isolation contour spans the whole board, so it would be kept or dropped
  whatever the operator chose.

**Why this shape:** Sections are a correction like the depth. As a field of
`MachineFileBuild` they get the depth's version check (`MachineFileVersion`), its lock
(`FileLock`), its refusal during a run (`WhyTheFileCannotChange`), and its reset when a file
loads or the map changes (`ClearJobCorrections`). None of the four needed new code.

**Lesson:** Add a new correction to what the machine runs as another input to
`PutFileOnMachine`, never as a filter in the streaming path. Order the transforms by what each
later transform must see.

**Rule:** `machine-gcode-built-in-one-place` (four inputs and their order); web → browser
v13; shared constants → client v3.

Touches: `GCodeFile.KeepSections`, `GCodeFile.CuttingBounds`, `BoardDivision`,
`BoardSections`, `SectionClip`, `AppState.PutFileOnMachine`, `AppState.ChooseMillSections`,
`AppState.MillSections`, `MillingController`, `machine-gcode-built-in-one-place`,
`loaded-gcode-unchanged-during-run`, `web → browser`, `shared constants → client`.
