using coppercli.Core.GCode;
using coppercli.Helpers;

namespace coppercli
{
    /// <summary>What a restore question is about.</summary>
    internal enum SessionRestoreTopic
    {
        /// <summary>Reload the G-code file that was open last time.</summary>
        ReloadFile,

        /// <summary>Trust the work origin stored from the previous session.</summary>
        TrustWorkZero,

        /// <summary>Resolve a height map that was left part-measured.</summary>
        UnfinishedHeightMap,

        /// <summary>Resolve a finished height map that was never saved to a file.</summary>
        UnsavedHeightMap
    }

    /// <summary>One decision the operator has to make before the session is usable.</summary>
    internal sealed record SessionRestoreStep(
        SessionRestoreTopic Topic,
        string Question,
        string Detail,
        bool DefaultYes);

    /// <summary>
    /// The decisions carried over from a previous session, and what answering them does.
    ///
    /// One definition of the sequence, asked by both front ends. The terminal
    /// copy grew a condition that skipped the height-map question whenever the operator
    /// declined to trust the stored work zero, so the data was never resolved and was
    /// later announced as though it were current. The browser copy had no such gate.
    ///
    /// One place now decides which questions apply and what each answer means; the two
    /// interfaces only ask them.
    /// </summary>
    internal static class SessionRestore
    {
        /// <summary>
        /// The questions that still need answering, in the order they must be asked.
        /// Ordering matters: the file is decided first because what a height map
        /// describes is judged against it.
        /// </summary>
        public static List<SessionRestoreStep> GetPendingSteps()
        {
            var steps = new List<SessionRestoreStep>();
            var session = AppState.Session;

            if (!string.IsNullOrEmpty(session.LastLoadedGCodeFile) && File.Exists(session.LastLoadedGCodeFile))
            {
                steps.Add(new SessionRestoreStep(
                    SessionRestoreTopic.ReloadFile,
                    "Reload the file you had open?",
                    Path.GetFileName(session.LastLoadedGCodeFile),
                    DefaultYes: true));
            }

            if ((AppState.Machine?.Connected ?? false) && session.HasStoredWorkZero)
            {
                steps.Add(new SessionRestoreStep(
                    SessionRestoreTopic.TrustWorkZero,
                    "Is the work origin still where you left it?",
                    "The machine has kept its work offset. Say no if the workpiece has moved or been replaced.",
                    DefaultYes: true));
            }

            // Asked whatever the work-zero answer was. Gating this on that answer is the
            // defect this class exists to prevent: the data stayed on disk undecided and
            // resurfaced later claiming to be current.
            var storedMap = AppState.ReadUsableAutosave();

            if (storedMap != null && !storedMap.HasCompleteData)
            {
                steps.Add(new SessionRestoreStep(
                    SessionRestoreTopic.UnfinishedHeightMap,
                    "Keep the unfinished height map?",
                    DescribeStoredMap(),
                    DefaultYes: true));
            }
            else if (storedMap != null)
            {
                steps.Add(new SessionRestoreStep(
                    SessionRestoreTopic.UnsavedHeightMap,
                    "Keep the height map you have not saved?",
                    DescribeStoredMap(),
                    DefaultYes: true));
            }

            return steps;
        }

        /// <summary>
        /// Applies an answer. Every "no" leaves nothing behind - the previous terminal
        /// code discarded on "no" for an unfinished map but did nothing for a finished
        /// one, so declining to keep it still left the file on disk.
        /// </summary>
        public static void Answer(SessionRestoreTopic topic, bool yes)
        {
            switch (topic)
            {
                case SessionRestoreTopic.ReloadFile:
                    if (yes)
                    {
                        LoadStoredFile();
                    }
                    else
                    {
                        // Forget it so we stop asking. The browse directory is kept so
                        // the file picker still opens somewhere useful.
                        AppState.Session.LastLoadedGCodeFile = "";
                        Persistence.SaveSession();
                    }
                    break;

                case SessionRestoreTopic.TrustWorkZero:
                    AppState.TrustWorkZero(yes);
                    Logger.Log("SessionRestore: work zero {0}", yes ? "trusted" : "not trusted");
                    break;

                case SessionRestoreTopic.UnfinishedHeightMap:
                case SessionRestoreTopic.UnsavedHeightMap:
                    if (yes)
                    {
                        KeepStoredMap();
                    }
                    else
                    {
                        Persistence.ClearProbeAutoSave();
                        AppState.DiscardProbeData();
                        Logger.Log("SessionRestore: height map discarded at operator request");
                    }
                    break;
            }
        }

        /// <summary>
        /// Names the board a stored map was measured for, so it can be told apart from
        /// one belonging to the job in hand.
        /// </summary>
        private static string DescribeStoredMap()
        {
            try
            {
                var grid = Persistence.ReadProbeAutoSave();
                if (grid == null)
                {
                    return string.Empty;
                }

                string size = grid.HasCompleteData
                    ? $"{grid.TotalPoints} points"
                    : $"{grid.Progress} of {grid.TotalPoints} points measured";

                return grid.Context.IsKnown
                    ? $"{size}, measured for {Path.GetFileName(grid.Context.SourceFile)}"
                    : $"{size}, from an earlier session (the file it was measured for was not recorded)";
            }
            catch (Exception ex)
            {
                Logger.Log("SessionRestore: could not describe stored map - {0}", ex.Message);
                return "stored height map";
            }
        }

        private static void LoadStoredFile()
        {
            try
            {
                var file = GCodeFile.Load(AppState.Session.LastLoadedGCodeFile);
                AppState.LoadGCodeIntoMachine(file);
            }
            catch (Exception ex)
            {
                Logger.Log("SessionRestore: could not reload file - {0}", ex.Message);
            }
        }

        private static void KeepStoredMap()
        {
            try
            {
                // Through the one adopter, so the map is checked against the job in hand
                // here exactly as it is when the operator presses Recover later.
                AppState.ForceLoadProbeFromAutosave();
            }
            catch (Exception ex)
            {
                // Logged, not shown: this runs on the HTTP thread as well as the terminal's,
                // and ShowError waits for a keypress nobody is there to give.
                Logger.Log("SessionRestore: could not keep stored map - {0}", ex.Message);
            }
        }
    }
}
