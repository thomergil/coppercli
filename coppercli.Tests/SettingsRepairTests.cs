using coppercli;
using coppercli.Core.Settings;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The settings file is the operator's to edit. A value the machine cannot work to
    /// reaches a G-code line, so the loader puts back the default for that one setting and
    /// leaves the rest as the file has them.
    /// </summary>
    public class SettingsRepairTests
    {
        [Fact]
        public void AnUnusableSetting_IsReplacedByItsDefault()
        {
            var defaults = new MachineSettings();
            var settings = new MachineSettings
            {
                ProbeFeed = 0,
                ProbeSafeHeight = -1,
                JogFeed = double.NaN,

                // Usable, and not the default, so a repair that resets everything is caught.
                ProbeMaxDepth = 7.5
            };

            Persistence.RepairOutOfRangeSettings(settings);

            Assert.Equal(defaults.ProbeFeed, settings.ProbeFeed);
            Assert.Equal(defaults.ProbeSafeHeight, settings.ProbeSafeHeight);
            Assert.Equal(defaults.JogFeed, settings.JogFeed);
            Assert.Equal(7.5, settings.ProbeMaxDepth);
        }

        [Fact]
        public void AFileOfUsableSettings_IsLeftAsItIs()
        {
            var settings = new MachineSettings
            {
                ProbeFeed = 31,
                ProbeMaxDepth = 32,
                ToolSetterX = -33,
                ProbeOffsetY = -0.5
            };

            Persistence.RepairOutOfRangeSettings(settings);

            Assert.Equal(31, settings.ProbeFeed);
            Assert.Equal(32, settings.ProbeMaxDepth);
            Assert.Equal(-33, settings.ToolSetterX);
            Assert.Equal(-0.5, settings.ProbeOffsetY);
        }

        /// <summary>
        /// Every setting the table covers, one at a time, so a row that reads or writes the
        /// wrong property shows up as a neighbour being reset.
        /// </summary>
        [Fact]
        public void RepairingOneSetting_LeavesTheOthersAlone()
        {
            foreach (var binding in SettingRanges.All)
            {
                // A distinct usable value per setting, so a neighbour that was reset holds
                // its default instead of the value seeded here.
                var settings = new MachineSettings();
                double seed = 1000.0;
                foreach (var each in SettingRanges.All)
                {
                    each.Write(settings, seed);
                    seed += 1.0;
                }

                double unusable = binding.Range.MustBePositive ? 0 : double.NaN;
                binding.Write(settings, unusable);

                Persistence.RepairOutOfRangeSettings(settings);

                var defaults = new MachineSettings();
                Assert.Equal(binding.Read(defaults), binding.Read(settings));

                seed = 1000.0;
                foreach (var other in SettingRanges.All)
                {
                    if (other.Range.Name != binding.Range.Name)
                    {
                        Assert.Equal(seed, other.Read(settings));
                    }

                    seed += 1.0;
                }
            }
        }
    }
}
