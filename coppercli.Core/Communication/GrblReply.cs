#nullable enable

namespace coppercli.Core.Communication
{
    /// <summary>What GRBL did with a line it was sent.</summary>
    public enum GrblAnswer
    {
        /// <summary>
        /// GRBL accepted the line. A motion line is then planned, not finished; a line GRBL
        /// runs before it answers - $H, a G10 that writes EEPROM - is finished.
        /// </summary>
        Ok,

        /// <summary>GRBL answered with an error and did not run the line.</summary>
        Refused,

        /// <summary>
        /// GRBL alarmed or reset before answering. Whether the line ran is not knowable.
        /// </summary>
        Abandoned,

        /// <summary>
        /// Nothing came back in time. A line still waiting to go out was withdrawn; one
        /// already sent may still run.
        /// </summary>
        NoAnswer,

        /// <summary>The line never left coppercli: not connected, a file streaming, or a
        /// character that may not be sent.</summary>
        NotSent
    }

    /// <summary>
    /// GRBL's answer to one line, the only evidence of whether it ran. See
    /// <see cref="IMachine.SendAsync"/> for why a status report cannot show it.
    /// </summary>
    public readonly record struct GrblReply
    {
        /// <summary>Private, so a reply is built only by the members below and Refused
        /// always carries the rejection and nothing else does.</summary>
        private GrblReply(GrblAnswer answer, GrblRejection? rejection)
        {
            Answer = answer;
            Rejection = rejection;
        }

        public GrblAnswer Answer { get; }

        /// <summary>Why GRBL refused the line; set only when <see cref="Answer"/> is Refused.</summary>
        public GrblRejection? Rejection { get; }

        public bool Ran => Answer == GrblAnswer.Ok;

        public static readonly GrblReply Ok = new(GrblAnswer.Ok, null);

        public static readonly GrblReply Abandoned = new(GrblAnswer.Abandoned, null);

        public static readonly GrblReply NoAnswer = new(GrblAnswer.NoAnswer, null);

        public static readonly GrblReply NotSent = new(GrblAnswer.NotSent, null);

        public static GrblReply Refused(GrblRejection rejection) => new(GrblAnswer.Refused, rejection);
    }
}
