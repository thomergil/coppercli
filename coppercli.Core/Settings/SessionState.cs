namespace coppercli.Core.Settings
{
    /// <summary>
    /// Transient session state that changes during normal use.
    /// Separate from MachineSettings which contains permanent configuration.
    /// </summary>
    public class SessionState
    {
        // File browsing
        public string LastBrowseDirectory { get; set; } = "";
        public string LastLoadedGCodeFile { get; set; } = "";

        // Probe auto-save (for resuming interrupted probes)
        public string ProbeAutoSavePath { get; set; } = "";

        // Last saved complete probe file
        public string LastSavedProbeFile { get; set; } = "";

        // Work zero position (stored when user sets X0 Y0 Z0)
        public double WorkZeroX { get; set; }
        public double WorkZeroY { get; set; }
        public double WorkZeroZ { get; set; }
        public bool HasStoredWorkZero { get; set; } = false;
    }
}
