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
    /// The tool change an M6 sets off. Phase is what both front ends read to decide what to
    /// show - after a page reload the browser gets it from /api/status - and
    /// <see cref="ToolChangePhase"/> carries the two flows and what each phase means.
    /// </summary>
    public class ToolChangeController : ControllerBase, IToolChangeController
    {
        private readonly IMachine _machine;

        /// <inheritdoc/>
        protected override IMachine Machine => _machine;
        private readonly Func<bool> _hasToolSetter;
        private readonly Func<(double X, double? Y)?> _getToolSetterPosition;
        private readonly Func<ToolSetterConfig?> _getToolSetterConfig;

        private ToolChangePhase _phase = ToolChangePhase.NotStarted;
        private readonly object _phaseLock = new();
        private ToolChangeInfo? _currentToolChange;

        // The reference tool's length, measured at the start of a tool change and subtracted
        // from the new tool's to give the offset. ResetRunState clears it for each change.
        private double _referenceToolLength;

        private double _returnX;
        private double _returnY;

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

        public ToolChangeOptions Options { get; set; } = new();

        public event Action<ToolChangePhase>? PhaseChanged;

        /// <summary>
        /// The tool setter is read through callbacks, not captured once, so a change to the
        /// settings applies to the next tool change without rebuilding this controller.
        /// </summary>
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

        public async Task<bool> HandleToolChangeAsync(ToolChangeInfo info, CancellationToken ct = default)
        {
            if (State != ControllerState.Idle)
            {
                throw new InvalidControllerStateException(string.Format(ErrorCannotStart, State));
            }

            // This is the entry point, not StartAsync, so the reset the base class would
            // have done has to happen here.
            ResetRunState();

            _currentToolChange = info;
            ControllerLog.Log(LogToolChangeStart, info.ToolNumber);

            try
            {
                TransitionTo(ControllerState.Initializing);

                // Buffered commands finish first, so the position read below is where the
                // M6 actually left the tool.
                await MachineWait.WaitForIdleAsync(_machine, IdleWaitTimeoutMs, ct);

                _returnX = _machine.WorkPosition.X;
                _returnY = _machine.WorkPosition.Y;

                Phase = ToolChangePhase.RaisingZ;
                await RaiseZToClearanceAsync(ct);

                TransitionTo(ControllerState.Running);

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
                    // The run has reached its terminal state. Phase names the step of work and
                    // there is no step left, so the completion message is emitted here.
                    EmitProgress(new ProgressInfo(
                        nameof(ControllerState.Completing), ProgressPercentComplete, MessageToolChangeComplete));
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

        /// <inheritdoc/>
        protected override void ResetRunState()
        {
            lock (_phaseLock)
            {
                _phase = ToolChangePhase.NotStarted;
            }

            _currentToolChange = null;
            _returnX = 0;
            _returnY = 0;

            // A tool length measured for one tool change does not apply to the next, where
            // the operator has been free to fit anything.
            _referenceToolLength = 0;
        }

        protected override Task RunAsync(CancellationToken ct)
        {
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
            // Stops first and reports an unconfirmed lift, as the probe and mill runs do.
            // Retracting without stopping queued the lift behind a probe still descending.
            await StopAndLiftAsync(SafeClearanceZ, CancelRetractTimeoutMs).ConfigureAwait(false);
        }

        /// <summary>
        /// Raise Z to the clearance height, and stop the run if it cannot be confirmed. Every
        /// caller follows it with an XY rapid, and an unconfirmed retract means the tool may
        /// still be down.
        /// </summary>
        /// <exception cref="InvalidOperationException">The tool did not reach the height.</exception>
        private async Task RaiseZToClearanceAsync(CancellationToken ct)
        {
            if (!await MachineWait
                .SafetyRetractZAsync(_machine, SafeClearanceZ, ZHeightWaitTimeoutMs, ct)
                .ConfigureAwait(false))
            {
                throw new InvalidOperationException(ErrorSafetyRetractFailed);
            }
        }

        private async Task<bool> HandleWithToolSetterAsync((double X, double? Y) setterPos, CancellationToken ct)
        {
            // The reference tool is measured every time, and never persisted: the operator
            // may have changed it by hand between jobs.
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

            Phase = ToolChangePhase.RaisingZ;
            await RaiseZToClearanceAsync(ct);

            Phase = ToolChangePhase.MovingToWorkArea;
            await MoveToWorkAreaCenterAsync(ct);

            if (!await PromptForToolChangeAsync(ToolChangePrompt, ct))
            {
                return false;
            }

            Phase = ToolChangePhase.MovingToToolSetter;
            await MoveToToolSetterAsync(setterPos, ct);

            Phase = ToolChangePhase.MeasuringNewTool;
            var newLength = await ProbeToolSetterAsync(ct);
            if (newLength == null)
            {
                EmitError(new ControllerError(LogToolChangeProbeFailed, null, true));
                return false;
            }

            Phase = ToolChangePhase.ApplyingOffset;
            double offset = newLength.Value - _referenceToolLength;
            // Read G54 itself, not the combined WCO: WorkOffset is G54 + G92 + tool length
            // offset, while the write below is G10 L2 P1, which sets G54 alone. Starting from
            // the combined figure would re-datum Z at every tool change.
            if (!await _machine.RefreshWorkOffsetsAsync(WorkOffsetQueryTimeoutMs, ct))
            {
                throw new InvalidOperationException(ErrorWorkOffsetUnknown);
            }

            double currentWcoZ = _machine.G54Offset.Z;
            double newWcoZ = currentWcoZ + offset;
            ControllerLog.Log(LogToolChangeOffset, _referenceToolLength, newLength.Value, offset);
            _machine.SendLine(Inv($"{CmdSetWorkOffset} Z{newWcoZ:F3}"));
            await Task.Delay(CommandDelayMs, ct);

            // Read back before believing it. A rejected write leaves the new tool carrying
            // the old tool's length compensation, with the run reporting success.
            if (!await _machine.RefreshWorkOffsetsAsync(WorkOffsetQueryTimeoutMs, ct)
                || Math.Abs(_machine.G54Offset.Z - newWcoZ) > WorkOffsetToleranceMm)
            {
                throw new InvalidOperationException(ErrorToolOffsetNotTaken);
            }

            Phase = ToolChangePhase.Returning;
            await ReturnToPositionAsync(ct);

            return true;
        }

        private async Task<bool> HandleWithoutToolSetterAsync(CancellationToken ct)
        {
            if (!await PromptForToolChangeAsync(ToolChangePrompt, ct))
            {
                return false;
            }

            if (!await PromptForZeroZAsync(ct))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Asks the operator to jog to the PCB surface and set Z0 by hand. Only on the path
        /// for a machine with no tool setter; with one, HandleWithToolSetterAsync measures it.
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

            await EnsureDoorClosedAsync(ct, operatorJustAgreed: true).ConfigureAwait(false);
            return true;
        }

        /// <summary>Returns false when the operator aborted.</summary>
        private async Task<bool> PromptForToolChangeAsync(string promptFormat, CancellationToken ct)
        {
            Phase = ToolChangePhase.WaitingForToolChange;
            int toolNumber = _currentToolChange?.ToolNumber ?? 0;
            string? toolName = _currentToolChange?.ToolName;
            string prompt = string.IsNullOrWhiteSpace(toolName) || promptFormat != ToolChangePrompt
                ? string.Format(promptFormat, toolNumber)
                : string.Format(ToolChangePromptNamed, toolNumber, toolName);
            var response = await RequestUserInputAsync(
                ToolChangePromptTitle,
                prompt,
                new[] { OptionContinue, OptionAbort },
                ct);

            if (response == OptionAbort)
            {
                return false;
            }

            await EnsureDoorClosedAsync(ct, operatorJustAgreed: true).ConfigureAwait(false);
            return true;
        }

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
            double targetX = Options.WorkAreaCenter?.X ?? _returnX;
            double targetY = Options.WorkAreaCenter?.Y ?? _returnY;

            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdRapidMove} X{targetX:F3} Y{targetY:F3}"));
            await MachineWait.WaitForIdleAsync(_machine, MoveCompleteTimeoutMs, ct);
        }

        private async Task ReturnToPositionAsync(CancellationToken ct)
        {
            await RaiseZToClearanceAsync(ct);
            _machine.SendLine(Inv($"{CmdRapidMove} X{_returnX:F3} Y{_returnY:F3}"));
            await MachineWait.WaitForIdleAsync(_machine, MoveCompleteTimeoutMs, ct);
        }

        private async Task<double?> ProbeToolSetterAsync(CancellationToken ct)
        {
            var config = _getToolSetterConfig();
            double probeDepth = config?.ProbeDepth ?? ToolSetterProbeDepth;
            double fastFeed = config?.FastFeed ?? ToolSetterSeekFeed;
            double slowFeed = config?.SlowFeed ?? ToolSetterProbeFeed;
            double retract = config?.Retract ?? ToolSetterRetract;

            // No rapid pre-approach: the only height to aim one at is the trigger height of
            // the previous probe, taken with the previous tool, so a tool longer than the
            // clearance margin would be driven into the setter at rapid speed. The seek probe
            // starts from wherever Z is, which is what it is for.
            var (seekSuccess, seekZ) = await ExecuteProbeAsync(-probeDepth, fastFeed, ct);
            if (!seekSuccess)
            {
                return null;
            }

            _machine.SendLine(CmdAbsolute);
            _machine.SendLine(Inv($"{CmdMachineCoords} {CmdRapidMove} Z{seekZ + retract:F3}"));
            await MachineWait.WaitForIdleAsync(_machine, ZHeightWaitTimeoutMs, ct);

            double slowTarget = seekZ - 1.0;
            var (probeSuccess, probeZ) = await ExecuteProbeToMachineZAsync(slowTarget, slowFeed, ct);
            if (!probeSuccess)
            {
                return null;
            }

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
                MachineWait.OpenProbeCycle(_machine);
                _machine.SendLine(CmdAbsolute);
                _machine.SendLine(Inv($"{CmdProbeToward} Z{targetWorkZ:F3} F{feed:F1}"));

                using var registration = ct.Register(() => tcs.TrySetCanceled());

                return await MachineWait.AwaitReplyOrTimeoutAsync(
                    tcs.Task, ProbeReplyTimeoutMs, ErrorProbeTimeout, ct, _machine);
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
                _ => phase.ToString()
            };
        }
    }

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
