using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using static coppercli.Core.Util.Constants;

namespace coppercli.Helpers
{
    internal static class MachineProfiles
    {
        private const string ProfilesFileName = "machine-profiles.yaml";

        private static MachineProfilesFile? _cachedProfiles;

        public static MachineProfilesFile Load()
        {
            if (_cachedProfiles != null)
            {
                return _cachedProfiles;
            }

            string profilesPath = GetProfilesPath();
            if (!File.Exists(profilesPath))
            {
                return new MachineProfilesFile();
            }

            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .Build();

                string yaml = File.ReadAllText(profilesPath);
                _cachedProfiles = deserializer.Deserialize<MachineProfilesFile>(yaml) ?? new MachineProfilesFile();
                return _cachedProfiles;
            }
            catch
            {
                return new MachineProfilesFile();
            }
        }

        public static List<string> GetProfileIds()
        {
            var profiles = Load();
            return profiles.Machines?.Keys.ToList() ?? new List<string>();
        }

        public static MachineProfile? GetProfile(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            var profiles = Load();
            if (profiles.Machines != null && profiles.Machines.TryGetValue(id, out var profile))
            {
                return profile;
            }
            return null;
        }

        public static bool HasToolSetter()
        {
            var settings = AppState.Settings;

            if (settings.ToolSetterX != 0 || settings.ToolSetterY != 0)
            {
                return true;
            }

            var profile = GetProfile(settings.MachineProfile);
            return profile?.ToolSetter != null;
        }

        public static ToolSetterConfig? GetToolSetterConfig()
        {
            var settings = AppState.Settings;

            // A manual position overrides the profile, and zero in both axes means none is set.
            if (settings.ToolSetterX != 0 || settings.ToolSetterY != 0)
            {
                return new ToolSetterConfig
                {
                    X = settings.ToolSetterX,
                    Y = settings.ToolSetterY != 0 ? settings.ToolSetterY : null,
                    ProbeDepth = ToolSetterProbeDepth,
                    FastFeed = ToolSetterSeekFeed,
                    SlowFeed = ToolSetterProbeFeed,
                    Retract = ToolSetterRetract
                };
            }

            var profile = GetProfile(settings.MachineProfile);
            return profile?.ToolSetter;
        }

        public static (double X, double? Y)? GetToolSetterPosition()
        {
            var config = GetToolSetterConfig();
            if (config != null)
            {
                return (config.X, config.Y);
            }
            return null;
        }

        private static string GetProfilesPath()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(exeDir, ProfilesFileName);
            if (File.Exists(path))
            {
                return path;
            }

            // A development build runs from the project directory, where the file is not
            // beside the executable.
            return ProfilesFileName;
        }
    }

    public class MachineProfilesFile
    {
        public Dictionary<string, MachineProfile>? Machines { get; set; }
    }

    public class MachineProfile
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public ToolSetterConfig? ToolSetter { get; set; }
    }

    public class ToolSetterConfig
    {
        public double X { get; set; }
        /// <summary>
        /// Null on a machine with a moving bed, such as the Nomad 3, where only X is needed
        /// to reach the tool setter.
        /// </summary>
        public double? Y { get; set; }

        // Defaults come from the same constants the tool-change controller falls back to when
        // a profile has no tool setter, so an omitted value and the fallback agree. SlowFeed
        // sets the accuracy of the tool-length measurement.
        public double ProbeDepth { get; set; } = Core.Util.Constants.ToolSetterProbeDepth;
        public double FastFeed { get; set; } = Core.Util.Constants.ToolSetterSeekFeed;
        public double SlowFeed { get; set; } = Core.Util.Constants.ToolSetterProbeFeed;
        public double Retract { get; set; } = Core.Util.Constants.ToolSetterRetract;
    }
}
