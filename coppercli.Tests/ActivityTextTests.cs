#nullable enable
using coppercli.Core.Controllers;
using coppercli.Core.Util;
using coppercli.Helpers;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>The words the terminal's status line shows for what the machine is doing.</summary>
    public class ActivityTextTests
    {
        /// <summary>
        /// GRBL reports the park retract until the move ends, even after the operator has
        /// closed the door, so the status line must not call it an open door.
        /// </summary>
        [Fact]
        public void TheRetract_IsNotShownAsAnOpenDoor()
        {
            Assert.Equal(CliConstants.DoorRetractingStatus,
                DisplayHelpers.GetActivityText(MachineActivity.DoorRetracting, GrblProtocol.StatusDoor));
        }
    }
}
