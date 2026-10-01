#nullable enable
using System;

namespace coppercli.Core.Controllers
{
    /// <summary>
    /// How far a milling run is through its G-code, as a share of the time its moves take, and
    /// the time its moves have left at GRBL's overrides. Time the operator spends at a tool
    /// change or a pause is in neither.
    /// </summary>
    public sealed record JobEstimate(double FractionDone, TimeSpan TimeLeft);
}
