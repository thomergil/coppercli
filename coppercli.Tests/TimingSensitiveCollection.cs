using Xunit;

namespace coppercli.Tests
{
    /// <summary>
    /// Tests that measure elapsed time or wait on timers against short timeouts. Run beside
    /// the rest on a CI runner with few cores, they miss those timeouts, so this collection
    /// runs on its own once the others have finished.
    /// </summary>
    [CollectionDefinition(TimingSensitiveCollection.Name, DisableParallelization = true)]
    public class TimingSensitiveCollection
    {
        public const string Name = "timing-sensitive";
    }
}
