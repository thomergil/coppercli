#nullable enable

using coppercli.Core.GCode.GCodeCommands;
using System;
using System.Collections.Generic;

namespace coppercli.Core.GCode
{
    /// <summary>
    /// A phase of a job: the work one tool does. The first runs from the start of the file, and
    /// each later one from the last tool change (M6) between two cuts, so a tool change before
    /// the file's first cut or after its last does not make a phase with no work.
    /// </summary>
    /// <param name="Number">From 1, in file order.</param>
    /// <param name="Time">How long its feed moves take at the feeds the file gives.</param>
    public sealed record JobPhase(int Number, TimeSpan Time)
    {
        /// <summary>The phases of <paramref name="toolpath"/>, and the number of the phase that holds each command.</summary>
        internal static (IReadOnlyList<JobPhase> Phases, int[] PhaseOf) Of(IReadOnlyList<Command> toolpath)
        {
            var starts = StartsIn(toolpath);
            var times = new List<TimeSpan> { TimeSpan.Zero };
            var phaseOf = new int[toolpath.Count];

            for (int i = 0; i < toolpath.Count; i++)
            {
                if (times.Count <= starts.Count && starts[times.Count - 1] == i)
                {
                    times.Add(TimeSpan.Zero);
                }
                phaseOf[i] = times.Count;

                if (toolpath[i] is Motion { FullyKnown: true } move)
                {
                    times[^1] += move.FeedTime;
                }
            }

            var phases = new List<JobPhase>();
            for (int i = 0; i < times.Count; i++)
            {
                phases.Add(new JobPhase(i + 1, times[i]));
            }
            return (phases, phaseOf);
        }

        /// <summary>
        /// Where in <paramref name="toolpath"/> each phase after the first starts: at the last
        /// tool change between one cut and the next.
        /// </summary>
        private static List<int> StartsIn(IReadOnlyList<Command> toolpath)
        {
            var starts = new List<int>();
            bool hasCut = false;
            int? toolChange = null;

            for (int i = 0; i < toolpath.Count; i++)
            {
                if (toolpath[i] is MCode { IsToolChange: true } && hasCut)
                {
                    toolChange = i;
                }
                else if (toolpath[i] is Motion { FullyKnown: true, IsCut: true } or Line { FullyKnown: false, ZKnown: true, End.Z: < 0 })
                {
                    if (toolChange is int start)
                    {
                        starts.Add(start);
                        toolChange = null;
                    }
                    hasCut = true;
                }
            }

            return starts;
        }
    }
}
