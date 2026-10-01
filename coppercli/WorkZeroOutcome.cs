namespace coppercli
{
    /// <summary>
    /// Returned rather than stored, so one front end's result cannot overwrite another's.
    /// </summary>
    /// <param name="Refused">Why nothing was sent, or null once the offset was written.</param>
    public readonly record struct WorkZeroResult(string? Refused, WorkZeroOutcome Outcome);

    /// <summary>What setting the work zero did to the height map.</summary>
    public enum WorkZeroOutcome
    {
        /// <summary>No height map was applied to the G-code.</summary>
        NothingToDo,

        /// <summary>
        /// The datum moved in X or Y, so the map's coordinates no longer describe the board.
        /// The map and its autosave are deleted.
        /// </summary>
        MapDiscarded,

        /// <summary>
        /// Only Z moved, and a height map is applied. It stays applied: its heights are the
        /// copper's, relative to the zero touched off on that copper.
        /// </summary>
        MapStillApplied,

        /// <summary>
        /// A run is streaming the loaded file, so it was left alone: rebuilding the machine's
        /// G-code would take the program back to the start.
        /// </summary>
        FileLeftAlone
    }
}
