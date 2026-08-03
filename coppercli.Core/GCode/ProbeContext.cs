using coppercli.Core.Util;

namespace coppercli.Core.GCode
{
    /// <summary>
    /// The setup a height map was measured in: which G-code it was probed for, and the
    /// work origin it was measured from.
    ///
    /// A height map is Z heights indexed by X/Y in work coordinates. Those numbers only
    /// mean anything against the board they were taken from and the origin they were
    /// measured relative to - move either and they describe somewhere else. Carrying the
    /// binding on the map (and in the saved file) is what lets the question "is this map
    /// usable here?" have one answer instead of being inferred from scattered state.
    /// </summary>
    public readonly record struct ProbeContext(string SourceFile, Vector3 WorkOrigin)
    {
        /// <summary>A map with no recorded setup - written before this was tracked.</summary>
        public static readonly ProbeContext Unknown = new(string.Empty, Vector3.MinValue);

        public bool IsKnown => !string.IsNullOrEmpty(SourceFile) && WorkOrigin != Vector3.MinValue;
    }

    /// <summary>Whether a stored height map describes the job now in hand.</summary>
    public enum ProbeApplicability
    {
        /// <summary>Measured for this file, from this origin - safe to apply.</summary>
        Applicable,

        /// <summary>Measured for a different G-code file.</summary>
        DifferentFile,

        /// <summary>Measured from a different work origin, so the heights land elsewhere.</summary>
        OriginMoved,

        /// <summary>No record of the setup it was measured in, so it cannot be checked.</summary>
        Unknown
    }
}
