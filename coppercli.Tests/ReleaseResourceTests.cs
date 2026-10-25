using coppercli.Core.Util;
using coppercli.Helpers;
using Xunit;

namespace coppercli.Tests
{
    public class ReleaseResourceTests
    {
        [Fact]
        public void GrblTablesAreEmbeddedInTheCoreAssembly()
        {
            var assembly = typeof(GrblCodeTranslator).Assembly;
            foreach (string name in new[]
            {
                "grbl_error_codes_en_US.csv",
                "grbl_alarm_codes_en_US.csv",
                "grbl_setting_codes_en_US.csv"
            })
            {
                using var stream = assembly.GetManifestResourceStream($"coppercli.Core.Resources.{name}");
                Assert.NotNull(stream);
                Assert.True(stream.Length > 0);
            }
        }

        [Fact]
        public void BuiltInMachineProfilesAreEmbeddedInTheAppAssembly()
        {
            var assembly = typeof(MachineProfiles).Assembly;
            using var stream = assembly.GetManifestResourceStream("coppercli.machine-profiles.yaml");
            Assert.NotNull(stream);
            Assert.True(stream.Length > 0);
        }
    }
}
