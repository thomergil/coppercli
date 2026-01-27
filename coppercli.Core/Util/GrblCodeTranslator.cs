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
        static Dictionary<int, string> UcncErrors = new Dictionary<int, string>();
        static Dictionary<int, string> UcncAlarms = new Dictionary<int, string>();

        /// <summary>
        /// setting name, unit, description
        /// </summary>
        public static Dictionary<int, Tuple<string, string, string>> Settings = new Dictionary<int, Tuple<string, string, string>>();

        private static bool _initialized = false;

        private static void LoadErr(Dictionary<int, string> dict, string resourceName)
        {
            try
            {
                string content = LoadEmbeddedResource(resourceName);
                if (string.IsNullOrEmpty(content))
                    return;

                Regex LineParser = new Regex(@"""([0-9]+)"",""[^\n\r""]*"",""([^\n\r""]*)""");

                MatchCollection mc = LineParser.Matches(content);

                foreach (Match m in mc)
                {
                    try
                    {
                        int number = int.Parse(m.Groups[1].Value);
                        dict[number] = m.Groups[2].Value;
                    }
                    catch
                    {
                        // Skip malformed lines in error code file
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading {resourceName}: {ex.Message}");
            }
        }

        private static void LoadSettings(Dictionary<int, Tuple<string, string, string>> dict, string resourceName)
        {
            try
            {
                string content = LoadEmbeddedResource(resourceName);
                if (string.IsNullOrEmpty(content))
                {
                    return;
                }

                Regex LineParser = new Regex(@"""([0-9]+)"",""([^\n\r""]*)"",""([^\n\r""]*)"",""([^\n\r""]*)""");

                MatchCollection mc = LineParser.Matches(content);

                foreach (Match m in mc)
                {
                    try
                    {
                        int number = int.Parse(m.Groups[1].Value);
                        dict[number] = new Tuple<string, string, string>(m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
                    }
                    catch
                    {
                        // Skip malformed lines in settings file
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading {resourceName}: {ex.Message}");
            }
        }

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
                return;

            Console.WriteLine("Loading GRBL Code Database");

            LoadErr(GrblErrors, "grbl_error_codes_en_US.csv");
            LoadErr(GrblAlarms, "grbl_alarm_codes_en_US.csv");
            LoadErr(UcncErrors, "ucnc_error_codes_en_US.csv");
            LoadErr(UcncAlarms, "ucnc_alarm_codes_en_US.csv");
            LoadSettings(Settings, "grbl_setting_codes_en_US.csv");

            _initialized = true;
            Console.WriteLine("Loaded GRBL Code Database");
        }

        public static string GetErrorMessage(int errorCode, bool alarm, string firmwareType = "Grbl")
        {
            Initialize();

            Dictionary<int, string> dict;

            if (firmwareType == "uCNC")
            {
                dict = alarm ? UcncAlarms : UcncErrors;
            }
            else
            {
                dict = alarm ? GrblAlarms : GrblErrors;
            }

            if (dict.ContainsKey(errorCode))
                return dict[errorCode];
            else
                return alarm ? $"Unknown Alarm: {errorCode}" : $"Unknown Error: {errorCode}";
        }

        static Regex ErrorExp = new Regex(@"error:(\d+)");
        private static string ErrorMatchEvaluator(Match m, string firmwareType)
        {
            return GetErrorMessage(int.Parse(m.Groups[1].Value), false, firmwareType);
        }

        static Regex AlarmExp = new Regex(@"ALARM:(\d+)");
        private static string AlarmMatchEvaluator(Match m, string firmwareType)
        {
            return GetErrorMessage(int.Parse(m.Groups[1].Value), true, firmwareType);
        }

        public static string ExpandError(string error, string firmwareType = "Grbl")
        {
            Initialize();

            string ret = ErrorExp.Replace(error, m => ErrorMatchEvaluator(m, firmwareType));
            return AlarmExp.Replace(ret, m => AlarmMatchEvaluator(m, firmwareType));
        }
    }
}
