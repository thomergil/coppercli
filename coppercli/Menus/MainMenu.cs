// Extracted from Program.cs

using coppercli.Helpers;
using coppercli.Macro;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.GrblProtocol;

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
            Proxy,
            Settings,
            About,
            Exit
        }

        private static readonly MenuDef<MainAction> MainMenuDef = new(
            new MenuItem<MainAction>("Connection", 'c', MainAction.Connect),
            new MenuItem<MainAction>("Load G-Code", 'l', MainAction.LoadFile),
            new MenuItem<MainAction>("Jog", 'j', MainAction.Move,
                EnabledWhen: () => AppState.Machine.Connected),
            new MenuItem<MainAction>("Probe", 'p', MainAction.Probe,
                EnabledWhen: () => AppState.Machine.Connected && AppState.CurrentFile != null),
            new MenuItem<MainAction>("Mill", 'm', MainAction.Mill,
                EnabledWhen: () => AppState.Machine.Connected && AppState.Machine.File.Count > 0 &&
                    (AppState.ProbePoints == null || AppState.AreProbePointsApplied)),
            new MenuItem<MainAction>("Macro", 'r', MainAction.Macro,
                EnabledWhen: () => AppState.Machine.Connected),
            new MenuItem<MainAction>("Proxy [experimental]", 'x', MainAction.Proxy),
            new MenuItem<MainAction>("Settings", 't', MainAction.Settings),
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
            var statusColor = machine.Connected ? "green" : "red";
            var statusText = machine.Connected ? machine.Status : StatusDisconnected;

            AnsiConsole.Write(new Rule($"[bold blue]{AppTitle} {AppVersion}[/]").RuleStyle("blue"));
            AnsiConsole.MarkupLine($"Status: [{statusColor}]{statusText}[/] | " +
                $"X:[yellow]{machine.WorkPosition.X:F3}[/] " +
                $"Y:[yellow]{machine.WorkPosition.Y:F3}[/] " +
                $"Z:[yellow]{machine.WorkPosition.Z:F3}[/]");

            if (currentFile != null)
            {
                AnsiConsole.MarkupLine($"File: [cyan]{currentFile.FileName}[/] ({currentFile.Size.X:F1} x {currentFile.Size.Y:F1} mm)");
            }

            if (probePoints != null)
            {
                var appliedStatus = AppState.AreProbePointsApplied ? "[green]applied[/]" : "[yellow]not applied[/]";
                AnsiConsole.MarkupLine($"Probe: [cyan]{probePoints.SizeX}x{probePoints.SizeY}[/] ({probePoints.Progress}/{probePoints.TotalPoints} points, {appliedStatus})");
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
                case MainAction.Proxy: ProxyMenu.Show(); break;
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

            if (probePoints == null || probePoints.NotProbed.Count > 0 || !AppState.AreProbePointsApplied)
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
