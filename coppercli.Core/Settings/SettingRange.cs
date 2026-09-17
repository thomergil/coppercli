#nullable enable
using System;
using System.Globalization;

namespace coppercli.Core.Settings
{
    /// <summary>
    /// What one machine setting may be. Every value reaches the machine as a number in a
    /// G-code line or a move.
    /// </summary>
    /// <param name="Name">What the operator calls this setting.</param>
    /// <param name="Unit">Its unit, used in the refusal message.</param>
    /// <param name="MustBePositive">Whether zero and below are refused.</param>
    public readonly record struct SettingRange(string Name, string Unit, bool MustBePositive)
    {
        /// <summary>Why this value is not usable, or null if it is.</summary>
        public string? Check(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return string.Format(
                    CultureInfo.InvariantCulture, SettingsText.NotANumber, Name);
            }

            if (MustBePositive && value <= 0)
            {
                return string.Format(
                    CultureInfo.InvariantCulture, SettingsText.MustBePositive, Name, Unit);
            }

            return null;
        }
    }

    /// <summary>What the operator is told when a setting is refused.</summary>
    public static class SettingsText
    {
        public const string NotANumber = "{0} must be a number.";
        public const string MustBePositive = "{0} must be more than 0 {1}.";
    }

    /// <summary>
    /// One setting: its range, and how to read and write it. Paired here so no caller lists
    /// the pairs again.
    /// </summary>
    public readonly record struct SettingBinding(
        SettingRange Range,
        Func<MachineSettings, double> Read,
        Action<MachineSettings, double> Write);

    /// <summary>
    /// Every numeric setting whose value reaches the machine, read by the terminal, the web
    /// API and the settings file alike. Only bounds that hold for any machine are here: an
    /// upper limit would come from the machine's travel and maximum rate, which coppercli
    /// does not read.
    /// </summary>
    public static class SettingRanges
    {
        private const string MmPerMin = "mm/min";
        private const string Mm = "mm";
        private const string Ms = "ms";
        private const string Bytes = "bytes";

        public static readonly SettingBinding ProbeFeed = new(
            new SettingRange("Probe feed", MmPerMin, MustBePositive: true),
            s => s.ProbeFeed, (s, v) => s.ProbeFeed = v);

        public static readonly SettingBinding ProbeMaxDepth = new(
            new SettingRange("Probe max depth", Mm, MustBePositive: true),
            s => s.ProbeMaxDepth, (s, v) => s.ProbeMaxDepth = v);

        public static readonly SettingBinding ProbeSafeHeight = new(
            new SettingRange("Probe safe height", Mm, MustBePositive: true),
            s => s.ProbeSafeHeight, (s, v) => s.ProbeSafeHeight = v);

        public static readonly SettingBinding ProbeMinimumHeight = new(
            new SettingRange("Probe minimum height", Mm, MustBePositive: true),
            s => s.ProbeMinimumHeight, (s, v) => s.ProbeMinimumHeight = v);

        public static readonly SettingBinding OutlineTraceHeight = new(
            new SettingRange("Outline trace height", Mm, MustBePositive: true),
            s => s.OutlineTraceHeight, (s, v) => s.OutlineTraceHeight = v);

        public static readonly SettingBinding OutlineTraceFeed = new(
            new SettingRange("Outline trace feed", MmPerMin, MustBePositive: true),
            s => s.OutlineTraceFeed, (s, v) => s.OutlineTraceFeed = v);

        public static readonly SettingBinding JogFeed = new(
            new SettingRange("Jog feed", MmPerMin, MustBePositive: true),
            s => s.JogFeed, (s, v) => s.JogFeed = v);

        public static readonly SettingBinding JogDistance = new(
            new SettingRange("Jog distance", Mm, MustBePositive: true),
            s => s.JogDistance, (s, v) => s.JogDistance = v);

        public static readonly SettingBinding JogFeedSlow = new(
            new SettingRange("Slow jog feed", MmPerMin, MustBePositive: true),
            s => s.JogFeedSlow, (s, v) => s.JogFeedSlow = v);

        public static readonly SettingBinding JogDistanceSlow = new(
            new SettingRange("Slow jog distance", Mm, MustBePositive: true),
            s => s.JogDistanceSlow, (s, v) => s.JogDistanceSlow = v);

        /// <summary>Added to the probed position before it becomes a height map node.</summary>
        public static readonly SettingBinding ProbeOffsetX = new(
            new SettingRange("Probe offset X", Mm, MustBePositive: false),
            s => s.ProbeOffsetX, (s, v) => s.ProbeOffsetX = v);

        /// <inheritdoc cref="ProbeOffsetX"/>
        public static readonly SettingBinding ProbeOffsetY = new(
            new SettingRange("Probe offset Y", Mm, MustBePositive: false),
            s => s.ProbeOffsetY, (s, v) => s.ProbeOffsetY = v);

        /// <summary>How much the interpolation uses the points to either side in X.</summary>
        public static readonly SettingBinding ProbeXAxisWeight = new(
            new SettingRange("Probe X axis weight", "", MustBePositive: false),
            s => s.ProbeXAxisWeight, (s, v) => s.ProbeXAxisWeight = v);

        // Zero leaves no room for the next line, so the job starts and sends nothing.
        public static readonly SettingBinding ControllerBufferSize = new(
            new SettingRange("Controller buffer size", Bytes, MustBePositive: true),
            s => s.ControllerBufferSize, (s, v) => s.ControllerBufferSize = (int)v);

        // Zero polls as fast as the loop runs.
        public static readonly SettingBinding StatusPollInterval = new(
            new SettingRange("Status poll interval", Ms, MustBePositive: true),
            s => s.StatusPollInterval, (s, v) => s.StatusPollInterval = (int)v);

        /// <summary>Where the tool setter sits. Zero means there is none.</summary>
        public static readonly SettingBinding ToolSetterX = new(
            new SettingRange("Tool setter X", Mm, MustBePositive: false),
            s => s.ToolSetterX, (s, v) => s.ToolSetterX = v);

        /// <inheritdoc cref="ToolSetterX"/>
        public static readonly SettingBinding ToolSetterY = new(
            new SettingRange("Tool setter Y", Mm, MustBePositive: false),
            s => s.ToolSetterY, (s, v) => s.ToolSetterY = v);

        /// <summary>Every binding above, for the settings loader and its test.</summary>
        public static readonly SettingBinding[] All =
        {
            ProbeFeed, ProbeMaxDepth, ProbeSafeHeight, ProbeMinimumHeight,
            OutlineTraceHeight, OutlineTraceFeed,
            JogFeed, JogDistance, JogFeedSlow, JogDistanceSlow,
            ProbeOffsetX, ProbeOffsetY, ProbeXAxisWeight,
            ControllerBufferSize, StatusPollInterval,
            ToolSetterX, ToolSetterY
        };
    }
}
