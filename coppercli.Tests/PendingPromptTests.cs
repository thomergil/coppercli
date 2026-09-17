using System.Collections.Generic;
using coppercli.Core.Controllers;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// An answer names the prompt it answers, because answering publishes the next prompt
    /// from inside the answering call. These tests share one static slot; xUnit runs a
    /// class's tests one at a time and no other class touches it.
    /// </summary>
    public class PendingPromptTests
    {
        private static void ClearPendingPrompt() => PendingPrompt.ClearIfCurrent(PendingPrompt.Current);

        private static UserInputRequest Prompt(List<string> answers) => new()
        {
            Title = "Change the tool",
            Message = "Then press Continue",
            Options = new[] { "Continue", "Abort" },
            OnResponse = answers.Add
        };

        [Fact]
        public void AnAnswerNamingThePendingPrompt_ReachesTheRun()
        {
            var answers = new List<string>();
            var prompt = Prompt(answers);
            PendingPrompt.Set(prompt);

            Assert.Equal(PromptAnswerResult.Accepted, PendingPrompt.Answer(prompt.Id, "Continue"));
            Assert.Equal(new[] { "Continue" }, answers);
            Assert.Null(PendingPrompt.Current);
        }

        /// <summary>
        /// Models a second tap on the button of a prompt the run has already replaced.
        /// </summary>
        [Fact]
        public void AnAnswerToAReplacedPrompt_IsRefused()
        {
            var answers = new List<string>();
            var first = Prompt(answers);
            PendingPrompt.Set(first);
            PendingPrompt.Answer(first.Id, "Continue");

            var second = Prompt(answers);
            PendingPrompt.Set(second);

            Assert.Equal(PromptAnswerResult.WrongPrompt, PendingPrompt.Answer(first.Id, "Continue"));
            Assert.Single(answers);
            Assert.Same(second, PendingPrompt.Current);

            ClearPendingPrompt();
        }

        [Fact]
        public void TwoAnswersToOnePrompt_ReachTheRunOnce()
        {
            var answers = new List<string>();
            var prompt = Prompt(answers);
            PendingPrompt.Set(prompt);

            Assert.Equal(PromptAnswerResult.Accepted, PendingPrompt.Answer(prompt.Id, "Continue"));
            Assert.Equal(PromptAnswerResult.NothingPending, PendingPrompt.Answer(prompt.Id, "Continue"));
            Assert.Single(answers);
        }

        [Fact]
        public void AnAnswerWithNoPromptPending_IsRefused()
        {
            ClearPendingPrompt();
            Assert.Equal(PromptAnswerResult.NothingPending, PendingPrompt.Answer("any", "Continue"));
        }

        /// <summary>
        /// A run ending clears only the prompt it published: another run may already have
        /// published its own.
        /// </summary>
        [Fact]
        public void EndingARun_ClearsOnlyItsOwnPrompt()
        {
            var answers = new List<string>();
            var mine = Prompt(answers);
            var theirs = Prompt(answers);

            PendingPrompt.Set(theirs);
            PendingPrompt.ClearIfCurrent(mine);
            Assert.Same(theirs, PendingPrompt.Current);

            PendingPrompt.ClearIfCurrent(theirs);
            Assert.Null(PendingPrompt.Current);
        }

        /// <summary>
        /// ToolChangeController continues on anything that is not exactly "Abort", so only
        /// an exact option reaches it.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("abort")]
        [InlineData("Continue ")]
        [InlineData("anything")]
        public void AnAnswerThatIsNotAnOption_IsRefused(string? response)
        {
            var answers = new List<string>();
            var prompt = Prompt(answers);
            PendingPrompt.Set(prompt);

            Assert.Equal(PromptAnswerResult.NotAnOption, PendingPrompt.Answer(prompt.Id, response));
            Assert.Empty(answers);
            Assert.Same(prompt, PendingPrompt.Current);

            ClearPendingPrompt();
        }

        /// <summary>
        /// Answering resumes the run on this thread, and the run publishes its next prompt
        /// into the slot before the answer returns. Clearing the slot afterwards would
        /// discard that prompt.
        /// </summary>
        [Fact]
        public void Answering_LeavesTheNextPromptInTheSlot()
        {
            var answers = new List<string>();
            UserInputRequest? second = null;
            var first = new UserInputRequest
            {
                Title = "Change the tool",
                Message = "Then press Continue",
                Options = new[] { "Continue", "Abort" },
                OnResponse = answer =>
                {
                    answers.Add(answer);
                    second = Prompt(answers);
                    PendingPrompt.Set(second);
                }
            };
            PendingPrompt.Set(first);

            Assert.Equal(PromptAnswerResult.Accepted, PendingPrompt.Answer(first.Id, "Continue"));
            Assert.Same(second, PendingPrompt.Current);
            Assert.Equal(PromptAnswerResult.WrongPrompt, PendingPrompt.Answer(first.Id, "Continue"));
            Assert.Single(answers);

            ClearPendingPrompt();
        }

        [Fact]
        public void AnAnswerNamingNoPrompt_IsRefused()
        {
            var answers = new List<string>();
            var prompt = Prompt(answers);
            PendingPrompt.Set(prompt);

            Assert.Equal(PromptAnswerResult.WrongPrompt, PendingPrompt.Answer(null, "Continue"));
            Assert.Empty(answers);

            ClearPendingPrompt();
        }
    }
}
