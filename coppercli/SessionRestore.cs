using System.Linq;
using coppercli.Core.GCode;
using coppercli.Helpers;

namespace coppercli
{
    internal enum SessionRestoreTopic
    {
        ReloadFile,

        SetWorkZeroTrusted,

        UnfinishedHeightMap,

        UnsavedHeightMap,

        SavedHeightMap
    }

    internal sealed record SessionRestoreStep(
        SessionRestoreTopic Topic,
        string Question,
        string Detail,
        bool DefaultYes);

    /// <summary>
    /// Questions carried over from an earlier session. Both front ends ask them at startup and
    /// after a G-code load; only this class decides which apply and what an answer does.
    /// </summary>
    internal static class SessionRestore
    {
        /// <summary>
        /// The terminal and browser tabs can answer at once, so the pending check and the
        /// answer share one lock.
        /// </summary>
        private static readonly object AnswerLock = new();

        /// <summary>
        /// One question at a time, because each answer changes which of the rest apply:
        /// declining the file leaves its map describing nothing. A successful answer clears its
        /// question. A failed one stays pending for the next pass and is skipped for the rest of
        /// this one, so the operator does not see the same failure twice and the rest still get
        /// asked.
        /// </summary>
        /// <param name="ask">Puts one question to the operator; a null answer means they quit.</param>
        /// <returns>False once the operator quit.</returns>
        public static bool AskPendingSteps(
            Func<SessionRestoreStep, bool?> ask, Action<string> onFailure)
        {
            var failedThisPass = new HashSet<SessionRestoreTopic>();

            while (GetPendingSteps().FirstOrDefault(step => !failedThisPass.Contains(step.Topic))
                   is SessionRestoreStep step)
            {
                bool? answer = ask(step);
                if (answer == null)
                {
                    return false;
                }

                string? failed = Answer(step.Topic, step.Detail, answer == true);
                if (failed != null)
                {
                    onFailure(failed);
                    failedThisPass.Add(step.Topic);
                }
            }

            return true;
        }

        /// <summary>
        /// The file comes first, because a height map is judged against it. Each answer, yes or
        /// no, makes its own condition false, so no caller tracks what it asked.
        /// </summary>
        public static List<SessionRestoreStep> GetPendingSteps() => ReadQuestions().Pending;

        /// <summary>
        /// "No" to an unfinished or unsaved map deletes that autosave; "no" to the saved map
        /// only forgets its path. An answer is carried out only while a question with this topic
        /// and detail is pending, so a second screen's answer, or one to a question that changed
        /// since it was shown, is refused. The refusal names the missing file or the disconnect
        /// when that is why the question cannot be asked.
        /// </summary>
        /// <param name="shownDetail">The detail of the question the operator answered.</param>
        /// <returns>What went wrong, or null once the answer was carried out.</returns>
        public static string? Answer(SessionRestoreTopic topic, string shownDetail, bool yes)
        {
            lock (AnswerLock)
            {
                var questions = ReadQuestions();
                if (!questions.Pending.Any(step => step.Topic == topic && step.Detail == shownDetail))
                {
                    string refused = questions.Unavailable
                        .FirstOrDefault(question =>
                            question.Topic == topic && question.MatchesShownDetail(shownDetail))
                        ?.Refused
                        ?? CliConstants.ErrorQuestionAlreadyAnswered;
                    Logger.Log("SessionRestore: {0} is not pending as shown, answer ignored: {1}", topic, refused);
                    return refused;
                }

                return CarryOut(topic, yes);
            }
        }

        /// <summary>
        /// A question that no answer has settled but that cannot be asked, because the file it
        /// is about is missing or the machine is not connected. The file can be deleted, or the
        /// machine can disconnect, while the question is on screen; the refusal then names the
        /// cause instead of saying the question was already answered.
        /// </summary>
        /// <param name="MatchesShownDetail">True if a detail the operator was shown belongs to this question.</param>
        private sealed record UnavailableQuestion(
            SessionRestoreTopic Topic, Func<string, bool> MatchesShownDetail, string Refused);

        private sealed record SessionQuestions(
            List<SessionRestoreStep> Pending, List<UnavailableQuestion> Unavailable);

        /// <summary>
        /// Reads every condition in one pass, so the pending questions and the refusal that
        /// <see cref="Answer"/> returns come from the same check of the disk and the machine
        /// connection.
        /// </summary>
        private static SessionQuestions ReadQuestions()
        {
            var pending = new List<SessionRestoreStep>();
            var unavailable = new List<UnavailableQuestion>();
            var session = AppState.Session;

            void Record(SessionRestoreStep step, string? unavailableBecause)
            {
                if (unavailableBecause == null)
                {
                    pending.Add(step);
                }
                else
                {
                    unavailable.Add(new UnavailableQuestion(
                        step.Topic, shownDetail => shownDetail == step.Detail, unavailableBecause));
                }
            }

            if (AppState.CurrentFile == null && !string.IsNullOrEmpty(session.LastLoadedGCodeFile))
            {
                Record(
                    new SessionRestoreStep(
                        SessionRestoreTopic.ReloadFile,
                        CliConstants.ReloadFileQuestion,
                        Path.GetFileName(session.LastLoadedGCodeFile),
                        DefaultYes: true),
                    DescribeIfMissing(session.LastLoadedGCodeFile));
            }

            if (session.HasStoredWorkZero && !AppState.IsWorkZeroSet)
            {
                Record(
                    new SessionRestoreStep(
                        SessionRestoreTopic.SetWorkZeroTrusted,
                        CliConstants.StoredWorkZeroQuestion,
                        CliConstants.StoredWorkZeroDetail,
                        DefaultYes: true),
                    (AppState.Machine?.Connected ?? false) ? null : CliConstants.ErrorSessionDisconnected);
            }

            // Read regardless of the work-zero answer, or a declined origin would leave the map
            // undecided on disk and later shown as current. Keeping the map adopts it, which
            // ends this question.
            var storedMap = AppState.ReadAutosaveNotYetAdopted();

            if (storedMap != null)
            {
                pending.Add(new SessionRestoreStep(
                    storedMap.HasCompleteData
                        ? SessionRestoreTopic.UnsavedHeightMap
                        : SessionRestoreTopic.UnfinishedHeightMap,
                    storedMap.HasCompleteData
                        ? CliConstants.UnsavedHeightMapQuestion
                        : CliConstants.UnfinishedHeightMapQuestion,
                    DescribeStoredMap(storedMap),
                    DefaultYes: true));
            }
            else if (AppState.SavedProbeFileToOffer is string savedFile)
            {
                // Saving deletes the autosave read above; without this a saved map is never
                // offered again.
                string mapName = Path.GetFileName(savedFile);
                if (AppState.ReadUsableSavedProbeGrid(savedFile) is ProbeGrid saved)
                {
                    pending.Add(new SessionRestoreStep(
                        SessionRestoreTopic.SavedHeightMap,
                        CliConstants.SavedHeightMapQuestion,
                        string.Format(CliConstants.SavedHeightMapDetail, mapName, DescribeStoredMap(saved)),
                        DefaultYes: true));
                }
                else if (DescribeIfMissing(savedFile) is string missing)
                {
                    // The map file is missing, so its description cannot be built; match the
                    // shown detail by the file name it starts with.
                    string nameShown = string.Format(CliConstants.SavedHeightMapDetail, mapName, "");
                    unavailable.Add(new UnavailableQuestion(
                        SessionRestoreTopic.SavedHeightMap,
                        shownDetail => shownDetail.StartsWith(nameShown, StringComparison.Ordinal),
                        missing));
                }
            }

            return new SessionQuestions(pending, unavailable);
        }

        /// <returns>The refusal naming <paramref name="path"/>, or null if the file exists.</returns>
        private static string? DescribeIfMissing(string path) =>
            File.Exists(path) ? null : string.Format(CliConstants.ErrorSessionFileGone, Path.GetFileName(path));

        private static string? CarryOut(SessionRestoreTopic topic, bool yes)
        {
            switch (topic)
            {
                case SessionRestoreTopic.ReloadFile:
                    if (yes)
                    {
                        return LoadStoredFile();
                    }

                    // Forgetting the path stops the question returning; the browse directory
                    // stays, so the file picker still opens somewhere useful.
                    AppState.Session.LastLoadedGCodeFile = "";
                    Persistence.SaveSession();
                    return null;

                case SessionRestoreTopic.SetWorkZeroTrusted:
                    AppState.SetWorkZeroTrusted(yes);
                    if (!yes)
                    {
                        // The stored origin is wrong, so none is offered until all three axes
                        // are zeroed again.
                        AppState.Session.HasStoredWorkZero = false;
                        Persistence.SaveSession();
                    }
                    Logger.Log("SessionRestore: work zero {0}", yes ? "trusted" : "not trusted");
                    return null;

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

                // The file stays: it is the operator's, not an autosave. Loading it from the
                // Probe menu offers it again.
                case SessionRestoreTopic.SavedHeightMap:
                    if (yes)
                    {
                        return ApplySavedMap();
                    }

                    AppState.Session.LastProbeFile = "";
                    Persistence.SaveSession();
                    return null;
            }

            return null;
        }

        /// <summary>
        /// Names the file the map was measured for, so the operator can tell it from the
        /// loaded job.
        /// </summary>
        private static string DescribeStoredMap(ProbeGrid grid)
        {
            try
            {
                string size = grid.HasCompleteData
                    ? string.Format(CliConstants.StoredMapPoints, grid.TotalPoints)
                    : string.Format(CliConstants.StoredMapPointsMeasured, grid.Progress, grid.TotalPoints);

                return grid.Context.IsKnown
                    ? string.Format(CliConstants.StoredMapMeasuredFor, size, Path.GetFileName(grid.Context.SourceFile))
                    : string.Format(CliConstants.StoredMapFileNotRecorded, size);
            }
            catch (Exception ex)
            {
                Logger.Log("SessionRestore: could not describe stored map - {0}", ex.Message);
                return CliConstants.StoredMapUndescribed;
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

        /// <returns>Why the map was not applied, or null once it was.</returns>
        private static string? ApplySavedMap()
        {
            try
            {
                // Adopts the grid that was checked, from the path that was checked, so a file
                // replaced on disk since cannot be applied instead.
                if (AppState.SavedProbeFileToOffer is not string path
                    || AppState.ReadUsableSavedProbeGrid(path) is not ProbeGrid saved)
                {
                    return CliConstants.ErrorSavedHeightMapNotRead;
                }

                var (_, refused) = AppState.AdoptProbeGridFromFile(saved, path);
                string? failed = refused ?? AppState.ApplyProbeData();
                Logger.Log("SessionRestore: saved height map {0}", failed ?? "applied");
                return failed;
            }
            catch (Exception ex)
            {
                Logger.Log("SessionRestore: could not apply saved map - {0}", ex.Message);
                return CliConstants.ErrorSavedHeightMapNotRead;
            }
        }

        /// <returns>Why the map was not kept, or null once it was.</returns>
        private static string? KeepStoredMap()
        {
            try
            {
                // The same call the Recover button makes, so the map is checked against the
                // current job the same way.
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
