using coppercli.Core.Controllers;
using coppercli.Helpers;
using Xunit;
using static coppercli.CliConstants;

namespace coppercli.Tests
{
    /// <summary>
    /// A menu entry is selectable exactly when nothing blocks it, and the reason shown when
    /// it is blocked comes from the same check. Written separately they can disagree, and
    /// Mill would be offered with a height map whose origin has moved.
    /// </summary>
    public class MenuEnableTests
    {
        /// <summary>
        /// Both the Start button and the text shown in its place read MillStartCheck.CanStart.
        /// A CanStart that returned true on an alarm, a missing file or an unapplied height
        /// map would start the job, and every reason-text test would still pass.
        /// </summary>
        [Fact]
        public void CanStart_IsExactlyTheAbsenceOfABlocker()
        {
            foreach (MillBlocker error in System.Enum.GetValues<MillBlocker>())
            {
                var check = new MillStartCheck(
                    error, new System.Collections.Generic.List<MillWarning>(), "0/9");

                Assert.Equal(error == MillBlocker.None, check.CanStart);

                // A blocked start with no reason, or a reason beside an enabled button, is
                // the disagreement this covers.
                Assert.Equal(check.CanStart, MenuHelpers.GetMillBlockerReason(check) == null);
            }
        }

        [Fact]
        public void EveryMillBlocker_HasText()
        {
            // Non-empty is not enough: the fallback arm returns "unknown error" for any
            // unmapped member, which would still look like a reason.
            foreach (MillBlocker error in System.Enum.GetValues<MillBlocker>())
            {
                string? reason = MenuHelpers.GetMillBlockerReason(
                    new MillStartCheck(error, new System.Collections.Generic.List<MillWarning>(), "0/9"));

                if (error == MillBlocker.None)
                {
                    Assert.Null(reason);
                    continue;
                }

                Assert.False(string.IsNullOrWhiteSpace(reason), $"{error} has no reason to show");
                Assert.NotEqual(DisabledUnknown, reason);
            }
        }

        [Fact]
        public void AnAlarm_IsReportedAsAnAlarm()
        {
            Assert.Equal(DisabledAlarm, MenuHelpers.GetMillBlockerReason(
                new MillStartCheck(MillBlocker.AlarmState,
                    new System.Collections.Generic.List<MillWarning>(), null)));
        }


        [Fact]
        public void ABlockedItem_IsNotSelectableAndReportsTheReason()
        {
            var blocked = new MenuItem<int>("Probe", 'p', 1, Blocker: () => "a job is running");

            Assert.False(blocked.IsEnabled);
            Assert.Equal("a job is running", blocked.CurrentDisabledReason);
        }

        [Fact]
        public void AnItemWithNothingBlockingIt_IsSelectableWithNoReason()
        {
            var open = new MenuItem<int>("Probe", 'p', 1, Blocker: () => null);

            Assert.True(open.IsEnabled);
            Assert.Null(open.CurrentDisabledReason);
        }

        [Fact]
        public void AnItemWithNoBlocker_IsSelectable()
        {
            var always = new MenuItem<int>("Quit", 'q', 1);

            Assert.True(always.IsEnabled);
            Assert.Null(always.CurrentDisabledReason);
        }
    }
}
