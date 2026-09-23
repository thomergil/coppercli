#nullable enable
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Net.Http;
using System.Threading;
using coppercli;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.Settings;
using coppercli.Core.Util;
using coppercli.Tests.Fakes;
using coppercli.WebServer;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The whole web stack over a simulated machine: a real Machine on a loopback GRBL, the
    /// real controllers, and the real HTTP API. Tests drive it as the browser does.
    /// </summary>
    public sealed class WebServerFixture : IDisposable
    {
        /// <summary>
        /// The repository root, for a test that reads a source file rather than a built one.
        /// Taken from this file's compile-time path, because the build output is outside the
        /// tree.
        /// </summary>
        public static string RepositoryRoot => Path.GetDirectoryName(
            Path.GetDirectoryName(ThisFile())!)!;

        private static string ThisFile([CallerFilePath] string path = "") => path;

        private const int StartupTimeoutMs = 10000;
        private const int PollIntervalMs = 20;

        private readonly FakeGrbl _grbl;
        private readonly Thread _serverThread;
        private readonly string _appDataDir;
        private readonly string? _previousAppData;
        private readonly Machine _machine;
        private readonly SessionState _session;
        private readonly bool _previousLogging;

        public WebServerFixture()
        {
            // Scratch directory for the app's own files, so a test run does not touch the
            // settings or probe autosave of whoever runs it.
            _appDataDir = Path.Combine(Path.GetTempPath(), "coppercli-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_appDataDir);
            _previousAppData = Environment.GetEnvironmentVariable(AppDataEnvVar);
            Environment.SetEnvironmentVariable(AppDataEnvVar, _appDataDir);

            // A 500 carries no detail to the browser, so the reason has to reach the log.
            _previousLogging = Helpers.Logger.Enabled;
            Helpers.Logger.Enabled = true;

            _grbl = new FakeGrbl();

            Settings = new MachineSettings
            {
                ConnectionType = ConnectionType.Ethernet,
                EthernetIP = "127.0.0.1",
                EthernetPort = _grbl.Port
            };

            AppState.Settings = Settings;
            _session = new SessionState();
            AppState.Session = _session;
            AppState.Machine = new Machine(Settings);
            AppState.ResetControllers();
            AppState.Machine.Connect();
            _machine = AppState.Machine;
            WaitUntil(() => AppState.Machine.Status == GrblProtocol.StatusIdle,
                "the simulated machine never reported Idle");

            Port = FreeTcpPort();
            var started = new ManualResetEvent(false);
            _serverThread = new Thread(() => CncWebServer.Run(Port, started))
            {
                IsBackground = true,
                Name = "CncWebServer(test)"
            };
            _serverThread.Start();

            if (!started.WaitOne(StartupTimeoutMs))
            {
                throw new InvalidOperationException("The web server did not start");
            }

            Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}/") };
        }

        /// <summary>The variable .NET reads the per-user application data directory from.</summary>
        private static string AppDataEnvVar =>
            OperatingSystem.IsWindows() ? "APPDATA" : "XDG_CONFIG_HOME";

        /// <summary>
        /// Points AppState back at this fixture's machine. Another test in this collection
        /// may have replaced it, and AppState is process-wide.
        /// </summary>
        public void RestoreFixtureState()
        {
            if (!ReferenceEquals(AppState.Machine, _machine))
            {
                AppState.Settings = Settings;
                AppState.Machine = _machine;
                AppState.ResetControllers();
            }

            // The session is process-wide as well, so without this the next test starts with
            // whatever board the last one loaded.
            if (!ReferenceEquals(AppState.Session, _session))
            {
                AppState.Session = _session;
            }

            // The machine is shared across the collection, and a stop leaves it alarmed the
            // way GRBL does. Unlock it here, so the order of the tests cannot change the
            // result.
            if (!MachineWait.IsIdle(_machine))
            {
                _machine.SendLine(GrblProtocol.CmdUnlock);
                WaitUntil(() => MachineWait.IsIdle(_machine), "the machine to settle");
            }
        }

        public HttpClient Client { get; }
        public MachineSettings Settings { get; }
        public FakeGrbl Grbl => _grbl;
        public int Port { get; }

        public static void WaitUntil(Func<bool> until, string what, int timeoutMs = StartupTimeoutMs)
        {
            // Stopwatch is monotonic: a step in the wall clock during this wait would end it
            // at once or never, and every web test waits through here.
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < timeoutMs)
            {
                if (until()) { return; }
                Thread.Sleep(PollIntervalMs);
            }

            Assert.Fail($"Timed out after {timeoutMs}ms waiting for: {what}");
        }

        /// <summary>A loopback port nothing is listening on.</summary>
        internal static int FreeTcpPort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            Client.Dispose();
            CncWebServer.Stop();
            _serverThread.Join(TimeSpan.FromSeconds(5));

            try { AppState.Machine?.Disconnect(); } catch { /* tearing down */ }
            _grbl.Dispose();

            Helpers.Logger.Enabled = _previousLogging;
            Environment.SetEnvironmentVariable(AppDataEnvVar, _previousAppData);
            try { Directory.Delete(_appDataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// xUnit ignores a [Collection] whose definition it cannot find, so this has to exist.
    /// </summary>
    [CollectionDefinition(WebServerCollection.Name)]
    public class WebServerCollection : ICollectionFixture<WebServerFixture>
    {
        public const string Name = "web-server";
    }
}
