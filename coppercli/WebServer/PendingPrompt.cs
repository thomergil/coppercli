using System;
using System.Threading;
using coppercli.Core.Controllers;

namespace coppercli.WebServer;

/// <summary>Why an answer to a prompt was or was not accepted.</summary>
internal enum PromptAnswerResult
{
    /// <summary>The answer reached the run, which is no longer waiting.</summary>
    Accepted,

    /// <summary>Nothing is waiting on an answer.</summary>
    NothingPending,

    /// <summary>The answer named a prompt other than the one now waiting.</summary>
    WrongPrompt,

    /// <summary>The answer was not one of the choices the question offered.</summary>
    NotAnOption,
}

/// <summary>
/// The one question a run is waiting on, and the rule for answering it. An answer names the
/// question it answers, because answering resumes the run on this thread and the run can
/// publish its next question before the answer returns.
///
/// The tool-change controller's prompts and the milling controller's M0/M1 prompt share this
/// slot; the run sends one line at a time, so they cannot both be pending.
/// </summary>
internal static class PendingPrompt
{
    // Written by a run's thread, read by the status loop and by whichever request answers
    // it, so every access goes through Volatile or Interlocked.
    private static UserInputRequest? _request;

    /// <summary>The question now waiting, or null if none is.</summary>
    public static UserInputRequest? Current => Volatile.Read(ref _request);

    /// <summary>Publish a question.</summary>
    public static void Set(UserInputRequest request) => Volatile.Write(ref _request, request);

    /// <summary>
    /// Clear the slot as a run ends, if <paramref name="published"/> is still in it. By then
    /// the run it hands back to may have asked one of its own.
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
    /// question now waiting and the response is one of its choices. The slot is emptied
    /// before the run resumes, because resuming publishes the next question into it.
    /// </summary>
    public static PromptAnswerResult Answer(string? promptId, string? response)
    {
        var pending = Volatile.Read(ref _request);
        if (pending == null)
        {
            return PromptAnswerResult.NothingPending;
        }

        // Whose question it is comes before whether the answer is usable.
        if (promptId != pending.Id)
        {
            return PromptAnswerResult.WrongPrompt;
        }

        // A workflow compares the answer to one of its options with ==, so "abort" with a
        // stray space would be read as continue.
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
