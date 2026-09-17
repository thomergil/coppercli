namespace coppercli.Core.Controllers
{
    public record ProgressInfo(
        string Phase,

        /// <summary>0 to 100, not 0 to 1.</summary>
        float Percentage,

        string Message,

        int? CurrentStep = null,

        int? TotalSteps = null
    );
}
