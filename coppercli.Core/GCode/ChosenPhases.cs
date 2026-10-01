#nullable enable

using coppercli.Core.Util;
using System.Collections.Generic;
using System.Linq;

namespace coppercli.Core.GCode
{
    /// <summary>The phases a run mills when it skips some (see <see cref="JobPhase"/> and PartClip).</summary>
    public sealed class ChosenPhases
    {
        private ChosenPhases(IReadOnlySet<int> numbers)
        {
            Numbers = numbers;
        }

        /// <summary>Phase numbers, from 1.</summary>
        public IReadOnlySet<int> Numbers { get; }

        /// <summary>
        /// The operator's choice of phases of <paramref name="file"/>.
        /// </summary>
        /// <returns>
        /// The phases; null with no refusal when every phase is chosen, which mills the whole
        /// job; or null with the reason the choice was refused.
        /// </returns>
        public static (ChosenPhases? Phases, string? Refused) Choose(GCodeFile file, IEnumerable<int> numbers)
        {
            var set = numbers.ToHashSet();
            if (set.Count == 0)
            {
                return (null, Constants.ErrorNoPhaseChosen);
            }

            if (!set.All(number => number >= 1 && number <= file.Phases.Count))
            {
                return (null, Constants.ErrorPhaseNotInFile);
            }

            return set.Count == file.Phases.Count ? (null, null) : (new ChosenPhases(set), null);
        }

        /// <summary>Whether a run mills phase <paramref name="number"/>; null <paramref name="phases"/> mills every phase.</summary>
        public static bool Runs(ChosenPhases? phases, int number) => phases == null || phases.Numbers.Contains(number);

        /// <summary>The phases chosen, for the log.</summary>
        public override string ToString() => string.Join(", ", Numbers.Order());
    }
}
