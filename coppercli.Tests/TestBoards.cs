#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using coppercli;
using coppercli.Core.GCode;
using Xunit;

namespace coppercli.Tests
{
    /// <summary>Writes board files for a test, deletes them when it ends, and loads one as the operator does.</summary>
    internal sealed class TestBoards : IDisposable
    {
        private const string FilePrefix = "coppercli-board-";
        private const string FileExtension = ".ngc";

        private readonly List<string> _paths = new();

        /// <returns>The path of a new file holding <paramref name="lines"/>.</returns>
        public string Write(string[] lines)
        {
            string path = Path.Combine(Path.GetTempPath(), FilePrefix + Guid.NewGuid().ToString("N") + FileExtension);
            File.WriteAllLines(path, lines);
            _paths.Add(path);
            return path;
        }

        /// <summary>Writes <paramref name="lines"/> to a file and loads it into the machine, with work zero set.</summary>
        public GCodeFile Load(string[] lines)
        {
            AppState.MarkWorkZeroSet();
            var loaded = GCodeFile.Load(Write(lines));
            Assert.Null(AppState.LoadGCodeIntoMachine(loaded).Refused);
            return loaded;
        }

        public void Dispose()
        {
            foreach (string path in _paths)
            {
                File.Delete(path);
            }
        }
    }
}
