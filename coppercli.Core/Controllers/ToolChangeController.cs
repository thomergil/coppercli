#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using coppercli.Core.Communication;
using coppercli.Core.Util;
using static coppercli.Core.Util.Constants;
using static coppercli.Core.Util.GrblProtocol;
using static coppercli.Core.Controllers.ControllerConstants;
using static coppercli.Core.Util.GCodeFormat;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Controller for tool change workflow (M6 handling).
    ///
    /// SINGLE SOURCE OF TRUTH: The Phase property is the FSM state.
    /// Both TUI and Web UI read Phase to determine what to display.
    /// See ToolChangePhase.cs for the complete FSM documentation.
    ///
    /// TWO MODES:
    ///
    /// Mode A (with tool setter) - automatic Z offset measurement:
    ///   NotStarted → RaisingZ → MovingToToolSetter → MeasuringReference
    ///   → RaisingZ → MovingToWorkArea → WaitingForToolChange [USER: change tool]
    ///   → MovingToToolSetter → MeasuringNewTool → ApplyingOffset
    ///   → Returning → Complete
    ///
    /// Mode B (without tool setter) - manual Z re-zeroing:
    ///   NotStarted → RaisingZ → MovingToWorkArea → WaitingForToolChange [USER: change tool]
    ///   → WaitingForZeroZ [USER: jog, set Z0] → Complete
    ///
    /// UI BEHAVIOR (1:1 with phase):
    ///   - WaitingForToolChange → Mill screen shows overlay with Continue/Abort
    ///   - WaitingForZeroZ → Jog screen shows "Continue Milling" button
    ///   - All other phases → Spindle moving autonomously, no user action needed
    ///   - null/NotStarted/Complete → No tool change UI
    ///
    /// PAGE RELOAD: UI queries /api/status which returns toolChange.phase.
    /// UI checks phase string directly to determine what to show.
    /// </summary>
    public class ToolChangeController : ControllerBase, IToolChangeController
    {
        // =========================================================================
        // Dependencies
        // =========================================================================

        private readonly IMachine _machine;
        private readonly Func<bool> _hasToolSetter;
        private readonly Func<(double X, double? Y)?> _getToolSetterPosition;
        private readonly Func<ToolSetterConfig?> _getToolSetterConfig;

        // =========================================================================
        // State
        // =========================================================================

        private ToolChangePhase _phase = ToolChangePhase.NotStarted;
        private readonly object _phaseLock = new();
        private ToolChangeInfo? _currentToolChange;

        // The reference tool's length, measured at the start of a tool change and used to
        // work out the new tool's offset. Cleared per tool change - see ResetRunState.
        private double _referenceToolLength;

        // Return position after tool change
        private double _returnX;
        private double _returnY;

        // =========================================================================
        // Properties
        // =========================================================================

        public ToolChangePhase Phase
        {
            get
            {
                lock (_phaseLock)
                {
                    return _phase;
                }
            }
            private set
            {
                lock (_phaseLock)
                {
                    _phase = value;
                }
                ControllerLog.Log(LogToolChangePhase, value);
                PhaseChanged?.Invoke(value);
                EmitProgress(new ProgressInfo(value.ToString(), 0, GetPhaseMessage(value)));
            }
        }

        public bool HasToolSetter => _hasToolSetter();

        public ToolChangeInfo? CurrentToolChange => _currentToolChange;

        /// <summary>Configuration options for this tool change.</summary>
        public ToolChangeOptions Options { get; set; } = new();

        // =========================================================================
        // Events
        // =========================================================================

        public event Action<ToolChangePhase>? PhaseChanged;

        // =========================================================================
        // Constructor
        // =========================================================================

        /// <summary>
        /// Create a ToolChangeController.
        /// </summary>
        /// <param name="machine">Machine interface</param>
        /// <param name="hasToolSetter">Function to check if tool setter is configured</param>
        /// <param name="getToolSetterPosition">Function to get tool setter XY position</param>
        /// <param name="getToolSetterConfig">Function to get tool setter probing config</param>
        public ToolChangeController(
            IMachine machine,
            Func<bool> hasToolSetter,
            Func<(double X, double? Y)?> getToolSetterPosition,
            Func<ToolSetterConfig?> getToolSetterConfig)
        {
            _machine = machine ?? throw new ArgumentNullException(nameof(machine));
            _hasToolSetter = hasToolSetter ?? throw new ArgumentNullException(nameof(hasToolSetter));
            _getToolSetterPosition = getToolSetterPosition ?? throw new ArgumentNullException(nameof(getToolSetterPosition));
            _getToolSetterConfig = getToolSetterConfig ?? throw new ArgumentNullException(nameof(getToolSetterConfig));
        }

        // =========================================================================
        // IToolChangeController implementation
        // =========================================================================

        public async Task<bool> HandleToolChangeAsync(ToolChangeInfo info, CancellationToken ct = default)
        {
            if (State != ControllerState.Idle)
            {
                throw new InvalidOperationException(string.Format(ErrorCannotStart, State));
            }

            // This is the entry point, not StartAsync, so the reset the base class would
            // have done has to happen here.
            ResetRunState();

            _currentToolChange = info;
            ControllerLog.Log(LogToolChangeStart, info.ToolNumber);

            try
            {
                TransitionTo(ControllerState.Initializing);

                // Wait for any buffered commands to complete
                await MachineWait.WaitForIdleAsync(_machine, IdleWaitTimeoutMs, ct);

                // Store return position
                _returnX = _machine.WorkPosition.X;
                _returnY = _machine.WorkPosition.Y;

                // Raise Z to clearance
                Phase = ToolChangePhase.RaisingZ;
                await RaiseZToClearanceAsync(ct);

                TransitionTo(ControllerState.Running);

                // Route to appropriate workflow
                bool success;
                if (HasToolSetter)
                {
                    var setterPos = _getToolSetterPosition();
                    if (setterPos == null)
                    {
                        throw new InvalidOperationException(ErrorToolSetterNotConfigured);
                    }
                    success = await HandleWithToolSetterAsync(setterPos.Value, ct);
                }
                else
                {
                    success = await HandleWithoutToolSetterAsync(ct);
                }

                if (success)
                {
                    Phase = ToolChangePhase.Complete;
                    TransitionTo(ControllerState.Completing);
                    TransitionTo(ControllerState.Completed);
                    ControllerLog.Log(LogToolChangeComplete);
                }
                else
                {
                    TransitionTo(ControllerState.Cancelled);
                    ControllerLog.Log(LogToolChangeAborted);
                }

                return success;
            }
            catch (OperationCanceledException)
            {
                // HandleToolChangeAsync is a second entry point alongside StartAsync, so
                // it has to run the same cleanup the base class would - otherwise an
                // aborted tool change leaves the tool down at the tool setter.
                await SafeCleanupAsync();
                TransitionTo(ControllerState.Cancelled);
                ControllerLog.Log(LogToolChangeAborted);
                return false;
            }
            catch (Exception ex)
            {
                await SafeCleanupAsync();
                EmitError(ex);
                TransitionTo(ControllerState.Failed);
                return false;
            }
            finally
            {
                _currentToolChange = null;
            }
        }

        // =========================================================================
        // IController implementation
        // =========================================================================

        /// <inheritdoc/>
        protected override void ResetRunState()
        {
            // NotStarted is what makes DetectToolChange() report no tool change.
            lock (_phaseLock)
            {
                _phase = ToolChangePhase.NotStarted;
            }

            _currentToolChange = null;
            _returnX = 0;
            _returnY = 0;

            // A tool length measured against one tool change says nothing about the next,
            // where the operator has been free to fit anything.
            _referenceToolLength = 0;
        }

        protected override Task RunAsync(CancellationToken ct)
        {
            // Not used - HandleToolChangeAsync is the entry point
            throw new NotImplementedException("Use HandleToolChangeAsync instead");
        }

        /// <summary>
        /// Retracts Z, never letting a cleanup failure mask the error that caused it.
        /// </summary>
        private async Task SafeCleanupAsync()
        {
            try
            {
                await CleanupAsync();
            }
            catch (Exception ex)
            {
                ControllerLog.Log("ToolChange cleanup failed: {0}", ex.Message);
            }
        }

        protected override async Task CleanupAsync()
        {
            // Raise Z to safe height
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdMachineCoords} {CmdRapidMove} Z{ToolChangeClearanceZ:F1}"));
            await Task.Delay(CommandDelayMs);
        }

        // =========================================================================
        // Workflow phases
        // =========================================================================

        private async Task RaiseZToClearanceAsync(CancellationToken ct)
        {
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdMachineCoords} {CmdRapidMove} Z{ToolChangeClearanceZ:F1}"));
            await MachineWait.WaitForIdleAsync(_machine, ZHeightWaitTimeoutMs, ct);
        }

        private async Task<bool> HandleWithToolSetterAsync((double X, double? Y) setterPos, CancellationToken ct)
        {
            // Always measure the reference tool: the user may have changed it manually
            // between jobs, so a cached length cannot be trusted (which is also why it is
            // not persisted across sessions).
            Phase = ToolChangePhase.MovingToToolSetter;
            await MoveToToolSetterAsync(setterPos, ct);

            Phase = ToolChangePhase.MeasuringReference;
            var refLength = await ProbeToolSetterAsync(ct);
            if (refLength == null)
            {
                EmitError(new ControllerError(LogToolChangeProbeFailed, null, true));
                return false;
            }

            _referenceToolLength = refLength.Value;

            // Raise Z after probing
            Phase = ToolChangePhase.RaisingZ;
            await RaiseZToClearanceAsync(ct);

            // Move to work area center for tool swap
            Phase = ToolChangePhase.MovingToWorkArea;
            await MoveToWorkAreaCenterAsync(ct);

            // Prompt user to change tool
            if (!await PromptForToolChangeAsync(ToolChangePrompt, ct))
            {
                return false;
            }

            // Measure new tool
            Phase = ToolChangePhase.MovingToToolSetter;
            await MoveToToolSetterAsync(setterPos, ct);

            Phase = ToolChangePhase.MeasuringNewTool;
            var newLength = await ProbeToolSetterAsync(ct);
            if (newLength == null)
            {
                EmitError(new ControllerError(LogToolChangeProbeFailed, null, true));
                return false;
            }

            // Calculate and apply offset
            Phase = ToolChangePhase.ApplyingOffset;
            double offset = newLength.Value - _referenceToolLength;
            // One consistent read. Subtracting WorkPosition from MachinePosition reads
            // Read G54 itself, not the combined WCO. WorkOffset is G54 + G92 + tool
            // length offset, but the write below is G10 L2 P1, which sets G54 alone -
            // so starting from the combined figure would re-datum Z by whatever the
            // other two contribute, at every tool change.
            if (!await _machine.RefreshWorkOffsetsAsync(WorkOffsetQueryTimeoutMs, ct))
            {
                throw new InvalidOperationException(ErrorWorkOffsetUnknown);
            }

            double currentWcoZ = _machine.G54Offset.Z;
            double newWcoZ = currentWcoZ + offset;
            ControllerLog.Log(LogToolChangeOffset, _referenceToolLength, newLength.Value, offset);
            _machine.SendLine(Inv($"{CmdSetWorkOffset} Z{newWcoZ:F3}"));
            await Task.Delay(CommandDelayMs, ct);

            // Return to original position
            Phase = ToolChangePhase.Returning;
            await ReturnToPositionAsync(ct);

            return true;
        }

        private async Task<bool> HandleWithoutToolSetterAsync(CancellationToken ct)
        {
            // First prompt: change tool
            if (!await PromptForToolChangeAsync(ToolChangePrompt, ct))
            {
                return false;
            }

            // Second prompt: user jogs to PCB surface and sets Z0
            // User navigates to jog screen, sets Z0 manually, then presses Continue
            if (!await PromptForZeroZAsync(ct))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Prompts user to set Z0 (Mode B only).
        /// User navigates to jog screen, jogs to PCB surface, sets Z0, returns and presses Continue.
        /// </summary>
        private async Task<bool> PromptForZeroZAsync(CancellationToken ct)
        {
            Phase = ToolChangePhase.WaitingForZeroZ;
            var response = await RequestUserInputAsync(
                ToolChangeZeroZTitle,
                ToolChangePromptZeroZ,
                new[] { OptionContinue, OptionAbort },
                ct);

            if (response == OptionAbort)
            {
                return false;
            }

            // Clear Door state if user opened enclosure
            await MachineWait.ClearDoorStateAsync(_machine, ct);
            return true;
        }

        // =========================================================================
        // User interaction helpers
        // =========================================================================

        /// <summary>
        /// Prompts user to change tool, handles abort, and clears door state.
        /// Returns true if user chose to continue, false if aborted.
        /// </summary>
        private async Task<bool> PromptForToolChangeAsync(string promptFormat, CancellationToken ct)
        {
            Phase = ToolChangePhase.WaitingForToolChange;
            string prompt = string.Format(promptFormat, _currentToolChange?.ToolNumber ?? 0);
            var response = await RequestUserInputAsync(
                ToolChangePromptTitle,
                prompt,
                new[] { OptionContinue, OptionAbort },
                ct);

            if (response == OptionAbort)
            {
                return false;
            }

            // Clear Door state if user opened enclosure to change tool
            await MachineWait.ClearDoorStateAsync(_machine, ct);
            return true;
        }

        // =========================================================================
        // Movement helpers
        // =========================================================================

        private async Task MoveToToolSetterAsync((double X, double? Y) setterPos, CancellationToken ct)
        {
            string cmd = Inv($"{CmdMachineCoords} {CmdRapidMove} X{setterPos.X:F1}");
            if (setterPos.Y.HasValue)
            {
                cmd += Inv($" Y{setterPos.Y.Value:F1}");
            }
            _machine.SendLine(cmd);
            await MachineWait.WaitForIdleAsync(_machine, MoveCompleteTimeoutMs, ct);
        }

        private async Task MoveToWorkAreaCenterAsync(CancellationToken ct)
        {
            // Move to work area center for accessible tool swap
            // Use Options.WorkAreaCenter if provided (calculated from file bounds by caller)
            // Otherwise fall back to return position (where M6 was encountered)
            double targetX = Options.WorkAreaCenter?.X ?? _returnX;
            double targetY = Options.WorkAreaCenter?.Y ?? _returnY;

            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdRapidMove} X{targetX:F3} Y{targetY:F3}"));
            await MachineWait.WaitForIdleAsync(_machine, MoveCompleteTimeoutMs, ct);
        }

        private async Task ReturnToPositionAsync(CancellationToken ct)
        {
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdMachineCoords} {CmdRapidMove} Z{ToolChangeClearanceZ:F1}"));
            await MachineWait.WaitForIdleAsync(_machine, ZHeightWaitTimeoutMs, ct);
            _machine.SendLine(Inv($"{CmdRapidMove} X{_returnX:F3} Y{_returnY:F3}"));
            await MachineWait.WaitForIdleAsync(_machine, MoveCompleteTimeoutMs, ct);
        }

        // =========================================================================
        // Probing helpers
        // =========================================================================

        private async Task<double?> ProbeToolSetterAsync(CancellationToken ct)
        {
            var config = _getToolSetterConfig();
            double probeDepth = config?.ProbeDepth ?? ToolSetterProbeDepth;
            double fastFeed = config?.FastFeed ?? ToolSetterSeekFeed;
            double slowFeed = config?.SlowFeed ?? ToolSetterProbeFeed;
            double retract = config?.Retract ?? ToolSetterRetract;

            // No rapid pre-approach. The only height available to aim one at is the
            // trigger height of the PREVIOUS probe, and within a tool change that probe
            // was taken with the previous tool. A tool longer than the clearance margin
            // would be driven into the setter at rapid speed. The seek probe below starts
            // from wherever Z is, which is what it is for.

            // Fast seek probe
            var (seekSuccess, seekZ) = await ExecuteProbeAsync(-probeDepth, fastFeed, ct);
            if (!seekSuccess)
            {
                return null;
            }

            // Retract
            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdMachineCoords} {CmdRapidMove} Z{seekZ + retract:F3}"));
            await MachineWait.WaitForIdleAsync(_machine, ZHeightWaitTimeoutMs, ct);

            // Slow precise probe
            double slowTarget = seekZ - 1.0;
            var (probeSuccess, probeZ) = await ExecuteProbeToMachineZAsync(slowTarget, slowFeed, ct);
            if (!probeSuccess)
            {
                return null;
            }

            // Retract after probing
            _machine.SendLine(Inv($"{CmdMachineCoords} {CmdRapidMove} Z{probeZ + retract:F3}"));
            await MachineWait.WaitForIdleAsync(_machine, ZHeightWaitTimeoutMs, ct);

            return probeZ;
        }

        private async Task<(bool Success, double MachineZ)> ExecuteProbeAsync(double targetWorkZ, double feed, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<(bool, double)>();

            void OnProbeFinished(Vector3 pos, bool success)
            {
                tcs.TrySetResult((success, _machine.LastProbePosMachine.Z));
            }

            _machine.ProbeFinished += OnProbeFinished;

            try
            {
                _machine.ProbeStart();
                _machine.SendLine(CmdAbsolute);
                _machine.SendLine(Inv($"{CmdProbeToward} Z{targetWorkZ:F3} F{feed:F1}"));

                using var registration = ct.Register(() => tcs.TrySetCanceled());

                return await MachineWait.AwaitReplyOrTimeoutAsync(
                    tcs.Task, ProbeReplyTimeoutMs, ErrorProbeTimeout, ct);
            }
            finally
            {
                _machine.ProbeFinished -= OnProbeFinished;
                _machine.ProbeStop();
            }
        }

        private async Task<(bool Success, double MachineZ)> ExecuteProbeToMachineZAsync(double targetMachineZ, double feed, CancellationToken ct)
        {
            double wcoZ = _machine.WorkOffset.Z;
            double targetWorkZ = targetMachineZ - wcoZ;
            return await ExecuteProbeAsync(targetWorkZ, feed, ct);
        }

        // =========================================================================
        // Helpers
        // =========================================================================

        private static string GetPhaseMessage(ToolChangePhase phase)
        {
            return phase switch
            {
                ToolChangePhase.RaisingZ => MessageToolChangeRaisingZ,
                ToolChangePhase.MovingToToolSetter => MessageToolChangeMovingToSetter,
                ToolChangePhase.MeasuringReference => MessageToolChangeMeasuringRef,
                ToolChangePhase.MovingToWorkArea => MessageToolChangeMovingToWork,
                ToolChangePhase.WaitingForToolChange => MessageToolChangeWaitingForToolChange,
                ToolChangePhase.WaitingForZeroZ => MessageToolChangeWaitingForZeroZ,
                ToolChangePhase.MeasuringNewTool => MessageToolChangeMeasuringNew,
                ToolChangePhase.ProbingPCBSurface => MessageToolChangeProbingPCB,
                ToolChangePhase.ApplyingOffset => MessageToolChangeApplyingOffset,
                ToolChangePhase.Returning => MessageToolChangeReturning,
                ToolChangePhase.Complete => MessageToolChangeComplete,
                _ => phase.ToString()
            };
        }
    }

    /// <summary>
    /// Configuration for tool setter probing.
    /// </summary>
    public class ToolSetterConfig
    {
        public double X { get; set; }
        public double? Y { get; set; }
        public double ProbeDepth { get; set; }
        public double FastFeed { get; set; }
        public double SlowFeed { get; set; }
        public double Retract { get; set; }
    }
}
