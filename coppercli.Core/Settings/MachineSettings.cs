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
        public int SerialPortBaud { get; set; } = 115200;
        public bool SerialPortDTR { get; set; } = false;
        public string EthernetIP { get; set; } = "192.168.1.101";
        public int EthernetPort { get; set; } = 34000;

        // Machine
        public int StatusPollInterval { get; set; } = 100;
        public int ControllerBufferSize { get; set; } = 127;
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
        public double OutlineTraverseHeight { get; set; } = 2.0;  // mm above Z0
        public double OutlineTraverseFeed { get; set; } = 600.0;  // mm/min (10mm/sec)

        // Firmware
        public string FirmwareType { get; set; } = "Grbl";

        // Jogging
        public double JogFeed { get; set; } = 1000.0;
        public double JogDistance { get; set; } = 10.0;
        public double JogFeedSlow { get; set; } = 100.0;
        public double JogDistanceSlow { get; set; } = 1.0;
    }
}
