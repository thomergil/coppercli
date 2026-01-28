namespace coppercli.Core.Settings
{
    /// <summary>
    /// Transient session state that changes during normal use.
    /// Separate from MachineSettings which contains permanent configuration.
    /// </summary>
    public class SessionState
    {
        // Connection - tracks last successful connection for auto-reconnect
        public ConnectionType? LastSuccessfulConnectionType { get; set; }

        // File browsing
        public string LastBrowseDirectory { get; set; } = "";
        public string LastLoadedGCodeFile { get; set; } = "";
        public string LastMacroBrowseDirectory { get; set; } = "";
        public string LastMacroFile { get; set; } = "";

        // Probe auto-save (for resuming interrupted probes)
        public string ProbeAutoSavePath { get; set; } = "";

        // Last saved complete probe file
        public string LastSavedProbeFile { get; set; } = "";

        // Work zero position (stored when user sets X0 Y0 Z0)
        public double WorkZeroX { get; set; }
        public double WorkZeroY { get; set; }
        public double WorkZeroZ { get; set; }
        public bool HasStoredWorkZero { get; set; } = false;

        // Tool change - reference tool length for offset calculation
        public double ReferenceToolLength { get; set; } = 0;
        public bool HasReferenceToolLength { get; set; } = false;

        // Tool setter Z position (machine coords) - for fast approach on subsequent probes
        public double LastToolSetterZ { get; set; } = 0;
    }
}
