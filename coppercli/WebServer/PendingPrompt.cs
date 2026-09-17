using System;
using System.Threading;
using coppercli.Core.Controllers;

namespace coppercli.WebServer;

internal enum PromptAnswerResult
{
    Accepted,

    NothingPending,

    WrongPrompt,

    NotAnOption,
}

/// <summary>
/// The one prompt a run is waiting on, and the rule for answering it. An answer names the
/// prompt it answers, because answering resumes the run on this thread and the run can
/// publish its next prompt before the answer returns.
///
/// The tool-change controller's prompts and the milling controller's M0/M1 prompt share this
/// slot. The run sends one line at a time, so they cannot both be pending.
/// </summary>
internal static class PendingPrompt
{
    // Written by a run's thread, read by the status loop and by whichever request answers
    // it, so every access goes through Volatile or Interlocked.
    private static UserInputRequest? _request;

    public static UserInputRequest? Current => Volatile.Read(ref _request);

    public static void Set(UserInputRequest request) => Volatile.Write(ref _request, request);

    /// <summary>
    /// Clear the slot as a run ends, if <paramref name="published"/> is still in it. By then
    /// another run may have published its own.
    /// </summary>
    public static void ClearIfCurrent(UserInputRequest? published)
    {
        if (published != null)
        {
            Interlocked.CompareExchange(ref _request, null, published);
        }
    }

    /// <summary>
    /// Hand <paramref name="response"/> to the run, if <paramref name="promptId"/> names the
    /// prompt now waiting and the response is one of its options. The slot is cleared before
    /// the run resumes, because resuming publishes the next prompt into it.
    /// </summary>
    public static PromptAnswerResult Answer(string? promptId, string? response)
    {
        var pending = Volatile.Read(ref _request);
        if (pending == null)
        {
            return PromptAnswerResult.NothingPending;
        }

        if (promptId != pending.Id)
        {
            return PromptAnswerResult.WrongPrompt;
        }

        // A workflow compares the answer to its options with ==, so "abort" with a stray
        // space would be read as continue.
        if (response == null || Array.IndexOf(pending.Options, response) < 0)
        {
            return PromptAnswerResult.NotAnOption;
        }

        if (!ReferenceEquals(Interlocked.CompareExchange(ref _request, null, pending), pending))
        {
            return PromptAnswerResult.WrongPrompt;
        }

        pending.OnResponse?.Invoke(response);
        return PromptAnswerResult.Accepted;
    }
}
