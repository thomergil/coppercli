using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace coppercli.Core.Util
{
    public static class GrblCodeTranslator
    {
        static Dictionary<int, string> GrblErrors = new Dictionary<int, string>();
        static Dictionary<int, string> GrblAlarms = new Dictionary<int, string>();

        /// <summary>
        /// setting name, unit, description
        /// </summary>
        public static Dictionary<int, Tuple<string, string, string>> Settings = new Dictionary<int, Tuple<string, string, string>>();

        private static bool _initialized = false;

        /// <summary>
        /// Reads a quoted-CSV resource into a dictionary keyed by its first column.
        ///
        /// The two loaders differed only in how many columns they read and what they built
        /// from them, so those are the two parameters.
        /// </summary>
        private static void LoadCsvResource<T>(
            Dictionary<int, T> dict, string resourceName, Regex lineParser, Func<Match, T> build)
        {
            try
            {
                string content = LoadEmbeddedResource(resourceName);
                if (string.IsNullOrEmpty(content))
                {
                    return;
                }

                foreach (Match m in lineParser.Matches(content))
                {
                    try
                    {
                        dict[int.Parse(m.Groups[1].Value)] = build(m);
                    }
                    catch
                    {
                        // One malformed line must not cost the whole table.
                    }
                }
            }
            catch (Exception ex)
            {
                Controllers.ControllerLog.Log($"Could not read {resourceName}: {ex.Message}");
            }
        }

        private static readonly Regex ErrorLineParser =
            new(@"""([0-9]+)"",""[^\n\r""]*"",""([^\n\r""]*)""");

        private static readonly Regex SettingLineParser =
            new(@"""([0-9]+)"",""([^\n\r""]*)"",""([^\n\r""]*)"",""([^\n\r""]*)""");

        private static void LoadErr(Dictionary<int, string> dict, string resourceName)
            => LoadCsvResource(dict, resourceName, ErrorLineParser, m => m.Groups[2].Value);

        private static void LoadSettings(Dictionary<int, Tuple<string, string, string>> dict, string resourceName)
            => LoadCsvResource(dict, resourceName, SettingLineParser,
                m => Tuple.Create(m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value));

        private static string LoadEmbeddedResource(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var fullName = $"coppercli.Core.Resources.{resourceName}";

            using (Stream stream = assembly.GetManifestResourceStream(fullName))
            {
                if (stream == null)
                {
                    // Try loading from file path relative to app directory
                    string basePath = AppContext.BaseDirectory;
                    string filePath = Path.Combine(basePath, "Resources", resourceName);
                    if (System.IO.File.Exists(filePath))
                    {
                        return System.IO.File.ReadAllText(filePath);
                    }
                    return null;
                }

                using (StreamReader reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            Console.WriteLine("Loading GRBL Code Database");

            LoadErr(GrblErrors, "grbl_error_codes_en_US.csv");
            LoadErr(GrblAlarms, "grbl_alarm_codes_en_US.csv");
            LoadSettings(Settings, "grbl_setting_codes_en_US.csv");

            _initialized = true;
            Console.WriteLine("Loaded GRBL Code Database");
        }

        /// <summary>
        /// The sentence behind a bare GRBL code, or a readable stand-in when the code is
        /// not in the table.
        /// </summary>
        public static string GetErrorMessage(int errorCode, bool alarm)
        {
            Initialize();

            var dict = alarm ? GrblAlarms : GrblErrors;

            return dict.TryGetValue(errorCode, out string message)
                ? message
                : alarm ? $"Unknown Alarm: {errorCode}" : $"Unknown Error: {errorCode}";
        }

        private static readonly Regex ErrorExp = new(@"error:(\d+)");
        private static readonly Regex AlarmExp = new(@"ALARM:(\d+)");

        /// <summary>
        /// Replaces every bare code in a line from the machine with what it means.
        /// </summary>
        public static string ExpandError(string error)
        {
            Initialize();

            string expanded = ErrorExp.Replace(
                error, m => GetErrorMessage(int.Parse(m.Groups[1].Value), alarm: false));

            return AlarmExp.Replace(
                expanded, m => GetErrorMessage(int.Parse(m.Groups[1].Value), alarm: true));
        }
    }
}
