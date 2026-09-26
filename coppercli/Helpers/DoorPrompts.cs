using Spectre.Console;
using static coppercli.CliConstants;

namespace coppercli.Helpers
{
    /// <summary>
    /// How a terminal screen puts MachineWait.ClearDoorHoldAsync's questions to the operator:
    /// as an overlay on a full-screen view, or as lines on the scrolling console at startup.
    /// </summary>
    /// <param name="Ask">The closed-door question. True sends the cycle start.</param>
    /// <param name="Announce">A door state the operator cannot answer.</param>
    /// <param name="ShowFailure">Shown when the hold will not release.</param>
    internal sealed record DoorPrompts(Func<string, bool> Ask, Action<string> Announce, Action<string> ShowFailure)
    {
        public static readonly DoorPrompts Overlay = new(
            // Defaults to yes: this prompt only appears once GRBL reports the door closed,
            // which is what it asks about.
            message => DisplayHelpers.ShowOverlayConfirm(message, defaultYes: true) == true,
            message => DisplayHelpers.ShowOverlay(message, messageColor: DisplayHelpers.AnsiWarning),
            message => DisplayHelpers.ShowOverlayAndWait(message));

        public static readonly DoorPrompts ScrollingConsole = new(
            // A cycle start restarts the spindle and moves the tool back, so the operator
            // confirms it here as in a run.
            message => MenuHelpers.ConfirmOrExit(message, defaultYes: false),
            message => AnsiConsole.MarkupLine($"[{ColorWarning}]{Markup.Escape(message)}[/]"),
            MenuHelpers.ShowError);
    }
}
