using System.Linq;
using coppercli.Core.Settings;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// The settings table is hand-written, one row per setting. A row that reads one property
    /// and writes another passes every other test: the operator sets a max depth and gets a
    /// probe feed.
    /// </summary>
    public class SettingRangeTests
    {
        /// <summary>A value no default uses, so reading it back proves the write landed.</summary>
        private const double Marker = 12345.0;

        [Fact]
        public void EverySetting_ReadsAndWritesItsOwnProperty()
        {
            foreach (var binding in SettingRanges.All)
            {
                var settings = new MachineSettings();
                binding.Write(settings, Marker);

                Assert.Equal(Marker, binding.Read(settings));

                // A row wired to a neighbor's property leaves that neighbor holding the
                // marker instead of its default.
                var untouched = new MachineSettings();
                foreach (var other in SettingRanges.All.Where(o => o.Range.Name != binding.Range.Name))
                {
                    Assert.Equal(other.Read(untouched), other.Read(settings));
                }
            }
        }

        [Fact]
        public void EverySetting_IsNamedOnce()
        {
            var names = SettingRanges.All.Select(b => b.Range.Name).ToList();

            Assert.Equal(names.Count, names.Distinct().Count());
        }

        /// <summary>
        /// A default outside its own range makes the loader replace a bad value with another
        /// bad one.
        /// </summary>
        [Fact]
        public void EveryDefault_IsUsable()
        {
            var defaults = new MachineSettings();

            foreach (var binding in SettingRanges.All)
            {
                Assert.Null(binding.Range.Check(binding.Read(defaults)));
            }
        }

        [Fact]
        public void AValueThatIsNotANumber_IsRefused()
        {
            foreach (var binding in SettingRanges.All)
            {
                Assert.NotNull(binding.Range.Check(double.NaN));
                Assert.NotNull(binding.Range.Check(double.PositiveInfinity));
                Assert.NotNull(binding.Range.Check(double.NegativeInfinity));
            }
        }

        /// <summary>
        /// A Check returning false for every value passes a suite that asserts only refusals,
        /// and blocks every edit on the settings screen.
        /// </summary>
        [Fact]
        public void EverySetting_AcceptsAValueItsRangeAllows()
        {
            foreach (var binding in SettingRanges.All)
            {
                Assert.Null(binding.Range.Check(Marker));
            }
        }

        [Fact]
        public void ASettingThatMustBePositive_RefusesZeroAndBelow()
        {
            foreach (var binding in SettingRanges.All.Where(b => b.Range.MustBePositive))
            {
                Assert.NotNull(binding.Range.Check(0));
                Assert.NotNull(binding.Range.Check(-1));
            }
        }

        /// <summary>
        /// Coordinates, offsets and weights may be zero or negative. Refusing those would
        /// make the machine unconfigurable.
        /// </summary>
        [Fact]
        public void ACoordinateSetting_TakesZeroAndNegativeValues()
        {
            foreach (var binding in SettingRanges.All.Where(b => !b.Range.MustBePositive))
            {
                Assert.Null(binding.Range.Check(0));
                Assert.Null(binding.Range.Check(-42.5));
            }
        }

        [Fact]
        public void ARefusal_NamesTheSetting()
        {
            foreach (var binding in SettingRanges.All)
            {
                string refused = binding.Range.Check(double.NaN)!;

                Assert.Contains(binding.Range.Name, refused);
            }
        }
    }
}
