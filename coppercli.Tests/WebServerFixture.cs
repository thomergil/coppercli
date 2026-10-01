#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using coppercli;
using coppercli.Core.Communication;
using coppercli.Core.Controllers;
using coppercli.Core.GCode;
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

        internal const string JsonFieldVersion = "version";

        /// <summary>
        /// A complete height map over the test boards, stamped with the loaded file and the
        /// machine's origin (or <paramref name="measuredFor"/>), rising by
        /// <paramref name="perColumn"/> from column to column.
        /// </summary>
        public static ProbeGrid CompleteMapForThisJob(
            double baseHeight = CompleteMapHeight, double perColumn = 0, string? measuredFor = null)
        {
            var map = new ProbeGrid(CompleteMapGridSize, new Vector2(0, 0), new Vector2(CompleteMapSpan, CompleteMapSpan))
            {
                Context = new ProbeContext(
                    measuredFor ?? AppState.Session.LastLoadedGCodeFile!, AppState.Machine.G54Offset)
            };
            for (int x = 0; x < map.SizeX; x++)
            {
                for (int y = 0; y < map.SizeY; y++)
                {
                    map.RecordMeasurement(x, y, baseHeight + perColumn * x);
                }
            }

            return map;
        }

        private const double CompleteMapHeight = -0.1;
        private const double CompleteMapGridSize = 10.0;
        private const double CompleteMapSpan = 20.0;
        private const string JsonFieldHomeFirst = "homeFirst";

        /// <summary>A GET as the browser makes it: the status and the JSON body.</summary>
        public async Task<(HttpStatusCode Code, JsonElement Body)> GetJsonAsync(string path)
        {
            var response = await Client.GetAsync(path);
            return (response.StatusCode, await JsonBodyAsync(response));
        }

        /// <summary>A POST as the browser makes it, with <paramref name="body"/> as JSON when given: the status and the JSON body.</summary>
        public async Task<(HttpStatusCode Code, JsonElement Body)> PostJsonAsync(string path, object? body = null)
        {
            var response = body == null
                ? await Client.PostAsync(path, null)
                : await Client.PostAsJsonAsync(path, body);
            return (response.StatusCode, await JsonBodyAsync(response));
        }

        /// <summary>Whether <paramref name="name"/> is true in a JSON reply.</summary>
        public static bool Flag(JsonElement json, string name) =>
            json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;

        /// <summary>The error a JSON reply names, or null.</summary>
        public static string? Error(JsonElement json) =>
            json.ValueKind == JsonValueKind.Object && json.TryGetProperty(WebConstants.JsonFieldError, out var error)
                ? error.GetString()
                : null;

        /// <returns>The body as JSON, or default for an empty body.</returns>
        private static async Task<JsonElement> JsonBodyAsync(HttpResponseMessage response)
        {
            string text = await response.Content.ReadAsStringAsync();
            return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        }

        /// <summary>The version /api/mill/can-start reports, which the browser reads when the pre-mill window opens.</summary>
        public async Task<long> CheckedVersionAsync() =>
            (await GetJsonAsync(WebConstants.ApiMillCanStart)).Body.GetProperty(JsonFieldVersion).GetInt64();

        /// <summary>
        /// A start request as the browser sends it: the version can-start reports now, and the
        /// home-first answer when one is given.
        /// </summary>
        public async Task<Dictionary<string, object>> MillStartBodyAsync(bool? homeFirst = null)
        {
            var body = new Dictionary<string, object> { [JsonFieldVersion] = await CheckedVersionAsync() };
            if (homeFirst is bool answer)
            {
                body[JsonFieldHomeFirst] = answer;
            }

            return body;
        }

        /// <summary>
        /// Loads the file at <paramref name="path"/> as the browser does, with work zero set,
        /// and applies a complete height map to it, so a mill run can start.
        /// </summary>
        public async Task LoadWithAMapAppliedAsync(string path)
        {
            AppState.MarkWorkZeroSet();
            var load = await Client.PostAsJsonAsync(WebConstants.ApiFileLoad, new { path });
            Assert.True(load.IsSuccessStatusCode, "loading the board failed");
            AppState.DiscardProbeData();
            CompleteMapForThisJob().Save(Persistence.GetProbeAutoSavePath());
            Assert.Equal(HttpStatusCode.OK, (await Client.PostAsync(WebConstants.ApiProbeApply, null)).StatusCode);
        }

        /// <summary>
        /// Starts a mill run held at a closed door, runs <paramref name="body"/> while it waits
        /// there, then stops the run and releases the hold.
        /// </summary>
        public Task WhileAMillRunHoldsAtTheDoorAsync(Func<Task> body, bool? homeFirst = null) =>
            AtAClosedDoorAsync(async () =>
            {
                var started = await Client.PostAsJsonAsync(WebConstants.ApiMillStart, await MillStartBodyAsync(homeFirst));
                Assert.True(started.IsSuccessStatusCode,
                    $"the mill did not start: {await started.Content.ReadAsStringAsync()}");
                WaitUntil(() => AppState.Milling.IsRunInProgress, "the run to take the machine");

                await body();
            });

        /// <summary>
        /// Runs <paramref name="body"/> with the machine held at a closed door, so a run it
        /// starts waits there, then stops any run and releases the hold. The machine is shared,
        /// so the teardown runs in a finally block whether the body passed or not.
        /// </summary>
        public async Task AtAClosedDoorAsync(Func<Task> body)
        {
            Grbl.SimulateDoorClosedAndHolding();
            WaitUntil(
                () => MachineWait.GetDoorState(AppState.Machine) == DoorState.WaitingForResume,
                "the machine to report the door hold");

            try
            {
                await body();
            }
            finally
            {
                await Client.PostAsync(WebConstants.ApiMillStop, null);
                WaitUntil(() => !AppState.Milling.IsRunInProgress, "the run to end");
                await MachineWait.ReleaseDoorHoldAsync(AppState.Machine, ControllerConstants.DoorResumeTimeoutMs);
                WaitUntil(() => !MachineWait.IsDoor(AppState.Machine), "the door hold to clear");
            }
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
