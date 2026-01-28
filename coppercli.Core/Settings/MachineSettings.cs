using coppercli.Core.Util;

namespace coppercli.Core.Settings
{
    public enum ConnectionType
    {
        Serial,
        Ethernet
    }

    public class MachineSettings
    {
        // Warnings
        public bool SilenceExperimentalWarning { get; set; } = false;

        // Connection
        public ConnectionType ConnectionType { get; set; } = ConnectionType.Serial;
        public string SerialPortName { get; set; } = "/dev/ttyUSB0";
        public int SerialPortBaud { get; set; } = Constants.DefaultBaudRate;
        public bool SerialPortDTR { get; set; } = false;
        public string EthernetIP { get; set; } = "192.168.1.101";
        public int EthernetPort { get; set; } = Constants.DefaultEthernetPort;

        // Machine
        public int StatusPollInterval { get; set; } = Constants.StatusPollIntervalMs;
        public int ControllerBufferSize { get; set; } = Constants.GrblBufferSize;
        public bool LogTraffic { get; set; } = false;
        public bool EnableDebugLogging { get; set; } = false;
        public bool PauseFileOnHold { get; set; } = true;
        public bool IgnoreAdditionalAxes { get; set; } = true;

        // Probing
        public double ProbeSafeHeight { get; set; } = 5.0;
        public double ProbeMinimumHeight { get; set; } = 1.0;
        public double ProbeMaxDepth { get; set; } = 5.0;
        public double ProbeFeed { get; set; } = 20.0;
        public double ProbeXAxisWeight { get; set; } = 0.5;
        public double ProbeOffsetX { get; set; } = 0.0;
        public double ProbeOffsetY { get; set; } = 0.0;
        public bool AbortOnProbeFail { get; set; } = false;
        public double OutlineTraceHeight { get; set; } = 2.0;  // mm above Z0
        public double OutlineTraceFeed { get; set; } = 600.0;  // mm/min (10mm/sec)

        // Firmware
        public string FirmwareType { get; set; } = "Grbl";

        // Jogging
        public double JogFeed { get; set; } = 1000.0;
        public double JogDistance { get; set; } = 10.0;
        public double JogFeedSlow { get; set; } = 100.0;
        public double JogDistanceSlow { get; set; } = 1.0;

        // Tool Change
        public string MachineProfile { get; set; } = "";  // e.g., "nomad3"
        public double ToolSetterX { get; set; } = 0;      // Machine coords, 0 = not configured
        public double ToolSetterY { get; set; } = 0;
    }
}
