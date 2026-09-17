using System;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// A question a controller puts to the operator. The controller raises it, the UI draws it,
    /// and the UI calls `OnResponse` with one of `Options`.
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

        public required string Message { get; init; }

        public required string[] Options { get; init; }

        /// <summary>
        /// True for the enclosure prompt, set where the prompt is raised so that no screen has
        /// to recognise it by its text. Answering Continue releases the door hold and restarts
        /// the spindle, so a screen must not draw a run's heading ("TOOL CHANGE") over it.
        /// </summary>
        public bool IsDoorPrompt { get; init; }

        public required Action<string> OnResponse { get; init; }
    }
}
