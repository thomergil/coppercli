// Extracted from Program.cs

using coppercli.Core.Controllers;
using coppercli.Core.Settings;
using coppercli.Helpers;
using coppercli.Macro;
using Spectre.Console;
using static coppercli.CliConstants;

namespace coppercli.Menus
{
    /// <summary>
    /// Main menu for the coppercli application.
    /// </summary>
    internal static class MainMenu
    {
        private enum MainAction
        {
            Connect,
            LoadFile,
            Move,
            Probe,
            Mill,
            Macro,
            Server,
            Settings,
            About,
            Exit
        }

        private static string? GetServerDisabledReason() =>
            AppState.Machine.Connected && AppState.Settings.ConnectionType == ConnectionType.Ethernet
                ? DisabledDisconnect
                : null;

        private static readonly MenuDef<MainAction> MainMenuDef = new(
            new MenuItem<MainAction>("Connection", 'c', MainAction.Connect),
            new MenuItem<MainAction>("Load G-Code", 'l', MainAction.LoadFile),
            new MenuItem<MainAction>("Jog", 'j', MainAction.Move,
                Blocker: MenuHelpers.GetMachineDisabledReason),
            // Enabled exactly when nothing blocks it, so the entry and the reason beside
            // it cannot disagree.
            new MenuItem<MainAction>("Probe", 'p', MainAction.Probe,
                Blocker: MenuHelpers.GetProbeDisabledReason),
            new MenuItem<MainAction>("Mill", 'm', MainAction.Mill,
                Blocker: () => MenuHelpers.GetMillDisabledReason()),
            new MenuItem<MainAction>("Macro", 'r', MainAction.Macro,
                Blocker: MenuHelpers.GetMachineDisabledReason),
            new MenuItem<MainAction>("Server", 'x', MainAction.Server,
                Blocker: GetServerDisabledReason),
            new MenuItem<MainAction>("Settings", 's', MainAction.Settings),
            new MenuItem<MainAction>("About", 'a', MainAction.About),
            new MenuItem<MainAction>("Exit", 'q', MainAction.Exit)
        );

        public static void Show()
        {
            var machine = AppState.Machine;
            var currentFile = AppState.CurrentFile;
            var probePoints = AppState.ProbePoints;

            Console.Clear();

            // Show status header
            var activity = MachineWait.GetActivity(machine);
            var statusColor = MachineWait.IsUnavailable(activity) ? ColorError : ColorSuccess;
            var statusText = DisplayHelpers.GetActivityText(activity, machine.Status);
            var settings = AppState.Settings;
            var connectionInfo = machine.Connected
                ? (settings.ConnectionType == ConnectionType.Serial
                    ? $" ({settings.SerialPortName})"
                    : $" ({settings.EthernetIP}:{settings.EthernetPort})")
                : "";

            AnsiConsole.Write(new Rule($"[{ColorBold} {ColorPrompt}]{AppTitle} {AppVersion}[/]").RuleStyle(ColorPrompt));
            AnsiConsole.MarkupLine($"Status: [{statusColor}]{statusText}[/][{ColorDim}]{connectionInfo}[/] | " +
                $"X:[{ColorWarning}]{machine.WorkPosition.X:F3}[/] " +
                $"Y:[{ColorWarning}]{machine.WorkPosition.Y:F3}[/] " +
                $"Z:[{ColorWarning}]{machine.WorkPosition.Z:F3}[/]");

            if (currentFile != null)
            {
                var probeStatus = "";
                if (probePoints != null)
                {
                    probeStatus = AppState.AreProbePointsApplied
                        ? $", [{ColorSuccess}]probe grid applied[/]"
                        : $", [{ColorWarning}]probe grid pending[/]";
                }
                AnsiConsole.MarkupLine($"File: [{ColorInfo}]{currentFile.FileName}[/] ({currentFile.Size.X:F1} x {currentFile.Size.Y:F1} mm{probeStatus})");
            }

            if (probePoints != null)
            {
                AnsiConsole.MarkupLine($"Probe: [{ColorInfo}]{probePoints.SizeX}x{probePoints.SizeY}[/] ({probePoints.Progress}/{probePoints.TotalPoints} points)");
            }

            // Show machine profile
            var profile = MachineProfiles.GetProfile(settings.MachineProfile);
            if (profile != null)
            {
                AnsiConsole.MarkupLine($"Machine: [{ColorInfo}]{profile.Name ?? settings.MachineProfile}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"Machine: [{ColorError}]{NoMachineProfileWarning}[/]");
            }

            AnsiConsole.WriteLine();

            // Enable auto-clear while showing menu (user can see status updates)
            machine.EnableAutoStateClear = true;

            // Smart default based on workflow state
            int smartDefault = MainMenuDef.IndexOf(GetSmartDefault());
            var choice = MenuHelpers.ShowMenuWithRefresh("Select an option:", MainMenuDef, smartDefault);

            // Disable auto-clear before leaving menu
            machine.EnableAutoStateClear = false;

            // Null means status changed - return to redraw (loop in Program.cs will call us again)
            if (choice == null)
            {
                return;
            }

            ExecuteAction(choice.Option);
        }

        private static void ExecuteAction(MainAction action)
        {
            switch (action)
            {
                case MainAction.Connect: ConnectionMenu.Show(); break;
                case MainAction.LoadFile: FileMenu.LoadGCodeFile(); break;
                case MainAction.Move: JogMenu.Show(); break;
                case MainAction.Probe: ProbeMenu.Show(); break;
                case MainAction.Mill: MillMenu.Show(); break;
                case MainAction.Macro: MacroMenu.Show(); break;
                case MainAction.Server: ServerMenu.Show(); break;
                case MainAction.Settings: SettingsMenu.Show(Persistence.SaveSettings); break;
                case MainAction.About: AboutMenu.Show(); break;
                case MainAction.Exit: ExitProgram(); break;
            }
        }

        private static MainAction GetSmartDefault()
        {
            var machine = AppState.Machine;
            var currentFile = AppState.CurrentFile;
            var probePoints = AppState.ProbePoints;

            if (!machine.Connected)
            {
                return MainAction.Connect;
            }

            if (currentFile == null)
            {
                return MainAction.LoadFile;
            }

            if (!AppState.IsWorkZeroSet)
            {
                return MainAction.Move;
            }

            if (probePoints == null || !probePoints.HasCompleteData || !AppState.AreProbePointsApplied)
            {
                return MainAction.Probe;
            }

            return MainAction.Mill;
        }

        private static void ExitProgram()
        {
            if (AppState.Machine.Connected)
            {
                AppState.Machine.Disconnect();
            }
            Environment.Exit(0);
        }

    }
}
