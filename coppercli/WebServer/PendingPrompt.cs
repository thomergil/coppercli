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
/// Store the current run prompt and validate answers against its id. Answering resumes
/// the run on the same thread, which may publish the next prompt before the call returns.
///
/// Tool-change and M0/M1 prompts use the same field; only one can be pending.
/// </summary>
internal static class PendingPrompt
{
    // Written by a run's thread, read by the status loop and by whichever request answers
    // it, so every access goes through Volatile or Interlocked.
    private static UserInputRequest? _request;

    public static UserInputRequest? Current => Volatile.Read(ref _request);

    public static void Set(UserInputRequest request) => Volatile.Write(ref _request, request);

    /// <summary>
    /// Clear <paramref name="published"/> only if it is still the current prompt.
    /// </summary>
    public static void ClearIfCurrent(UserInputRequest? published)
    {
        if (published != null)
        {
            Interlocked.CompareExchange(ref _request, null, published);
        }
    }

    /// <summary>
    /// Send <paramref name="response"/> when <paramref name="promptId"/> matches the
    /// current prompt and the response is one of its options. Clear the field before
    /// resuming the run, which may publish its next prompt immediately.
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
