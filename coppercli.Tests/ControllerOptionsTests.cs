using coppercli.Core.Controllers;
using coppercli.Core.Settings;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Options were built from settings in several places and had drifted (the web grid
    /// probe silently dropped trace height/feed). One factory per type now fills every
    /// field, so any two call sites get identical options from identical settings.
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

            var o = ProbeOptions.FromSettings(s, traceOutline: true, sourceFile: "/x.ngc");

            Assert.Equal(7, o.SafeHeight);
            Assert.Equal(9, o.MaxDepth);
            Assert.Equal(42, o.ProbeFeed);
            Assert.Equal(2, o.MinimumHeight);
            Assert.True(o.AbortOnFail);
            Assert.Equal(0.3, o.XAxisWeight);
            Assert.Equal(4, o.TraceHeight);       // the field the web copy used to drop
            Assert.Equal(333, o.TraceFeed);       // ditto
            Assert.True(o.TraceOutline);
            Assert.Equal("/x.ngc", o.SourceFile);
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
