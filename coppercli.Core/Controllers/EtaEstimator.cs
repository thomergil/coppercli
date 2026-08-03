using System;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// Estimates the time remaining in a job: it starts from an up-front guess and shifts
    /// its trust toward the measured pace as the job progresses.
    ///
    /// The display it replaced took its whole estimate from elapsed / lines-done, with the
    /// clock started before homing - so the first figure was dominated by setup time and
    /// swung wildly before settling. This instead blends two projections of the total
    /// duration by how far the job has come:
    ///
    ///   - the toolpath's own model estimate (feed rates times path lengths), known before
    ///     the first cut - the guess;
    ///   - the pace measured so far, projected across the whole job.
    ///
    /// Weighting by fraction-complete means the guess dominates at the start (so the number
    /// is steady, and early measurement noise - which is largest when only a few lines have
    /// run - barely registers) and the measurement dominates by the end (so it lands on the
    /// truth). The shift is gradual and monotonic, with no jumps and nothing to tune.
    ///
    /// The clock it is fed must measure milling only, not the setup phases before it.
    /// </summary>
    public sealed class EtaEstimator
    {
        private readonly int _totalLines;
        private readonly double _modelTotalSeconds;
        private readonly bool _haveModel;

        /// <param name="modelEstimate">Up-front guess for the whole job, from the toolpath
        /// model. Zero or negative if the model could not estimate (e.g. no feed moves),
        /// in which case only the measured pace is used once it exists.</param>
        /// <param name="totalLines">Number of lines in the job.</param>
        public EtaEstimator(TimeSpan modelEstimate, int totalLines)
        {
            _totalLines = Math.Max(1, totalLines);
            _haveModel = modelEstimate > TimeSpan.Zero;
            _modelTotalSeconds = _haveModel ? modelEstimate.TotalSeconds : 0.0;
        }

        /// <summary>
        /// Returns the time still to go. Null while there is nothing to base an estimate
        /// on - no model guess and no measured progress yet.
        /// </summary>
        public TimeSpan? Update(int linesCompleted, TimeSpan elapsed)
        {
            bool haveMeasurement = linesCompleted > 0 && elapsed > TimeSpan.Zero;

            if (haveMeasurement)
            {
                double measuredRate = elapsed.TotalSeconds / linesCompleted;   // seconds per line so far

                double rate;
                if (_haveModel)
                {
                    // Blend the per-line rate, not the projected total: weighting the
                    // total by fraction-complete would algebraically cancel the
                    // measurement and leave the estimate a pure scaling of the guess.
                    // Trust the measured rate in proportion to how much of the job it is
                    // based on - none at the start, all by the end.
                    double fraction = Math.Clamp((double)linesCompleted / _totalLines, 0.0, 1.0);
                    double modelRate = _modelTotalSeconds / _totalLines;
                    rate = (1 - fraction) * modelRate + fraction * measuredRate;
                }
                else
                {
                    rate = measuredRate;
                }

                double remainingSeconds = rate * (_totalLines - linesCompleted);
                return TimeSpan.FromSeconds(Math.Max(0, remainingSeconds));
            }

            if (_haveModel)
            {
                // No measurement yet: the whole guess remains.
                return TimeSpan.FromSeconds(Math.Max(0, _modelTotalSeconds - elapsed.TotalSeconds));
            }

            return null;
        }
    }
}
