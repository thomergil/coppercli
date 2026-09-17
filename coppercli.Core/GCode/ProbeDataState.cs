namespace coppercli.Core.GCode
{
    /// <summary>
    /// How much of the loaded height map is measured. Read <see cref="ProbeGrid.State"/>
    /// for a map, or <see cref="ProbeGrid.StateOf"/> where there may not be one.
    /// </summary>
    public enum ProbeDataState
    {
        /// <summary>There is no map.</summary>
        None,

        /// <summary>A map with its points laid out, none of them measured yet.</summary>
        Ready,

        /// <summary>Some points measured, some still to go.</summary>
        Partial,

        /// <summary>Every point measured, so the map can be applied.</summary>
        Complete
    }
}
