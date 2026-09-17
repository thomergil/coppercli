using coppercli.Core.Communication;
using static coppercli.CliConstants;

namespace coppercli.Helpers
{
    internal static class JogHelpers
    {
        /// <param name="blockXY">Set by callers while the probe is in contact.</param>
        public static bool HandleJogKey(ConsoleKeyInfo key, Machine machine, double feed, double distance, bool blockXY = false)
        {
            bool jogged = false;

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    if (!blockXY) { machine.Jog('Y', distance, feed); jogged = true; }
                    break;
                case ConsoleKey.DownArrow:
                    if (!blockXY) { machine.Jog('Y', -distance, feed); jogged = true; }
                    break;
                case ConsoleKey.LeftArrow:
                    if (!blockXY) { machine.Jog('X', -distance, feed); jogged = true; }
                    break;
                case ConsoleKey.RightArrow:
                    if (!blockXY) { machine.Jog('X', distance, feed); jogged = true; }
                    break;
                case ConsoleKey.PageUp: machine.Jog('Z', distance, feed); jogged = true; break;
                case ConsoleKey.PageDown: machine.Jog('Z', -distance, feed); jogged = true; break;
            }

            if (!jogged && InputHelpers.IsKey(key, ConsoleKey.Q))
            {
                machine.Jog('Z', distance, feed);
                jogged = true;
            }
            if (!jogged && InputHelpers.IsKey(key, ConsoleKey.Z))
            {
                machine.Jog('Z', -distance, feed);
                jogged = true;
            }

            if (!jogged && !blockXY && InputHelpers.IsKey(key, ConsoleKey.A))
            {
                machine.Jog('X', -distance, feed);
                jogged = true;
            }
            if (!jogged && !blockXY && InputHelpers.IsKey(key, ConsoleKey.D))
            {
                machine.Jog('X', distance, feed);
                jogged = true;
            }
            if (!jogged && !blockXY && InputHelpers.IsKey(key, ConsoleKey.X))
            {
                machine.Jog('Y', -distance, feed);
                jogged = true;
            }
            if (!jogged && !blockXY && InputHelpers.IsKey(key, ConsoleKey.W))
            {
                machine.Jog('Y', distance, feed);
                jogged = true;
            }

            if (jogged)
            {
                Thread.Sleep(JogPollIntervalMs);
                InputHelpers.FlushKeyboard();
            }

            return jogged;
        }
    }
}
