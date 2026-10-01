using coppercli.Core.GCode;
using coppercli.Helpers;
using static coppercli.CliConstants;
using static coppercli.Helpers.DisplayHelpers;

namespace coppercli.Menus
{
    /// <summary>
    /// The list where the operator chooses which phases of the job a run mills; Done hands the
    /// choice to AppState.ChooseMillPhases.
    /// </summary>
    internal static class PhasePicker
    {
        private enum PhaseOption { Phase, Done, Cancel }

        private const char DoneMnemonic = 'd';
        private const char CancelMnemonic = 'q';

        public static void Show()
        {
            if (AppState.CurrentFile is not GCodeFile file)
            {
                return;
            }

            var chosen = file.Phases
                .Where(phase => ChosenPhases.Runs(AppState.MillPhases, phase.Number))
                .Select(phase => phase.Number)
                .ToHashSet();
            int selection = 0;

            while (true)
            {
                Console.Clear();
                var menu = BuildMenu(file.Phases, chosen);
                var picked = MenuHelpers.ShowMenu(PhasesMenuTitle, menu, selection);
                selection = picked.Option == PhaseOption.Phase ? picked.Data - 1 : menu.IndexOf(picked.Option);

                switch (picked.Option)
                {
                    case PhaseOption.Phase:
                        if (!chosen.Remove(picked.Data))
                        {
                            chosen.Add(picked.Data);
                        }
                        break;

                    case PhaseOption.Done:
                        string? notChosen = AppState.ChooseMillPhases(chosen);
                        if (notChosen == null)
                        {
                            return;
                        }
                        MenuHelpers.ShowError(notChosen);
                        break;

                    case PhaseOption.Cancel:
                        return;
                }
            }
        }

        private static MenuDef<PhaseOption> BuildMenu(IReadOnlyList<JobPhase> phases, IReadOnlySet<int> chosen)
        {
            var menu = new MenuDef<PhaseOption>();
            foreach (var phase in phases)
            {
                string marker = chosen.Contains(phase.Number) ? PhaseChosenMarker : PhaseNotChosenMarker;
                menu.Add(new MenuItem<PhaseOption>(
                    marker + GetPhaseLabel(phase), MenuHelpers.DigitMnemonic(phase.Number), PhaseOption.Phase, phase.Number));
            }
            menu.Add(new MenuItem<PhaseOption>(PhasesDone, DoneMnemonic, PhaseOption.Done));
            menu.Add(new MenuItem<PhaseOption>(MenuCancel, CancelMnemonic, PhaseOption.Cancel));
            return menu;
        }
    }
}
