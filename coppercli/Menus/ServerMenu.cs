using coppercli.Core.Communication;
using coppercli.Helpers;
using coppercli.WebServer;
using Spectre.Console;
using static coppercli.CliConstants;
using static coppercli.Helpers.DisplayHelpers;
using static coppercli.WebServer.WebConstants;

namespace coppercli.Menus
{
    /// <summary>
    /// Server mode menu - runs serial proxy + web server.
    /// </summary>
    internal static class ServerMenu
    {
        private enum PortOption { UseSaved, Port, Manual, Back }

        // Message buffer for activity log
        private const int MaxMessages = 5;

        public static void Show()
        {
            var settings = AppState.Settings;
            string selectedPort;
            int selectedBaud;

            // If connected, offer to disconnect and use current settings
            if (AppState.Machine.Connected)
            {
                var currentPort = settings.SerialPortName;
                var currentBaud = settings.SerialPortBaud;

                AnsiConsole.MarkupLine($"[{ColorWarning}]Currently connected to {currentPort} @ {currentBaud}[/]");
                AnsiConsole.MarkupLine($"[{ColorDim}]Server will disconnect and take over the serial port.[/]");
                AnsiConsole.WriteLine();

                if (!MenuHelpers.Confirm("Disconnect and start server with current settings?"))
                {
                    return;
                }

                // Disconnect (but preserve IsWorkZeroSet since server will reconnect to same machine)
                var preserveWorkZero = AppState.IsWorkZeroSet;
                AppState.Machine.Disconnect();
                // Restore IsWorkZeroSet - the machine's work offset is preserved across reconnect
                AppState.IsWorkZeroSet = preserveWorkZero;
                Logger.Log($"ServerMenu: Preserved IsWorkZeroSet={preserveWorkZero} across server transition");

                selectedPort = currentPort;
                selectedBaud = currentBaud;
            }
            else
            {
                // Not connected - let user select port and baud

                // Get available serial ports
                string[] ports = Array.Empty<string>();
                AnsiConsole.Status()
                    .Start("Enumerating serial ports...", ctx =>
                    {
                        ports = ConnectionMenu.GetAvailablePorts();
                    });

                if (ports.Length == 0)
                {
                    MenuHelpers.ShowError("No serial ports found!");
                    return;
                }

                // Select serial port
                var portMenu = new MenuDef<PortOption>();

                // If we have saved settings, offer to use them
                if (!string.IsNullOrEmpty(settings.SerialPortName))
                {
                    portMenu.Add(new MenuItem<PortOption>(
                        $"Use saved ({settings.SerialPortName} @ {settings.SerialPortBaud})", 'u', PortOption.UseSaved));
                }

                for (int i = 0; i < ports.Length; i++)
                {
                    portMenu.Add(new MenuItem<PortOption>(ports[i], (char)('0' + ((i + 1) % 10)), PortOption.Port, i));
                }
                portMenu.Add(new MenuItem<PortOption>("Enter manually", 'm', PortOption.Manual));
                portMenu.Add(new MenuItem<PortOption>("Back", 'q', PortOption.Back));

                var portChoice = MenuHelpers.ShowMenu("Select serial port:", portMenu);

                if (portChoice.Option == PortOption.Back)
                {
                    return;
                }

                if (portChoice.Option == PortOption.UseSaved)
                {
                    selectedPort = settings.SerialPortName;
                    selectedBaud = settings.SerialPortBaud;
                }
                else
                {
                    if (portChoice.Option == PortOption.Manual)
                    {
                        selectedPort = MenuHelpers.Ask<string>("Enter port name:");
                    }
                    else
                    {
                        selectedPort = ports[portChoice.Data];
                    }

                    // Select baud rate
                    var baudOptions = CommonBaudRates.Select((b, i) => $"{i + 1}. {b}").ToArray();
                    int baudChoice = MenuHelpers.ShowMenu("Select baud rate:", baudOptions);
                    selectedBaud = CommonBaudRates[baudChoice];
                }
            }

            // Select ports
            int proxyPort = MenuHelpers.Ask("Proxy port (for TUI clients):", ProxyDefaultPort);
            int webPort = MenuHelpers.Ask("Web port (for browser):", WebDefaultPort);

            // Run unified server mode
            RunServer(selectedPort, selectedBaud, proxyPort, webPort, exitToMenu: true);
        }

        /// <summary>
        /// Runs server mode: SerialProxy on proxyPort, CncWebServer on webPort.
        /// This is the canonical server implementation used by both --server flag and menu.
        /// </summary>
        /// <param name="serialPort">Serial port name</param>
        /// <param name="baudRate">Baud rate</param>
        /// <param name="proxyPort">TCP port for proxy (TUI clients)</param>
        /// <param name="webPort">TCP port for web server (browser)</param>
        /// <param name="exitToMenu">If true, returns to menu on exit. If false, exits process.</param>
        public static void RunServer(string serialPort, int baudRate, int proxyPort, int webPort, bool exitToMenu)
        {
            Logger.Log("ServerMenu.RunServer: starting proxy={0}, web={1}", proxyPort, webPort);
            var messages = new List<string>();

            // Start the serial proxy
            Logger.Log("ServerMenu.RunServer: creating SerialProxy");
            var proxy = new SerialProxy();
            SubscribeProxyEvents(proxy, messages);

            // Callback to check if serial port is in use by web client
            // This prevents the proxy from attempting to open the serial port when Machine is connected
            proxy.IsSerialPortInUse = () => AppState.Machine.Connected || CncWebServer.HasWebClient;

            // Callback to force-disconnect TUI client from proxy (for web UI "Force Disconnect")
            CncWebServer.ForceDisconnectProxyClient = () => proxy.ForceDisconnectClient();

            // Callback to check if proxy has a TUI client (for web UI to detect and offer force disconnect)
            CncWebServer.HasProxyClient = () => proxy.HasClient;

            try
            {
                Logger.Log("ServerMenu.RunServer: starting proxy on port {0}", proxyPort);
                proxy.Start(serialPort, baudRate, proxyPort);
                Logger.Log("ServerMenu.RunServer: proxy started");
            }
            catch (Exception ex)
            {
                if (exitToMenu)
                {
                    MenuHelpers.ShowError($"Failed to start proxy: {ex.Message}");
                    return;
                }
                else
                {
                    AnsiConsole.MarkupLine($"[{ColorError}]Failed to start proxy: {ex.Message}[/]");
                    Environment.Exit(1);
                }
            }

            // Start web server in background thread
            Logger.Log("ServerMenu.RunServer: starting web server thread");
            Exception? webServerError = null;
            var webServerStarted = new ManualResetEvent(false);

            var webServerThread = new Thread(() =>
            {
                try
                {
                    CncWebServer.Run(webPort, serialPort, baudRate, webServerStarted);
                }
                catch (Exception ex)
                {
                    webServerError = ex;
                    webServerStarted.Set();
                }
            })
            {
                Name = "WebServer",
                IsBackground = true
            };
            webServerThread.Start();

            // Wait for web server to start (or fail)
            Logger.Log("ServerMenu.RunServer: waiting for web server to signal ready");
            webServerStarted.WaitOne(WebServerStartTimeoutMs);
            Logger.Log("ServerMenu.RunServer: web server signaled (error={0})", webServerError != null);
            if (webServerError != null)
            {
                proxy.Stop();
                if (exitToMenu)
                {
                    MenuHelpers.ShowError($"Web server failed: {webServerError.Message}");
                    return;
                }
                else
                {
                    AnsiConsole.MarkupLine($"[{ColorError}]Web server failed: {webServerError.Message}[/]");
                    Environment.Exit(1);
                }
            }

            // Handle Ctrl+C to exit cleanly
            var exitRequested = false;
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                exitRequested = true;
            };

            // Run server status display (blocks until q/Escape or Ctrl+C)
            MonitorServer(proxy, proxyPort, webPort, messages, () => exitRequested);

            // Cleanup - stop both servers
            CncWebServer.Stop();
            webServerThread.Join(2000);  // Wait up to 2s for web server to stop
            proxy.Stop();

            if (!exitToMenu)
            {
                Environment.Exit(0);
            }

            // Reconnect Machine when returning to menu
            if (!AppState.Machine.Connected)
            {
                try
                {
                    AppState.Machine.Connect();
                }
                catch
                {
                    // Ignore connection errors - user can reconnect manually
                }
            }
        }

        private static void SubscribeProxyEvents(SerialProxy proxy, List<string> messages)
        {
            proxy.Info += msg =>
            {
                Logger.Log($"Proxy: {msg}");
                lock (messages)
                {
                    messages.Add($"{AnsiDim}{DateTime.Now:HH:mm:ss}{AnsiReset} {msg}");
                    while (messages.Count > MaxMessages)
                    {
                        messages.RemoveAt(0);
                    }
                }
            };

            proxy.Error += msg =>
            {
                Logger.Log($"Proxy error: {msg}");
                lock (messages)
                {
                    messages.Add($"{AnsiDim}{DateTime.Now:HH:mm:ss}{AnsiReset} {AnsiError}{msg}{AnsiReset}");
                    while (messages.Count > MaxMessages)
                    {
                        messages.RemoveAt(0);
                    }
                }
            };
        }

        private static void MonitorServer(SerialProxy proxy, int proxyPort, int webPort, List<string> messages, Func<bool> shouldExit)
        {
            Console.Clear();
            Console.CursorVisible = false;

            try
            {
                while (!shouldExit())
                {
                    DrawServerStatus(proxy, proxyPort, webPort, messages);

                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        if (InputHelpers.IsExitKey(key))
                        {
                            break;
                        }
                    }

                    Thread.Sleep(ProxyStatusUpdateIntervalMs);
                }
            }
            finally
            {
                Console.CursorVisible = true;
            }
        }

        private static void DrawServerStatus(SerialProxy proxy, int proxyPort, int webPort, List<string> messages)
        {
            var (winWidth, _) = GetSafeWindowSize();

            Console.SetCursorPosition(0, 0);

            // Header
            string header = $"{AnsiPrompt}Server{AnsiReset}";
            int headerPad = Math.Max(0, (winWidth - CalculateDisplayLength(header)) / 2);
            WriteLineTruncated(new string(' ', headerPad) + header, winWidth);
            WriteLineTruncated("", winWidth);

            // Connection info
            WriteLineTruncated($"  Serial Port:    {AnsiInfo}{proxy.SerialPortName}{AnsiReset}", winWidth);
            WriteLineTruncated($"  Baud Rate:      {AnsiInfo}{proxy.BaudRate}{AnsiReset}", winWidth);
            WriteLineTruncated("", winWidth);

            // Show both ports
            var localIps = NetworkHelpers.GetLocalIPAddresses();
            if (localIps.Count > 0)
            {
                var ip = localIps[0];
                WriteLineTruncated($"  TUI  (:{proxyPort}):  {AnsiSuccessBold}{ip}:{proxyPort}{AnsiReset}", winWidth);
                WriteLineTruncated($"  Web  (:{webPort}):  {AnsiSuccessBold}http://{ip}:{webPort}{AnsiReset}", winWidth);
            }
            else
            {
                WriteLineTruncated($"  TUI Port:       {AnsiInfo}{proxyPort}{AnsiReset}", winWidth);
                WriteLineTruncated($"  Web Port:       {AnsiInfo}{webPort}{AnsiReset}", winWidth);
            }
            WriteLineTruncated("", winWidth);

            // Client status - only one client type can be connected at a time
            if (proxy.HasClient)
            {
                var clientAddr = proxy.ClientAddress ?? "";
                WriteLineTruncated($"  Client:         {AnsiSuccess}TUI @ {clientAddr}{AnsiReset}", winWidth);
                if (proxy.ClientConnectedTime.HasValue)
                {
                    var duration = DateTime.Now - proxy.ClientConnectedTime.Value;
                    WriteLineTruncated($"  Connected:      {AnsiSuccess}{FormatDuration(duration)}{AnsiReset}", winWidth);
                }
            }
            else if (CncWebServer.HasWebClient)
            {
                var webAddr = CncWebServer.WebClientAddress ?? "";
                WriteLineTruncated($"  Client:         {AnsiSuccess}Web @ {webAddr}{AnsiReset}", winWidth);
                WriteLineTruncated("", winWidth);
            }
            else
            {
                WriteLineTruncated($"  Client:         {AnsiDim}{StatusNone}{AnsiReset}", winWidth);
                WriteLineTruncated("", winWidth);
            }

            // Traffic statistics
            WriteLineTruncated($"  Bytes to client:   {AnsiDim}{proxy.BytesToClient,10:N0}{AnsiReset}", winWidth);
            WriteLineTruncated($"  Bytes from client: {AnsiDim}{proxy.BytesFromClient,10:N0}{AnsiReset}", winWidth);
            WriteLineTruncated("", winWidth);

            // Recent messages
            WriteLineTruncated($"  {AnsiInfo}Recent activity:{AnsiReset}", winWidth);
            lock (messages)
            {
                for (int i = 0; i < MaxMessages; i++)
                {
                    if (i < messages.Count)
                    {
                        WriteLineTruncated($"    {messages[i]}", winWidth);
                    }
                    else
                    {
                        WriteLineTruncated("", winWidth);
                    }
                }
            }
            WriteLineTruncated("", winWidth);

            // Instructions
            WriteLineTruncated($"  {AnsiDim}Press {AnsiReset}{AnsiInfo}q{AnsiReset}{AnsiDim} or {AnsiReset}{AnsiInfo}Escape{AnsiReset}{AnsiDim} to stop server{AnsiReset}", winWidth);
        }
    }
}
