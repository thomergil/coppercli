using System;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Request for user input during a controller operation.
    /// Controller emits this, UI shows prompt, UI calls OnResponse with selection.
    /// </summary>
    public class UserInputRequest
    {
        /// <summary>Unique ID for tracking this request.</summary>
        public string Id { get; init; } = Guid.NewGuid().ToString();

        /// <summary>Dialog title (e.g., "Confirmation Required", "Tool Change").</summary>
        public required string Title { get; init; }

        /// <summary>Message to display to the user.</summary>
        public required string Message { get; init; }

        /// <summary>Available options (e.g., ["Continue", "Abort"]).</summary>
        public required string[] Options { get; init; }

        /// <summary>Callback to invoke with the user's selection.</summary>
        public required Action<string> OnResponse { get; init; }
    }
}
