using System.Globalization;

namespace coppercli.Core.Util
{
    /// <summary>
    /// Constants shared across coppercli.Core; CLI-specific ones belong in
    /// coppercli/CliConstants.cs and GRBL protocol ones in GrblProtocol.cs. Machine Z=0 is
    /// home at the top, and negative Z runs down toward the work.
    /// </summary>
    public static class Constants
    {
        /// <summary>Parses decimals from G-code and GRBL replies with '.' as the
        /// separator, whatever the system locale is.</summary>
        public static readonly NumberFormatInfo DecimalParseFormat = new() { NumberDecimalSeparator = "." };

        /// <summary>Writes decimals into G-code with '.' as the separator and three
        /// decimal places.</summary>
        public static readonly NumberFormatInfo DecimalOutputFormat = new() { NumberDecimalSeparator = ".", NumberDecimalDigits = 3 };

        /// <summary>
        /// The build number stores the letter suffix, so 'f' is 102. GRBL 1.1f is the first
        /// with the real-time status report format this code reads.
        /// </summary>
        public static readonly Version MinimumGrblVersion = new(1, 1, 'f');

        public const int SerialReadTimeoutMs = 100;

        public const int SerialWriteTimeoutMs = 1000;

        /// <summary>The GRBL default since v0.9.</summary>
        public const int DefaultBaudRate = 115200;

        /// <summary>GRBL's serial receive buffer, in bytes; lines are queued until it
        /// fills.</summary>
        public const int GrblBufferSize = 127;

        /// <summary>Percent, where 100 is the programmed rate.</summary>
        public const int OverrideDefaultPercent = 100;

        /// <summary>How often '?' is sent for a status report.</summary>
        public const int StatusPollIntervalMs = 100;

        public const int DefaultEthernetPort = 34000;

        /// <summary>Bytes per proxy read or write.</summary>
        public const int ProxyBufferSize = 4096;

        /// <summary>How long the proxy worker sleeps when the port has nothing to
        /// read.</summary>
        public const int ProxyThreadSleepMs = 1;

        /// <summary>How long the accept loop sleeps between checks for a pending
        /// connection.</summary>
        public const int ProxyAcceptLoopSleepMs = 100;

        public const int SocketPollTimeoutMicroseconds = 100000;

        /// <summary>How often the file position is published while streaming.</summary>
        public const int FilePosUpdateIntervalMs = 500;

        /// <summary>
        /// How long the worker leaves GRBL alone before its first status query. Seeded into
        /// the last-poll time, so a board still settling after the port opens is not polled
        /// for a report it cannot yet produce.
        /// </summary>
        public const int FirstStatusPollDelayMs = 500;

        /// <summary>Errors are ignored for this long after connecting, because some
        /// controllers send garbage while initializing.</summary>
        public const int ErrorGracePeriodMs = 200;

        /// <summary>Written when the LogTraffic setting is on, holding every line sent and
        /// received.</summary>
        public const string SerialTrafficLogFile = "serial_traffic.log";

        public const int CommandDelayMs = 200;

        /// <summary>
        /// How long a stop waits for the feed hold to bring the axes to rest before it resets
        /// GRBL. The hold stops them within a few status reports; this bound keeps a machine
        /// that never reports stopping from delaying the reset that stops the spindle.
        /// </summary>
        public const int StopHoldTimeoutMs = 1000;

        public const int IdleWaitTimeoutMs = 3000;

        public const int MotionStartTimeoutMs = 1000;

        /// <summary>How long to wait for GRBL's reply to $# (its stored offsets).</summary>
        public const int WorkOffsetQueryTimeoutMs = 2000;

        /// <summary>
        /// How long to wait for GRBL's [PRB:] reply before giving up on a probe.
        /// Generous compared with any real probe move (a 50mm/min seek over 50mm is
        /// about a minute); the point is that a reply which never arrives - because the
        /// command was rejected, or the machine alarmed - cannot hang the workflow with
        /// the tool resting on the work.
        /// </summary>
        public const int ProbeReplyTimeoutMs = 180000;

        public const int ZHeightWaitTimeoutMs = 30000;

        public const int MoveCompleteTimeoutMs = 60000;

        public const int HomingTimeoutMs = 60000;

        /// <summary>How long the machine must read Idle without a break before a move
        /// counts as finished rather than briefly paused.</summary>
        public const int IdleSettleMs = 1000;

        /// <summary>Longest the settling phase may wait for a machine that never becomes
        /// ready (a standing alarm, sleep) before telling the operator why.</summary>
        public const int SettleTimeoutMs = 60000;

        /// <summary>How long the lift-on-cancel may take before the stop returns anyway.
        /// The tool still needs to come up, but a Stop must not appear to hang.</summary>
        public const int CancelRetractTimeoutMs = 5000;

        /// <summary>
        /// How long the port stays open after the reset byte when coppercli disconnects, so
        /// the reset reaches GRBL before the link closes.
        /// </summary>
        public const int ResetWaitMs = 500;

        /// <summary>
        /// Longest Machine holds lines after a soft reset waiting for GRBL's banner. The
        /// banner normally ends the wait; this covers one that never arrives.
        /// </summary>
        public const int ResetAnnounceTimeoutMs = 2000;

        /// <summary>
        /// How long GRBL has to answer a line that moves nothing, with nothing queued ahead
        /// of it: it answers as soon as it reads the line. Behind queued motion, a line GRBL
        /// runs only once the planner is empty - a G10 among them - is answered later.
        /// </summary>
        public const int CommandAnswerTimeoutMs = 1000;

        /// <summary>Positions within this distance, in mm, are treated as equal.</summary>
        public const double PositionToleranceMm = 0.1;

        /// <summary>
        /// How far a work offset read back may sit from the value written, in mm. The only
        /// difference comes from G-code's three decimal places; PositionToleranceMm is wider
        /// than a depth adjustment and has accepted writes that GRBL did not apply.
        /// </summary>
        public const double WorkOffsetToleranceMm = 0.001;

        /// <summary>Below this, a probe map's height range counts as no variation at
        /// all.</summary>
        public const double HeightRangeEpsilon = 0.0001;

        /// <summary>
        /// Where the tool is parked whenever it has to be clear of the work: before a job
        /// starts, after it finishes, at a tool change, and after a stop (mm, machine
        /// coordinates). 1mm below the top clears the workpiece without reaching the limit
        /// switch.
        /// </summary>
        public const double SafeClearanceZ = -1.0;

        /// <summary>How far down the tool setter probe may go, in mm.</summary>
        public const double ToolSetterProbeDepth = 50.0;

        /// <summary>The fast seek toward the tool setter, in mm/min.</summary>
        public const double ToolSetterSeekFeed = 500.0;

        /// <summary>The slow second touch on the tool setter, in mm/min.</summary>
        public const double ToolSetterProbeFeed = 50.0;

        /// <summary>How far the tool lifts after touching the setter, in mm.</summary>
        public const double ToolSetterRetract = 10.0;

        /// <summary>How many lines before an M6 are searched for its Tn tool
        /// number.</summary>
        public const int ToolInfoSearchLines = 10;

        /// <summary>Tags a GCodeParser warning so a screen can pick it out: something
        /// dangerous, such as a G28 home.</summary>
        public const string WarningPrefixDanger = "DANGER";

        /// <summary>Tags a GCodeParser warning that the file is written in inches.</summary>
        public const string WarningPrefixInches = "INCHES";

        /// <summary>
        /// How far, in mm, a job may reach left of or below work zero before its file warns
        /// that zero is not at the job's lower-left corner. Isolation passes around a board
        /// zeroed at that corner reach a fraction of a millimeter past it; a job zeroed at
        /// another corner reaches the whole width of the board.
        /// </summary>
        public const double JobOriginToleranceMm = 5.0;

        /// <summary>
        /// {0} and {1} are the X and Y of the job's lower-left corner; {2} and {3} are where
        /// work zero sits measured from the job's lower-left corner.
        /// </summary>
        public const string WarningJobOriginFormat = WarningPrefixDanger +
            ": The job's lower-left corner is at X{0:F1} Y{1:F1}, so work zero is not at the board's lower-left corner. " +
            "Set work zero X{2:F1} Y{3:F1} from the board's lower-left corner, or regenerate the file with a board outline.";

        /// <summary>
        /// Shown when the operator asks for a height map, a depth adjustment or sections on a file
        /// whose GCodeFile.HasArcsOutsideXYPlane is true.
        /// </summary>
        public const string ErrorArcsOutsideXYPlane =
            "This file has arcs outside the XY plane (G18 or G19), so coppercli cannot apply a height map or a depth adjustment to it, or mill it by section. Mill it without a height map, a depth adjustment or sections.";

        /// <summary>The most columns, and the most rows, the board can be divided into.</summary>
        public const int MaxSectionsPerAxis = 8;

        /// <summary>Millimeters. The shortest piece BoardDivision.Pieces splits a move into at a section line.</summary>
        public const double SectionEdgeToleranceMm = 0.01;

        /// <summary>{0} is MaxSectionsPerAxis.</summary>
        public const string ErrorSectionCountFormat = "Divide the board into 1 to {0} columns and 1 to {0} rows.";

        public const string ErrorSectionOutsideBoard =
            "A chosen section lies outside the columns and rows you set. Choose the sections again.";

        public const string ErrorNoAreaToDivide =
            "This file's cuts cover no area to divide into sections. Mill the whole board instead.";

        public const string ErrorSectionsNoTravelHeight =
            "This file never moves above the copper where it needs to travel between sections, so coppercli has no safe travel height from the file. Mill the whole board instead.";

        public const string ErrorSectionsCutWithUnknownStart =
            "This file cuts before it has said where the tool is, so coppercli cannot tell which sections the cut crosses. Mill the whole board instead.";

        public const string ErrorSectionsBlockInLeftOutCut =
            "This file probes, moves in machine coordinates or changes an offset while the tool is still in the copper from a cut that is left out, or before it has said where the tool is, so coppercli cannot mill it by section. Mill the whole board instead.";

        public const string ErrorNoPhaseChosen = "Choose at least one phase to mill.";

        public const string ErrorPhaseNotInFile = "A chosen phase is not in this file. Choose the phases again.";

        public const string ErrorPhaseSkipsABlock =
            "A phase you cleared probes or changes an offset, and a later phase may depend on that. coppercli cannot skip that phase. Choose it again.";

        public const string ErrorPhaseSkipsTheSpindleStart =
            "A phase you cleared starts the spindle, and a later phase cuts without starting the spindle again. coppercli cannot skip that phase. Choose it again.";

        public const string ErrorPhaseToolPlaceUnknown =
            "Without the phases you cleared, this file cuts, probes, moves in machine coordinates or changes an offset before it moves the tool to a known point above the copper. coppercli has no safe point to take the tool to. Choose those phases again.";

        public const string ErrorPhaseNoTravelHeight =
            "Without the phases you cleared, this file never moves above the copper. coppercli has no safe height to cross the board at. Choose those phases again.";

        public const string ErrorNothingToCut =
            "The sections and phases you chose hold no cuts. Choose other sections or phases.";

        /// <summary>
        /// How long a stop waits for a run to unwind: the machine is stopped and reset, then
        /// the tool is lifted clear and confirmed. Too short and a stop that is working
        /// reports that the machine may still be moving.
        /// </summary>
        public const int ControllerCancelTimeoutMs = 18000;

        /// <summary>Widest the mill view may be, in cells.</summary>
        public const int MillGridMaxWidth = 50;

        /// <summary>Tallest the mill view may be, in cells.</summary>
        public const int MillGridMaxHeight = 20;

        /// <summary>Z threshold below which the tool is considered cutting (mm, work coords).</summary>
        public const double MillCuttingDepthThreshold = 1.0;

        /// <summary>Minimum coordinate range to avoid division by zero in grid mapping.</summary>
        public const double MillMinRangeThreshold = 0.001;

        /// <summary>How far down a surface probe may go, in mm.</summary>
        public const double ProbeMaxDepth = 50.0;

        /// <summary>The default probe feed, in mm/min.</summary>
        public const double ProbeFeed = 100.0;

        /// <summary>Default Z retract height after probing (mm, work coordinates).</summary>
        public const double RetractZMm = 6.0;

        /// <summary>
        /// The prefix a client matches to recognize the one rejection it can act on: another
        /// client holds the connection, and the operator may take it over.
        /// </summary>
        public const string ProxyConnectionRejectedPrefix = "Connection rejected:";

        /// <summary>Built from the prefix, so rewording the sentence cannot leave a reader
        /// matching text nobody sends.</summary>
        public const string ProxyConnectionRejected = ProxyConnectionRejectedPrefix
            + " another client is already connected. Close the existing connection first.\r\n";

        public const string ProxySerialPortBusyPrefix = "Cannot access";

        /// <summary>Formatted with the port name. Starts with the prefix a client matches.</summary>
        public const string ProxyCannotOpenSerialPort = ProxySerialPortBusyPrefix
            + " {0}: the port could not be opened. Is the machine on and the cable connected?\r\n";

        public const string ProxySerialPortInUsePrefix = "Serial port in use:";

        public const string ProxySerialPortInUse = ProxySerialPortInUsePrefix
            + " the coppercli server has the machine. Take it over to continue.\r\n";

        public const string ProxyForceDisconnectPrefix = "Force disconnect:";

        public const string ProxyForceDisconnect = ProxyForceDisconnectPrefix
            + " another client is taking over.\r\n";

        public const int ForceDisconnectMessageDelayMs = 200;
    }
}
