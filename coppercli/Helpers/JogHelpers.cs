using coppercli.Core.Communication;
using static coppercli.CliConstants;

namespace coppercli.Helpers
{
    /// <summary>
    /// Helper methods for jogging operations.
    /// </summary>
    internal static class JogHelpers
    {
        /// <summary>
        /// Handle jog keys (arrows, vi-style HJKL, W/S for Z).
        /// Returns true if a jog command was sent.
        /// </summary>
        public static bool HandleJogKey(ConsoleKeyInfo key, Machine machine, double feed, double distance)
        {
            bool jogged = false;

            // Arrow keys
            switch (key.Key)
            {
                case ConsoleKey.UpArrow: machine.Jog('Y', distance, feed); jogged = true; break;
                case ConsoleKey.DownArrow: machine.Jog('Y', -distance, feed); jogged = true; break;
                case ConsoleKey.LeftArrow: machine.Jog('X', -distance, feed); jogged = true; break;
                case ConsoleKey.RightArrow: machine.Jog('X', distance, feed); jogged = true; break;
                case ConsoleKey.PageUp: machine.Jog('Z', distance, feed); jogged = true; break;
                case ConsoleKey.PageDown: machine.Jog('Z', -distance, feed); jogged = true; break;
            }

            // W/S for Z jog
            if (!jogged && InputHelpers.IsKey(key, ConsoleKey.W, 'w'))
            {
                machine.Jog('Z', distance, feed);
                jogged = true;
            }
            if (!jogged && InputHelpers.IsKey(key, ConsoleKey.S, 's'))
            {
                machine.Jog('Z', -distance, feed);
                jogged = true;
            }

            // Vi-style HJKL for X/Y jog
            if (!jogged && InputHelpers.IsKey(key, ConsoleKey.H, 'h'))
            {
                machine.Jog('X', -distance, feed);
                jogged = true;
            }
            if (!jogged && InputHelpers.IsKey(key, ConsoleKey.L, 'l'))
            {
                machine.Jog('X', distance, feed);
                jogged = true;
            }
            if (!jogged && InputHelpers.IsKey(key, ConsoleKey.J, 'j'))
            {
                machine.Jog('Y', -distance, feed);
                jogged = true;
            }
            if (!jogged && InputHelpers.IsKey(key, ConsoleKey.K, 'k'))
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
