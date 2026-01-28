using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace coppercli.Helpers
{
    /// <summary>
    /// Helper for loading and querying machine profiles from YAML.
    /// </summary>
    internal static class MachineProfiles
    {
        private const string ProfilesFileName = "machine-profiles.yaml";

        private static MachineProfilesFile? _cachedProfiles;

        /// <summary>
        /// Load machine profiles from YAML file.
        /// </summary>
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

        /// <summary>
        /// Get list of available machine profile IDs.
        /// </summary>
        public static List<string> GetProfileIds()
        {
            var profiles = Load();
            return profiles.Machines?.Keys.ToList() ?? new List<string>();
        }

        /// <summary>
        /// Get a specific machine profile by ID.
        /// </summary>
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

        /// <summary>
        /// Check if a tool setter is configured (either from profile or manual settings).
        /// </summary>
        public static bool HasToolSetter()
        {
            var settings = AppState.Settings;

            // Manual override takes precedence
            if (settings.ToolSetterX != 0 || settings.ToolSetterY != 0)
            {
                return true;
            }

            // Check selected profile
            var profile = GetProfile(settings.MachineProfile);
            return profile?.ToolSetter != null;
        }

        /// <summary>
        /// Get the effective tool setter configuration.
        /// Manual settings override profile settings.
        /// Returns null if no tool setter is configured.
        /// </summary>
        public static ToolSetterConfig? GetToolSetterConfig()
        {
            var settings = AppState.Settings;

            // Manual override takes precedence
            if (settings.ToolSetterX != 0 || settings.ToolSetterY != 0)
            {
                return new ToolSetterConfig
                {
                    X = settings.ToolSetterX,
                    // Only set Y if non-zero (0 means "not configured" for manual override)
                    Y = settings.ToolSetterY != 0 ? settings.ToolSetterY : null,
                    ProbeDepth = CliConstants.ToolSetterProbeDepth,
                    FastFeed = CliConstants.ToolSetterSeekFeed,
                    SlowFeed = CliConstants.ToolSetterProbeFeed,
                    Retract = CliConstants.ToolSetterRetract
                };
            }

            // Fall back to profile
            var profile = GetProfile(settings.MachineProfile);
            return profile?.ToolSetter;
        }

        /// <summary>
        /// Get tool setter position (X, required; Y, optional).
        /// Returns null if no tool setter is configured.
        /// Y is null for machines where only X matters (e.g., moving-bed machines like Nomad 3).
        /// </summary>
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
            // Look for profiles file next to the executable
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(exeDir, ProfilesFileName);
            if (File.Exists(path))
            {
                return path;
            }

            // Fall back to current directory (for development)
            return ProfilesFileName;
        }
    }

    /// <summary>
    /// Root structure of machine-profiles.yaml
    /// </summary>
    public class MachineProfilesFile
    {
        public Dictionary<string, MachineProfile>? Machines { get; set; }
    }

    /// <summary>
    /// A single machine profile.
    /// </summary>
    public class MachineProfile
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public ToolSetterConfig? ToolSetter { get; set; }
    }

    /// <summary>
    /// Tool setter configuration including position and probe parameters.
    /// </summary>
    public class ToolSetterConfig
    {
        public double X { get; set; }
        /// <summary>
        /// Y position for tool setter. Nullable because some machines (e.g., Nomad 3)
        /// have moving beds where only X matters to reach the tool setter.
        /// </summary>
        public double? Y { get; set; }
        public double ProbeDepth { get; set; } = 50.0;
        public double FastFeed { get; set; } = 800.0;
        public double SlowFeed { get; set; } = 200.0;
        public double Retract { get; set; } = 3.0;
    }
}
