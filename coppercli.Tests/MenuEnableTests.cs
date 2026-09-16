using coppercli.Helpers;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// A menu entry is selectable exactly when nothing blocks it, and the reason shown
    /// when it is blocked comes from the same check. Spelled out separately, the two can
    /// disagree, and Mill would be offered with a height map whose origin has moved.
    /// </summary>
    public class MenuEnableTests
    {
        [Fact]
        public void EveryBlockingMillErrorProducesAReasonToShow()
        {
            // A blocked entry with nothing to say leaves the operator guessing why. The
            // alarm case is deliberately excluded: the ready-check reports that one.
            foreach (MillPreflightError error in System.Enum.GetValues<MillPreflightError>())
            {
                string? reason = MenuHelpers.DescribeMillBlockingError(
                    new MillPreflightResult(false, error, new System.Collections.Generic.List<MillPreflightWarning>(), "0/9"));

                bool expectsReason = error != MillPreflightError.None
                    && error != MillPreflightError.AlarmState;

                Assert.Equal(expectsReason, !string.IsNullOrWhiteSpace(reason));
            }
        }
    }
}
