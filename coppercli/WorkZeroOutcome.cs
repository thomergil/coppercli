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

        /// <summary>The map was applied to the G-code again against the new Z0.</summary>
        MapReapplied,

        /// <summary>
        /// The map should have been applied again and could not be: the source G-code is
        /// missing or would not load. The G-code still holds the old Z0's corrections.
        /// </summary>
        MapNotReapplied,

        /// <summary>
        /// The map should have been discarded and could not be: the source G-code is missing
        /// or would not load. The G-code still holds corrections measured against the old
        /// origin.
        /// </summary>
        MapNotDiscarded,

        /// <summary>
        /// A run is streaming the loaded file, so it was left alone. Re-applying the map
        /// reloads the G-code and takes the program back to the start.
        /// </summary>
        FileLeftAlone
    }

    public static class WorkZeroOutcomeExtensions
    {
        /// <summary>
        /// Whether the loaded G-code contains corrections that do not match the origin,
        /// which the operator clears by reloading the file.
        /// </summary>
        public static bool LeftTheGCodeWrong(this WorkZeroOutcome outcome) =>
            outcome is WorkZeroOutcome.MapNotReapplied or WorkZeroOutcome.MapNotDiscarded;
    }
}
