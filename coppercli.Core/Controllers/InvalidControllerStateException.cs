using System;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// The state machine refused a call: a run started while one is going, a pause on a run
    /// that is not running, a transition the table does not allow. Its own type because its
    /// message names states, which <see cref="ControllerBase"/> keeps off the operator's
    /// screen.
    /// </summary>
    public class InvalidControllerStateException : InvalidOperationException
    {
        public InvalidControllerStateException(string message) : base(message)
        {
        }
    }
}
