#nullable enable
namespace coppercli.Core.Controllers
{
    /// <summary>
    /// The chosen sections and the depth adjustment of the job a milling run streams, as far as
    /// the run can change them: at the tool change the machine's stream stopped at, the
    /// operator may end them for the rest of the job.
    /// </summary>
    public interface ISectionsAndDepth
    {
        /// <summary>
        /// Whether the sections or the depth adjustment apply after the tool change the stream
        /// stopped at, and the job cuts before it. A tool change before any cut, such as
        /// Fusion's T1 M6, has no milling behind it to keep them for.
        /// </summary>
        bool ApplyAfterTheToolChange();

        /// <summary>
        /// Rebuilds the machine's G-code so the sections and depth adjustment end at the tool
        /// change the stream stopped at. The lines already sent stay as they were, and the
        /// stream continues after them.
        /// </summary>
        /// <returns>Null once done, or why not, in the operator's words.</returns>
        string? EndAtTheToolChange();
    }
}
