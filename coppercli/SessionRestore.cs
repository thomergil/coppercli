using System.Linq;
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
        SetWorkZeroTrusted,

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
    /// One place decides which questions apply and what each answer means; both front ends
    /// only ask them.
    /// </summary>
    internal static class SessionRestore
    {
        /// <summary>
        /// Puts each carried-over question to the operator once, in order, and applies the
        /// answer. Asked one at a time, because answering one changes which of the rest
        /// apply: declining to reload the file leaves a map measured for it describing
        /// nothing.
        ///
        /// The set of topics already asked lives here, not in the caller. Termination cannot
        /// rest on each answer clearing its own condition: reloading the file stores the same
        /// path again, keeping a map leaves the autosave on disk, and trusting the origin
        /// writes a different field from the one the question reads.
        /// </summary>
        /// <param name="ask">Puts one question to the operator. Null means they quit.</param>
        /// <param name="onFailure">Shown when an answer could not be carried out.</param>
        /// <returns>False once the operator quit.</returns>
        public static bool AskPendingSteps(
            Func<SessionRestoreStep, bool?> ask, Action<string> onFailure)
        {
            var answered = new HashSet<SessionRestoreTopic>();

            while (NextPendingStep(answered) is SessionRestoreStep step)
            {
                answered.Add(step.Topic);

                bool? answer = ask(step);
                if (answer == null)
                {
                    return false;
                }

                string? failed = Answer(step.Topic, answer == true);
                if (failed != null)
                {
                    onFailure(failed);
                }
            }

            return true;
        }

        /// <summary>The next question not in <paramref name="answered"/>, or null.</summary>
        private static SessionRestoreStep? NextPendingStep(ISet<SessionRestoreTopic> answered) =>
            GetPendingSteps().FirstOrDefault(step => !answered.Contains(step.Topic));

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
                    SessionRestoreTopic.SetWorkZeroTrusted,
                    "Is the work origin still where you left it?",
                    "The machine has kept its work offset. Say no if the workpiece has moved or been replaced.",
                    DefaultYes: true));
            }

            // Asked whatever the work-zero answer was. Gated on that answer, a map stays
            // on disk undecided and is later reported as current.
            var storedMap = AppState.ReadUsableAutosave();

            if (storedMap != null && !storedMap.HasCompleteData)
            {
                steps.Add(new SessionRestoreStep(
                    SessionRestoreTopic.UnfinishedHeightMap,
                    "Keep the unfinished height map?",
                    DescribeStoredMap(storedMap),
                    DefaultYes: true));
            }
            else if (storedMap != null)
            {
                steps.Add(new SessionRestoreStep(
                    SessionRestoreTopic.UnsavedHeightMap,
                    "Keep the height map you have not saved?",
                    DescribeStoredMap(storedMap),
                    DefaultYes: true));
            }

            return steps;
        }

        /// <summary>
        /// Applies an answer. Every "no" leaves nothing behind: a map the operator declines
        /// to keep is deleted from disk, finished or not.
        /// </summary>
        /// <returns>What went wrong, or null once the answer was carried out.</returns>
        public static string? Answer(SessionRestoreTopic topic, bool yes)
        {
            switch (topic)
            {
                case SessionRestoreTopic.ReloadFile:
                    if (yes)
                    {
                        return LoadStoredFile();
                    }
                    else
                    {
                        // Forget it so we stop asking. The browse directory is kept so
                        // the file picker still opens somewhere useful.
                        AppState.Session.LastLoadedGCodeFile = "";
                        Persistence.SaveSession();
                    }
                    break;

                case SessionRestoreTopic.SetWorkZeroTrusted:
                    AppState.SetWorkZeroTrusted(yes);
                    Logger.Log("SessionRestore: work zero {0}", yes ? "trusted" : "not trusted");
                    break;

                case SessionRestoreTopic.UnfinishedHeightMap:
                case SessionRestoreTopic.UnsavedHeightMap:
                    if (yes)
                    {
                        return KeepStoredMap();
                    }

                    string? notDiscarded = AppState.DiscardProbeDataAndAutosave();
                    Logger.Log(
                        "SessionRestore: {0}",
                        notDiscarded ?? "height map discarded at operator request");
                    return notDiscarded;
            }

            return null;
        }

        /// <summary>
        /// Names the board a stored map was measured for, so it can be told apart from
        /// one belonging to the job in hand.
        /// </summary>
        private static string DescribeStoredMap(ProbeGrid grid)
        {
            try
            {
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

        /// <returns>Why the file was not loaded, or null once it was.</returns>
        private static string? LoadStoredFile()
        {
            try
            {
                var file = GCodeFile.Load(AppState.Session.LastLoadedGCodeFile);
                string? refused = AppState.LoadGCodeIntoMachine(file).Refused;
                if (refused != null)
                {
                    Logger.Log("SessionRestore: {0}", refused);
                }

                return refused;
            }
            catch (Exception ex)
            {
                // The exception names offsets and types the operator cannot act on.
                Logger.Log("SessionRestore: could not reload file - {0}", ex.Message);
                return CliConstants.ErrorFileNotLoaded;
            }
        }

        /// <returns>Why the map was not kept, or null once it was.</returns>
        private static string? KeepStoredMap()
        {
            try
            {
                // Through the one adopter, so the map is checked against the current job
                // here exactly as it is when the operator presses Recover.
                var (_, refused) = AppState.ForceLoadProbeFromAutosave();
                if (refused != null)
                {
                    Logger.Log("SessionRestore: {0}", refused);
                }

                return refused;
            }
            catch (Exception ex)
            {
                Logger.Log("SessionRestore: could not keep stored map - {0}", ex.Message);
                return CliConstants.ProbeAutosaveNotApplicable;
            }
        }
    }
}
