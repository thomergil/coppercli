using coppercli.Core.Controllers;
using coppercli.Core.Settings;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// One factory per option type fills every field from settings, so any two call sites
    /// get identical options from identical settings.
    /// </summary>
    public class ControllerOptionsTests
    {
        [Fact]
        public void ProbeOptions_FromSettings_MapsEveryConfiguredField()
        {
            var s = new MachineSettings
            {
                ProbeSafeHeight = 7, ProbeMaxDepth = 9, ProbeFeed = 42,
                ProbeMinimumHeight = 2, AbortOnProbeFail = true, ProbeXAxisWeight = 0.3,
                OutlineTraceHeight = 4, OutlineTraceFeed = 333
            };

            var o = ProbeOptions.FromSettings(s, traceOutline: true);

            Assert.Equal(7, o.SafeHeight);
            Assert.Equal(9, o.MaxDepth);
            Assert.Equal(42, o.ProbeFeed);
            Assert.Equal(2, o.MinimumHeight);
            Assert.True(o.AbortOnFail);
            Assert.Equal(0.3, o.XAxisWeight);
            Assert.Equal(4, o.TraceHeight);
            Assert.Equal(333, o.TraceFeed);
            Assert.True(o.TraceOutline);
        }

        /// <summary>
        /// The height check has no setting behind it, so every run must get the shipped
        /// tolerance rather than a zero that would silently accept every reading.
        /// </summary>
        [Fact]
        public void ProbeOptions_FromSettings_CarriesTheShippedHeightTolerance()
        {
            var o = ProbeOptions.FromSettings(new MachineSettings());

            Assert.Equal(ControllerConstants.ProbeHeightDeviationToleranceMm,
                o.HeightDeviationTolerance);
            Assert.True(o.HeightDeviationTolerance > 0);
        }

        [Fact]
        public void ProbeOptions_FromSameSettings_AreIdenticalAcrossCallSites()
        {
            var s = new MachineSettings { ProbeSafeHeight = 5, OutlineTraceHeight = 3, OutlineTraceFeed = 200 };

            var tui = ProbeOptions.FromSettings(s, traceOutline: false);
            var web = ProbeOptions.FromSettings(s, traceOutline: false);

            Assert.Equal(tui.SafeHeight, web.SafeHeight);
            Assert.Equal(tui.TraceHeight, web.TraceHeight);
            Assert.Equal(tui.TraceFeed, web.TraceFeed);
            Assert.Equal(tui.AbortOnFail, web.AbortOnFail);
        }

        [Fact]
        public void MillingOptions_Create_SetsRequireHomingFromNotHomed()
        {
            Assert.True(MillingOptions.Create("f.nc", 0f, machineIsHomed: false).RequireHoming);
            Assert.False(MillingOptions.Create("f.nc", 0f, machineIsHomed: true).RequireHoming);
        }

        [Fact]
        public void ToolChangeOptions_FromSettings_WithNoFile_HasNoWorkAreaCenter()
        {
            var o = ToolChangeOptions.FromSettings(new MachineSettings(), file: null);
            Assert.Null(o.WorkAreaCenter);
        }
    }
}
