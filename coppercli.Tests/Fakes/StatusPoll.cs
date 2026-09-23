#nullable enable
using System;
using coppercli.Core.Util;

namespace coppercli.Tests.Fakes
{
    /// <summary>
    /// GRBL answers the status poll many times a second whether or not anything has changed,
    /// and MachineWait counts those answers to give the door switch's reading time to catch
    /// up. A double that reports only when its own state changes never produces them, so
    /// that wait runs to its timeout instead.
    ///
    /// The count is derived from how long GRBL has been answering rather than ticked by a
    /// timer, so a double needs no background thread and nothing to dispose.
    /// </summary>
    public sealed class StatusPoll
    {
        /// <summary>
        /// The machine's own poll interval, so a wait written against it counts the same
        /// number of reports here as it does on the machine.
        /// </summary>
        public const int IntervalMs = Constants.StatusPollIntervalMs;

        private readonly object _lock = new();
        private long _reports;
        private long _sinceMs = Environment.TickCount64;
        private bool _answering = true;

        /// <summary>Status reports GRBL has answered. Set it to start counting from a value.</summary>
        public long Count
        {
            get { lock (_lock) { return Settle(); } }
            set { lock (_lock) { Settle(); _reports = value; } }
        }

        /// <summary>
        /// False while GRBL is busy in a routine that services no status query: the homing
        /// cycle, and the reboot after a soft reset. The count holds where it was until it
        /// answers again, which is what a caller counting reports sees on the machine.
        /// </summary>
        public bool Answering
        {
            get { lock (_lock) { return _answering; } }
            set { lock (_lock) { Settle(); _answering = value; } }
        }

        /// <summary>Adds the reports answered since the last change to the total.</summary>
        private long Settle()
        {
            long now = Environment.TickCount64;

            if (!_answering)
            {
                _sinceMs = now;
                return _reports;
            }

            // Carry the part of an interval that has not produced a report yet. Moving the
            // mark to now instead would discard it, and a second reader asking twice per
            // interval would reset the mark before a report was ever due, so the count
            // would stand still while time passed.
            long answered = (now - _sinceMs) / IntervalMs;
            _reports += answered;
            _sinceMs += answered * IntervalMs;
            return _reports;
        }
    }
}
