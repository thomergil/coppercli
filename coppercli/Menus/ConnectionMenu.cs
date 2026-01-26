// Extracted from Program.cs

using coppercli.Core.Communication;
using coppercli.Core.GCode;
using coppercli.Core.Settings;
using coppercli.Helpers;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Core.Util.GrblProtocol;

namespace coppercli.Menus
{
    /// <summary>
    /// Connection menu for connecting/disconnecting from the CNC machine.
    /// </summary>
    internal static class ConnectionMenu
    {
        public enum ConnectionResult
        {
            Success,
            Timeout,
            PortNotOpened,
            Error
        }

        // Menu option types
        private enum ConnType { Serial, Ethernet, Back }
        private enum PortOption { Reconnect, AutoDetect, Port, Manual }

        // Connection type menu definition
        private static readonly MenuDef<ConnType> ConnTypeMenu = new(
            new MenuItem<ConnType>("Serial", 's', ConnType.Serial),
            new MenuItem<ConnType>("Ethernet [[untested]]", 'e', ConnType.Ethernet),
            new MenuItem<ConnType>("Back", 'q', ConnType.Back)
        );

        public static void Show()
        {
            var machine = AppState.Machine;
            var settings = AppState.Settings;

            if (machine.Connected)
            {
                if (MenuHelpers.Confirm("Disconnect from machine?"))
                {
                    machine.Disconnect();
                    AppState.IsWorkZeroSet = false;
                    AnsiConsole.MarkupLine("[yellow]Disconnected[/]");
                }
            }
            else
            {
                var connChoice = MenuHelpers.ShowMenu("Connection type:", ConnTypeMenu);

                if (connChoice.Option == ConnType.Back)
                {
                    return;
                }

                if (connChoice.Option == ConnType.Serial)
                {
                    settings.ConnectionType = ConnectionType.Serial;

                    // List available ports
                    string[] ports = Array.Empty<string>();
                    AnsiConsole.Status()
                        .Start("Enumerating serial ports...", ctx =>
                        {
                            ports = GetAvailablePorts();
                        });

                    if (ports.Length == 0)
                    {
                        AnsiConsole.MarkupLine("[red]No serial ports found![/]");
                        return;
                    }

                    // Build dynamic port menu
                    var portMenu = new MenuDef<PortOption>();

                    // Add Reconnect option if we have saved settings
                    if (!string.IsNullOrEmpty(settings.SerialPortName))
                    {
                        portMenu.Add(new MenuItem<PortOption>(
                            $"Reconnect ({settings.SerialPortName} @ {settings.SerialPortBaud})", 'r', PortOption.Reconnect));
                    }

                    portMenu.Add(new MenuItem<PortOption>("Auto-detect (scan all ports)", 'a', PortOption.AutoDetect));
                    for (int i = 0; i < ports.Length; i++)
                    {
                        portMenu.Add(new MenuItem<PortOption>(ports[i], (char)('0' + ((i + 2) % 10)), PortOption.Port, i));
                    }
                    portMenu.Add(new MenuItem<PortOption>("Enter manually", 'm', PortOption.Manual));

                    var selected = MenuHelpers.ShowMenu("Select serial port:", portMenu);

                    if (selected.Option == PortOption.Reconnect)
                    {
                        ConnectWithCurrentSettings();
                        return;
                    }

                    if (selected.Option == PortOption.AutoDetect)
                    {
                        if (AutoDetectSerial(ports))
                        {
                            Persistence.SaveSettings();
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[red]No GRBL device found on any port.[/]");
                        }
                        return;
                    }

                    string selectedPort;
                    if (selected.Option == PortOption.Manual)
                    {
                        selectedPort = AnsiConsole.Ask<string>("Enter port name:", settings.SerialPortName);
                    }
                    else
                    {
                        selectedPort = ports[selected.Data];
                    }

                    settings.SerialPortName = selectedPort;

                    var baudOptions = CommonBaudRates.Select((b, i) => $"{i + 1}. {b}").ToArray();
                    int baudChoice = MenuHelpers.ShowMenu("Select baud rate:", baudOptions);
                    settings.SerialPortBaud = CommonBaudRates[baudChoice];
                }
                else if (connChoice.Option == ConnType.Ethernet)
                {
                    settings.ConnectionType = ConnectionType.Ethernet;
                    settings.EthernetIP = AnsiConsole.Ask("Enter IP address:", settings.EthernetIP);
                    settings.EthernetPort = AnsiConsole.Ask("Enter port:", settings.EthernetPort);
                }

                ConnectWithCurrentSettings();
            }
        }

        /// <summary>
        /// Quick connect using saved settings (called on startup).
        /// </summary>
        public static void QuickConnect()
        {
            var settings = AppState.Settings;
            AnsiConsole.MarkupLine($"[blue]Connecting to {settings.SerialPortName} @ {settings.SerialPortBaud}...[/]");
            ConnectWithCurrentSettings();
        }

        /// <summary>
        /// Attempts to connect using the current settings and shows post-connection offers.
        /// </summary>
        private static void ConnectWithCurrentSettings()
        {
            var machine = AppState.Machine;

            try
            {
                ConnectionResult result = ConnectionResult.Error;
                string? message = null;

                AnsiConsole.Status()
                    .Start("Connecting...", ctx =>
                    {
                        (result, message) = TryConnect(ConnectionTimeoutMs + GrblResponseTimeoutMs);
                    });

                switch (result)
                {
                    case ConnectionResult.Success when message != null:
                        if (message != StatusIdle)
                        {
                            AnsiConsole.MarkupLine($"[green]Connected! GRBL status: {message}[/]");
                        }
                        Persistence.SaveSettings();
                        OfferToHome(message);
                        break;
                    case ConnectionResult.Success:
                        AnsiConsole.MarkupLine("[yellow]Warning: Port opened but no GRBL response received.[/]");
                        AnsiConsole.MarkupLine("[yellow]Check that the correct port is selected and GRBL is running.[/]");
                        if (MenuHelpers.Confirm("Disconnect?"))
                        {
                            machine.Disconnect();
                        }
                        break;
                    case ConnectionResult.Timeout:
                        AnsiConsole.MarkupLine("[red]Connection timed out.[/]");
                        if (machine.Connected)
                        {
                            machine.Disconnect();
                        }
                        break;
                    case ConnectionResult.PortNotOpened:
                        AnsiConsole.MarkupLine("[red]Could not open serial port. Is the machine powered on?[/]");
                        break;
                    case ConnectionResult.Error:
                        AnsiConsole.MarkupLine($"[red]Connection failed: {Markup.Escape(message ?? "Unknown error")}[/]");
                        break;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Connection failed: {Markup.Escape(ex.Message)}[/]");
            }
        }

        private static bool AutoDetectSerial(string[] ports)
        {
            var machine = AppState.Machine;
            var settings = AppState.Settings;

            AnsiConsole.MarkupLine($"[blue]Scanning {ports.Length} port(s) at {CommonBaudRates.Length} baud rates...[/]");

            foreach (var baud in CommonBaudRates)
            {
                foreach (var port in ports)
                {
                    AnsiConsole.Markup($"  Trying [cyan]{port}[/] @ [cyan]{baud}[/]... ");

                    settings.SerialPortName = port;
                    settings.SerialPortBaud = baud;

                    try
                    {
                        var (result, message) = TryConnect(AutoDetectTimeoutMs);

                        if (result == ConnectionResult.Success && message != null)
                        {
                            AnsiConsole.MarkupLine($"[green]Found! Status: {message}[/]");
                            return true;
                        }

                        if (machine.Connected)
                        {
                            machine.Disconnect();
                        }

                        var status = result switch
                        {
                            ConnectionResult.Timeout => "timeout",
                            ConnectionResult.PortNotOpened => "not available",
                            ConnectionResult.Error => "failed",
                            _ => "no response"
                        };
                        AnsiConsole.MarkupLine($"[dim]{status}[/]");
                    }
                    catch (Exception)
                    {
                        if (machine.Connected)
                        {
                            machine.Disconnect();
                        }
                        AnsiConsole.MarkupLine("[dim]failed[/]");
                    }
                }
            }

            return false;
        }

        public static string[] GetAvailablePorts()
        {
            var ports = new List<string>();

            if (OperatingSystem.IsWindows())
            {
                ports.AddRange(System.IO.Ports.SerialPort.GetPortNames());
            }
            else
            {
                if (Directory.Exists("/dev"))
                {
                    foreach (var pattern in UnixSerialPortPatterns)
                    {
                        try
                        {
                            ports.AddRange(Directory.GetFiles("/dev", pattern));
                        }
                        catch
                        {
                            // Ignore directory access errors - continue with other patterns
                        }
                    }
                }
            }

            return ports.ToArray();
        }

        public static (ConnectionResult Result, string? Message) TryConnect(int timeoutMs)
        {
            var machine = AppState.Machine;
            string? grblStatus = null;
            bool timedOut = false;
            Exception? error = null;

            var connectTask = Task.Run(() =>
            {
                machine.Connect();
                if (!machine.Connected)
                {
                    return null;
                }
                machine.SoftReset();
                return StatusHelpers.WaitForGrblResponse(machine, GrblResponseTimeoutMs);
            });

            if (connectTask.Wait(timeoutMs))
            {
                try
                {
                    grblStatus = connectTask.Result;
                }
                catch (AggregateException ae)
                {
                    error = ae.InnerException;
                }
            }
            else
            {
                timedOut = true;
                connectTask.ContinueWith(t =>
                {
                    var _ = t.Exception;
                }, TaskContinuationOptions.OnlyOnFaulted);
            }

            if (error != null)
            {
                return (ConnectionResult.Error, error.Message);
            }
            if (timedOut)
            {
                return (ConnectionResult.Timeout, null);
            }
            if (grblStatus != null)
            {
                return (ConnectionResult.Success, grblStatus);
            }
            if (machine.Connected)
            {
                return (ConnectionResult.Success, null);
            }
            return (ConnectionResult.PortNotOpened, null);
        }

        private static void OfferToHome(string status)
        {
            var machine = AppState.Machine;

            // If in alarm state, try to unlock first
            if (status.StartsWith(StatusAlarm))
            {
                AnsiConsole.MarkupLine("[yellow]Alarm state detected, sending unlock ($X)...[/]");
                machine.SendLine(CmdUnlock);
                Thread.Sleep(CommandDelayMs);
                status = machine.Status;
                if (!status.StartsWith(StatusAlarm))
                {
                    AnsiConsole.MarkupLine($"[green]Unlocked! Status: {status}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]Still in alarm state. May need to home ($H).[/]");
                }
            }

            // Offer to home if not in alarm
            if (!status.StartsWith(StatusAlarm))
            {
                var result = MenuHelpers.ConfirmOrQuit("Home machine?", false);
                if (result == null)
                {
                    Environment.Exit(0);
                }
                if (result != true)
                {
                    return;
                }
                machine.SoftReset();
                Thread.Sleep(CommandDelayMs);
                machine.SendLine(CmdHome);

                AnsiConsole.Status()
                    .Start("Homing...", ctx =>
                    {
                        StatusHelpers.WaitForIdle(machine, HomingTimeoutMs);
                    });

                if (machine.Status == StatusIdle)
                {
                    AnsiConsole.MarkupLine("[green]Homing complete[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]Homing finished with status: {machine.Status}[/]");
                }
            }
        }
    }
}
