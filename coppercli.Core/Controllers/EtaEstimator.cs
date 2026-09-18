using System;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Estimate remaining job time from completed lines. Use the toolpath estimate until
    /// enough lines have completed to measure the machine's actual rate.
    ///
    /// The estimate must be able to increase. If the model estimate is weighted by
    /// (1 - fraction complete), remaining time is proportional to (1-f)(1 + f(k-1)) for a
    /// machine running k times slower than the model. Its initial slope is (k-2), so
    /// the estimate cannot increase when k is less than 2.
    ///
    /// Project an exponential moving average of seconds per line over the remaining lines:
    ///
    ///   - Smooth over a fraction of the job so the caller's redraw rate does not affect
    ///     how quickly the estimate changes.
    ///   - Add time spent on the current line separately, so one long cut is not projected
    ///     over every remaining line, while a stall can still increase the estimate.
    ///
    /// The elapsed time passed to <see cref="Update"/> must include milling time only.
    /// </summary>
    public sealed class EtaEstimator
    {
        /// <summary>Share of the job over which the measured pace displaces the model guess.</summary>
        private const double WarmupFraction = 0.02;

        /// <summary>Share of the job the moving average remembers. Larger reacts more slowly.</summary>
        private const double SmoothingFraction = 0.10;

        private readonly int _totalLines;
        private readonly double _modelSecondsPerLine;
        private readonly bool _haveModel;
        private readonly double _warmupLines;
        private readonly double _smoothingLines;

        private int _lastLines;
        private double _lastAdvanceSeconds;
        private double _measuredLines;
        private double _pace;
        private bool _havePace;

        /// <summary>
        /// Pass zero or a negative modelEstimate where the toolpath model could not estimate,
        /// for example a file with no feed moves; only the measured pace is then used.
        /// </summary>
        public EtaEstimator(TimeSpan modelEstimate, int totalLines)
        {
            _totalLines = Math.Max(1, totalLines);
            _haveModel = modelEstimate > TimeSpan.Zero;
            _modelSecondsPerLine = _haveModel ? modelEstimate.TotalSeconds / _totalLines : 0.0;
            _warmupLines = Math.Max(1.0, WarmupFraction * _totalLines);
            _smoothingLines = Math.Max(1.0, SmoothingFraction * _totalLines);
        }

        /// <summary>
        /// Returns the time still to go. Null while there is nothing to base an estimate on -
        /// no model guess and no measured progress yet.
        /// </summary>
        public TimeSpan? Update(int linesCompleted, TimeSpan elapsed)
        {
            double elapsedSeconds = elapsed.TotalSeconds;

            // Rewinding (a restart, or a rewound abort) invalidates the baseline the deltas
            // are measured from, but not the pace already learned.
            if (linesCompleted < _lastLines || elapsedSeconds < _lastAdvanceSeconds)
            {
                _lastLines = linesCompleted;
                _lastAdvanceSeconds = elapsedSeconds;
            }

            int deltaLines = linesCompleted - _lastLines;
            double deltaSeconds = elapsedSeconds - _lastAdvanceSeconds;

            if (deltaLines > 0 && deltaSeconds > 0)
            {
                double instantPace = deltaSeconds / deltaLines;
                double weight = Math.Clamp(deltaLines / _smoothingLines, 0.0, 1.0);
                _pace = _havePace ? weight * instantPace + (1 - weight) * _pace : instantPace;
                _havePace = true;
                _measuredLines += deltaLines;
                _lastLines = linesCompleted;
                _lastAdvanceSeconds = elapsedSeconds;
                deltaSeconds = 0;
            }

            if (!_havePace)
            {
                if (!_haveModel)
                {
                    return null;
                }
                double guessRemaining = _modelSecondsPerLine * _totalLines - elapsedSeconds;
                return TimeSpan.FromSeconds(Math.Max(0, guessRemaining));
            }

            // Until enough lines have run to measure a pace worth trusting, blend in the guess.
            double rate = _pace;
            if (_haveModel)
            {
                double trust = Math.Clamp(_measuredLines / _warmupLines, 0.0, 1.0);
                rate = (1 - trust) * _modelSecondsPerLine + trust * _pace;
            }

            // Time already spent on the line in progress beyond what its pace predicted.
            // Counted once, for this line only - not extrapolated across the rest.
            double currentLineOverrun = Math.Max(0, deltaSeconds - rate);

            double remaining = rate * (_totalLines - linesCompleted) + currentLineOverrun;
            return TimeSpan.FromSeconds(Math.Max(0, remaining));
        }
    }
}
