using System;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Request for user input during a controller operation.
    /// Controller emits this, UI shows prompt, UI calls OnResponse with selection.
    /// </summary>
    public class UserInputRequest
    {
        /// <summary>
        /// Identifies this prompt. An answer names it, and PendingPrompt.Answer rejects one
        /// that names another, because a run publishes its next prompt from inside the call
        /// that answers this one.
        /// </summary>
        public string Id { get; init; } = Guid.NewGuid().ToString();

        /// <summary>
        /// The prompt's heading: "Tool Change", "Set Z Zero" or "Program Paused". Empty for
        /// the enclosure prompt, whose message already names the enclosure.
        /// </summary>
        public required string Title { get; init; }

        /// <summary>Message to display to the user.</summary>
        public required string Message { get; init; }

        /// <summary>Available options (e.g., ["Continue", "Abort"]).</summary>
        public required string[] Options { get; init; }

        /// <summary>
        /// True for the enclosure prompt. A screen that draws a heading over a run's prompt
        /// ("TOOL CHANGE") must not draw it over this one, whose Continue releases the door
        /// hold and restarts the spindle. Set where the prompt is raised, so no screen has to
        /// recognise it by its text.
        /// </summary>
        public bool IsDoorPrompt { get; init; }

        /// <summary>Callback to invoke with the user's selection.</summary>
        public required Action<string> OnResponse { get; init; }
    }
}
