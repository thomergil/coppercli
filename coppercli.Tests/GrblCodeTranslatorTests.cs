using System.Linq;
using coppercli.Core.Util;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The translator turns a bare "error:9" into the message the operator reads. Both
    /// tables are parsed from embedded CSV by one loader, so a pattern that stops matching
    /// empties them and every message falls back to its raw code.
    /// </summary>
    public class GrblCodeTranslatorTests
    {
        public GrblCodeTranslatorTests() => GrblCodeTranslator.Initialize();

        [Fact]
        public void SettingTable_IsPopulatedWithAllThreeColumns()
        {
            Assert.NotEmpty(GrblCodeTranslator.Settings);

            var (name, units, description) = GrblCodeTranslator.Settings.Values.First();
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.NotNull(units);
            Assert.NotNull(description);
        }

        [Fact]
        public void AKnownErrorCode_ExpandsToItsExplanation()
        {
            // 9 is GRBL's "locked out during alarm or jog", present in every build.
            string message = GrblCodeTranslator.GetErrorMessage(9, alarm: false);

            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.NotEqual("9", message);
        }

        [Fact]
        public void AnUnknownErrorCode_StillReturnsSomethingToShow()
        {
            Assert.False(string.IsNullOrWhiteSpace(
                GrblCodeTranslator.GetErrorMessage(9999, alarm: false)));
        }
    }
}
