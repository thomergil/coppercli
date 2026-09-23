namespace coppercli.Core.Settings
{
    /// <summary>
    /// State that changes during normal use. MachineSettings holds the configuration that
    /// does not.
    /// </summary>
    public class SessionState
    {
        // Offered again on the next connection attempt.
        public ConnectionType? LastSuccessfulConnectionType { get; set; }

        public string LastBrowseDirectory { get; set; } = "";
        public string LastLoadedGCodeFile { get; set; } = "";

        // Kept apart from the G-code directory, so browsing one does not move the other.
        public string LastProbeBrowseDirectory { get; set; } = "";
        public string LastMacroBrowseDirectory { get; set; } = "";
        public string LastMacroFile { get; set; } = "";

        public string ProbeSourceGCodeFile { get; set; } = "";

        // The map file last saved or loaded. Saving deletes the autosave, so after a restart
        // this is the only link from a saved map to its G-code.
        public string LastProbeFile { get; set; } = "";

        public bool HasStoredWorkZero { get; set; } = false;
    }
}
