#nullable enable
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Polling for a test that waits on state a background loop changes, rather than on one
    /// awaitable. WaitUntilAsync is the async twin of <see cref="WebServerFixture.WaitUntil"/>.
    /// Each method fails the test with the reason it was given, so a failure names what the
    /// test expected.
    /// </summary>
    internal static class AsyncWait
    {
        private const int PollIntervalMs = 10;

        /// <summary>Returns once the condition holds; fails with <paramref name="what"/> after timeoutMs.</summary>
        public static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!condition())
            {
                if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                {
                    Assert.Fail($"Timed out after {timeoutMs}ms waiting for: {what}");
                }
                await Task.Delay(PollIntervalMs);
            }
        }

        /// <summary>Fails with <paramref name="because"/> the first time the condition is false before durationMs has passed.</summary>
        public static async Task AssertStaysTrueAsync(Func<bool> condition, int durationMs, string because)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < durationMs)
            {
                Assert.True(condition(), because);
                await Task.Delay(PollIntervalMs);
            }
        }
    }
}
