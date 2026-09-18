using coppercli.Core.Util;
using System;
using System.Collections.Generic;
using System.Xml;

namespace coppercli.Core.GCode
{
    public class ProbeGrid
    {
        public double?[,] Points { get; private set; }
        public int SizeX { get; private set; }
        public int SizeY { get; private set; }

        public int TotalPoints { get { return SizeX * SizeY; } }

        /// <summary>
        /// Points still to measure in this pass, every operation on them taken under
        /// <see cref="_queueLock"/>. The probing loop removes from it on one thread while a
        /// display reads it on another, so callers get <see cref="SnapshotRemaining"/>
        /// rather than the list.
        /// </summary>
        private readonly List<(int X, int Y)> _remaining = new();
        private readonly object _queueLock = new object();

        public int RemainingCount
        {
            get { lock (_queueLock) { return _remaining.Count; } }
        }

        public int Progress { get { return TotalPoints - RemainingCount; } }

        /// <summary>A copy of the points still to measure, safe to read while probing
        /// continues.</summary>
        public IReadOnlyList<(int X, int Y)> SnapshotRemaining()
        {
            lock (_queueLock) { return _remaining.ToArray(); }
        }

        /// <summary>The next point to measure, or false if this pass is done.</summary>
        public bool TryPeekNext(out (int X, int Y) point)
        {
            lock (_queueLock)
            {
                if (_remaining.Count == 0)
                {
                    point = default;
                    return false;
                }

                point = _remaining[0];
                return true;
            }
        }

        /// <summary>
        /// Orders the remaining points by the given cost, nearest first, so probing
        /// takes a short path. Sorting happens inside the lock - sorting a list another
        /// thread is reading is the same hazard as removing from it.
        /// </summary>
        public void OrderRemainingBy(Func<(int X, int Y), double> cost)
        {
            lock (_queueLock)
            {
                _remaining.Sort((a, b) => cost(a).CompareTo(cost(b)));
            }
        }

        /// <summary>Records a measured height and takes the point off the queue.</summary>
        public void RecordMeasurement(int x, int y, double height)
        {
            AddPoint(x, y, height);
            RemoveFromQueue(x, y);
        }

        /// <summary>
        /// Remove a failed point from this probe pass without recording a height.
        /// The map remains incomplete until that point is measured.
        /// </summary>
        public void SkipPoint(int x, int y)
        {
            RemoveFromQueue(x, y);
        }

        private void RemoveFromQueue(int x, int y)
        {
            lock (_queueLock)
            {
                _remaining.Remove((x, y));
            }
        }

        private void EnqueuePoint(int x, int y)
        {
            lock (_queueLock)
            {
                _remaining.Add((x, y));
            }
        }

        public Vector2 Min { get; private set; }
        public Vector2 Max { get; private set; }

        public Vector2 Delta { get { return Max - Min; } }

        /// <summary>
        /// The lowest and highest measured heights, read from the nodes on every call.
        /// A node can be re-measured at any time, so these must follow the nodes rather than
        /// accumulate.
        /// </summary>
        public double MinHeight => MeasuredExtreme(takeLowest: true);

        /// <inheritdoc cref="MinHeight"/>
        public double MaxHeight => MeasuredExtreme(takeLowest: false);

        private double MeasuredExtreme(bool takeLowest)
        {
            double best = takeLowest ? double.MaxValue : double.MinValue;

            foreach (var (_, _, height) in MeasuredNodes())
            {
                if (takeLowest ? height < best : height > best)
                {
                    best = height;
                }
            }

            return best;
        }

        /// <summary>
        /// The measured nodes with their heights, so callers do not walk the grid.
        /// </summary>
        public IEnumerable<(int X, int Y, double Height)> MeasuredNodes()
        {
            for (int x = 0; x < SizeX; x++)
            {
                for (int y = 0; y < SizeY; y++)
                {
                    double? height = Points[x, y];
                    if (height.HasValue)
                    {
                        yield return (x, y, height.Value);
                    }
                }
            }
        }

        /// <summary>
        /// The setup this map was measured in. Saved and loaded with the map, so whether a
        /// map is usable is decided from the map rather than the session file.
        /// </summary>
        public ProbeContext Context { get; set; } = ProbeContext.Unknown;

        /// <summary>
        /// Whether this map matches the given file and origin. The origin comparison
        /// catches a work zero moved by any route: re-zeroing, another client, or a G10.
        /// </summary>
        public ProbeApplicability GetApplicability(string currentFile, Vector3 currentWorkOrigin)
        {
            if (!Context.IsKnown)
            {
                return ProbeApplicability.Unknown;
            }

            // The origin is unknown, so the comparison below would report a move from a
            // value that only means "not set".
            if (!IsFinite(currentWorkOrigin))
            {
                return ProbeApplicability.Unknown;
            }

            if (string.IsNullOrEmpty(currentFile) || !PathsMatch(Context.SourceFile, currentFile))
            {
                return ProbeApplicability.DifferentFile;
            }

            // Tolerance, not equality: the reported offset is a rounded decimal reading.
            var moved = Context.WorkOrigin - currentWorkOrigin;

            if (Math.Abs(moved.X) > Constants.PositionToleranceMm ||
                Math.Abs(moved.Y) > Constants.PositionToleranceMm)
            {
                return ProbeApplicability.OriginMoved;
            }

            return ProbeApplicability.Applicable;
        }

        private static bool PathsMatch(string a, string b)
        {
            try
            {
                return string.Equals(
                    System.IO.Path.GetFullPath(a),
                    System.IO.Path.GetFullPath(b),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.Ordinal);
            }
        }

        private const string IncompleteGridMessage =
            "Probe grid has unmeasured points - re-probe before applying it to a job.";

        public double GridX { get { return (Max.X - Min.X) / (SizeX - 1); } }
        public double GridY { get { return (Max.Y - Min.Y) / (SizeY - 1); } }

        public ProbeGrid(double gridSize, Vector2 min, Vector2 max)
        {
            if (min.X == max.X || min.Y == max.Y)
            {
                throw new Exception("Probe grid can't be infinitely narrow");
            }

            int pointsX = (int)Math.Ceiling((max.X - min.X) / gridSize) + 1;
            int pointsY = (int)Math.Ceiling((max.Y - min.Y) / gridSize) + 1;

            if (pointsX < 2 || pointsY < 2)
            {
                throw new Exception("Probe grid must have at least 4 points");
            }

            Points = new double?[pointsX, pointsY];

            if (max.X < min.X)
            {
                double a = min.X;
                min.X = max.X;
                max.X = a;
            }

            if (max.Y < min.Y)
            {
                double a = min.Y;
                min.Y = max.Y;
                max.Y = a;
            }

            Min = min;
            Max = max;

            SizeX = pointsX;
            SizeY = pointsY;

            for (int x = 0; x < SizeX; x++)
            {
                for (int y = 0; y < SizeY; y++)
                {
                    EnqueuePoint(x, y);
                }
            }
        }

        /// <summary>
        /// True only when every node holds a measured height. Progress counts points taken
        /// off the queue, a skipped probe included, so Progress reaching TotalPoints does
        /// not mean the map is usable.
        /// </summary>
        public bool HasCompleteData => Points != null && _measuredCount == TotalPoints;

        /// <summary>How many nodes hold a measured height, which is what a screen counting
        /// points shows.</summary>
        public int MeasuredCount => _measuredCount;

        /// <summary>
        /// Whether the map has no, partial, or complete measurements. Screens and
        /// availability checks use this instead of comparing Progress with TotalPoints.
        /// </summary>
        public ProbeDataState State =>
            HasCompleteData ? ProbeDataState.Complete
            : Progress > 0 ? ProbeDataState.Partial
            : ProbeDataState.Ready;

        /// <summary>The same answer where there may be no map: null reads as None.</summary>
        public static ProbeDataState StateOf(ProbeGrid grid) => grid?.State ?? ProbeDataState.None;

        /// <summary>
        /// Create a grid over the file's extent plus the margin, at the given spacing.
        /// </summary>
        public static ProbeGrid ForJob(Vector2 fileMin, Vector2 fileMax, double margin, double gridSize)
        {
            return new ProbeGrid(
                gridSize,
                new Vector2(fileMin.X - margin, fileMin.Y - margin),
                new Vector2(fileMax.X + margin, fileMax.Y + margin));
        }

        /// <summary>
        /// Reject non-finite or unordered extents and fewer than two nodes per axis.
        /// With fewer than two nodes, spacing divides by zero and interpolation uses one height.
        /// </summary>
        private static void ValidateLoadedGridGeometry(ProbeGrid map)
        {
            if (map.SizeX < MinNodesPerAxis || map.SizeY < MinNodesPerAxis
                || map.SizeX > MaxNodesPerAxis || map.SizeY > MaxNodesPerAxis)
            {
                throw new InvalidDataException(MalformedMessage);
            }

            if (!IsFinite(map.Min) || !IsFinite(map.Max)
                || map.Max.X <= map.Min.X || map.Max.Y <= map.Min.Y)
            {
                throw new InvalidDataException(MalformedMessage);
            }
        }

        private static bool IsFinite(Vector3 v) =>
            !double.IsNaN(v.X) && !double.IsInfinity(v.X)
            && !double.IsNaN(v.Y) && !double.IsInfinity(v.Y)
            && !double.IsNaN(v.Z) && !double.IsInfinity(v.Z);

        private static bool IsFinite(Vector2 v) =>
            !double.IsNaN(v.X) && !double.IsInfinity(v.X)
            && !double.IsNaN(v.Y) && !double.IsInfinity(v.Y);

        /// <summary>Two nodes is the fewest an axis can have and still define a spacing.</summary>
        private const int MinNodesPerAxis = 2;

        /// <summary>
        /// Far past any board this machine can reach. It bounds what a loaded file can ask
        /// this process to allocate.
        /// </summary>
        private const int MaxNodesPerAxis = 10000;

        public const string MalformedMessage = "This file is not a usable height map.";

        /// <summary>Nodes holding a measured height. Kept as a count because
        /// InterpolateZ consults it twice per toolpath segment.</summary>
        private int _measuredCount;

        public double InterpolateZ(double x, double y)
        {
            if (!HasCompleteData)
            {
                // Guessing a height here would silently change how deep the cutter runs.
                throw new InvalidOperationException(IncompleteGridMessage);
            }

            // Outside the probed area, the nearest edge is the best estimate the map has:
            // the board continues, and its measured height at the boundary is the closest
            // thing to a reading. Returning the board's highest point instead put a step of
            // the whole warp range at the edge of the grid, where the outermost traces are.
            x = Math.Clamp(x, Min.X, Max.X);
            y = Math.Clamp(y, Min.Y, Max.Y);

            x -= Min.X;
            y -= Min.Y;

            x /= GridX;
            y /= GridY;

            int iLX = (int)Math.Floor(x);   // lower integer part
            int iLY = (int)Math.Floor(y);

            int iHX = (int)Math.Ceiling(x); // upper integer part
            int iHY = (int)Math.Ceiling(y);

            // A point exactly on the far edge can round to one past the last node.
            iLX = Math.Clamp(iLX, 0, SizeX - 1);
            iLY = Math.Clamp(iLY, 0, SizeY - 1);
            iHX = Math.Clamp(iHX, 0, SizeX - 1);
            iHY = Math.Clamp(iHY, 0, SizeY - 1);

            double fX = x - iLX;            // fractional part
            double fY = y - iLY;

            double linUpper = Points[iHX, iHY].Value * fX + Points[iLX, iHY].Value * (1 - fX);
            double linLower = Points[iHX, iLY].Value * fX + Points[iLX, iLY].Value * (1 - fX);

            return linUpper * fY + linLower * (1 - fY);  // bilinear result
        }

        /// <summary>
        /// How far <paramref name="height"/> sits from the mean of the measured nodes one
        /// orthogonal step from (x, y), or null when none of them has been measured yet. A
        /// board is continuous, so those neighbors differ from a sound reading by one step
        /// of warp and no more, and the result is unsigned because a tip stopping short on
        /// debris reads high exactly as one pushing past the surface reads low.
        /// </summary>
        public double? GetNeighborDeviation(int x, int y, double height)
        {
            double sum = 0;
            int count = 0;

            foreach (var (dx, dy) in OrthogonalSteps)
            {
                int nx = x + dx;
                int ny = y + dy;

                if (nx < 0 || nx >= SizeX || ny < 0 || ny >= SizeY)
                {
                    continue;
                }

                double? neighbor = Points[nx, ny];
                if (neighbor.HasValue)
                {
                    sum += neighbor.Value;
                    count++;
                }
            }

            if (count == 0)
            {
                return null;
            }

            return Math.Abs(height - (sum / count));
        }

        private static readonly (int X, int Y)[] OrthogonalSteps =
        {
            (-1, 0), (1, 0), (0, -1), (0, 1)
        };

        public Vector2 GetCoordinates(int x, int y)
        {
            return new Vector2(x * (Delta.X / (SizeX - 1)) + Min.X, y * (Delta.Y / (SizeY - 1)) + Min.Y);
        }

        private ProbeGrid()
        {
        }

        /// <summary>
        /// Refills the work queue with every node that still has no measured height. A probe
        /// that fails with "don't abort on failure" drops its node without recording
        /// anything, so this is how the operator goes back and fills those holes.
        /// </summary>
        public void RequeueUnmeasuredPoints()
        {
            lock (_queueLock)
            {
                _remaining.Clear();

                for (int x = 0; x < SizeX; x++)
                {
                    for (int y = 0; y < SizeY; y++)
                    {
                        if (!Points[x, y].HasValue)
                        {
                            _remaining.Add((x, y));
                        }
                    }
                }
            }
        }

        public void AddPoint(int x, int y, double height)
        {
            if (!Points[x, y].HasValue)
            {
                _measuredCount++;
            }

            Points[x, y] = height;

        }

        private static bool TryParseOrigin(XmlReader r, out Vector3 origin)
        {
            origin = Vector3.MinValue;

            string x = r["OriginX"], y = r["OriginY"], z = r["OriginZ"];

            if (x == null || y == null || z == null)
            {
                return false;
            }

            try
            {
                var parsed = new Vector3(
                    double.Parse(x, Constants.DecimalParseFormat),
                    double.Parse(y, Constants.DecimalParseFormat),
                    double.Parse(z, Constants.DecimalParseFormat));

                // A non-finite origin compares false against every tolerance, which would
                // let a file declare itself applicable to any job.
                if (!IsFinite(parsed))
                {
                    return false;
                }

                origin = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static ProbeGrid Load(string path)
        {
            ProbeGrid map = new ProbeGrid();

            // Opened as a file rather than by name: XmlReader.Create(string) resolves its
            // argument as a URI, and the sharing flags let the autosave be rewritten or
            // deleted while a status read has it open.
            using var file = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = XmlReader.Create(file);

            while (r.Read())
            {
                if (!r.IsStartElement())
                    continue;

                switch (r.Name)
                {
                    case "heightmap":
                        map.Min = new Vector2(double.Parse(r["MinX"], Constants.DecimalParseFormat), double.Parse(r["MinY"], Constants.DecimalParseFormat));
                        map.Max = new Vector2(double.Parse(r["MaxX"], Constants.DecimalParseFormat), double.Parse(r["MaxY"], Constants.DecimalParseFormat));
                        map.SizeX = int.Parse(r["SizeX"]);
                        map.SizeY = int.Parse(r["SizeY"]);

                        // Apply constructor geometry checks to loaded files, whose heights
                        // set the commanded Z of cutting moves.
                        ValidateLoadedGridGeometry(map);

                        map.Points = new double?[map.SizeX, map.SizeY];

                        // Reset with the array it counts: a second heightmap element
                        // would otherwise leave the count describing a discarded array.
                        map._measuredCount = 0;

                        // Absent in maps written before the setup was recorded; those
                        // stay Unknown and are checked rather than assumed usable.
                        string sourceFile = r["SourceFile"];

                        if (!string.IsNullOrEmpty(sourceFile))
                        {
                            // A map that names a source file must contain a readable origin.
                            // Otherwise it becomes Unknown and passes availability checks.
                            if (!TryParseOrigin(r, out Vector3 origin))
                            {
                                throw new InvalidDataException(MalformedMessage);
                            }

                            map.Context = new ProbeContext(sourceFile, origin);
                        }
                        break;
                    case "point":
                        if (map.Points == null)
                        {
                            throw new InvalidDataException(MalformedMessage);
                        }

                        int x = int.Parse(r["X"]);
                        int y = int.Parse(r["Y"]);

                        if (x < 0 || x >= map.SizeX || y < 0 || y >= map.SizeY)
                        {
                            throw new InvalidDataException(MalformedMessage);
                        }

                        double height = double.Parse(r.ReadInnerXml(), Constants.DecimalParseFormat);

                        // A height that is not a number would reach InterpolateZ and from
                        // there the commanded Z of every cutting move.
                        if (double.IsNaN(height) || double.IsInfinity(height))
                        {
                            throw new InvalidDataException(MalformedMessage);
                        }

                        if (!map.Points[x, y].HasValue)
                        {
                            map._measuredCount++;
                        }

                        map.Points[x, y] = height;

                        break;
                }
            }

            for (int x = 0; x < map.SizeX; x++)
            {
                for (int y = 0; y < map.SizeY; y++)
                {
                    if (!map.Points[x, y].HasValue)
                    {
                        map.EnqueuePoint(x, y);
                    }
                }
            }

            return map;
        }

        public void Save(string path)
        {
            XmlWriterSettings set = new XmlWriterSettings();
            set.Indent = true;
            AtomicFile.Write(path, writePath =>
            {
                using XmlWriter w = XmlWriter.Create(writePath, set);
                w.WriteStartDocument();
                w.WriteStartElement("heightmap");
                w.WriteAttributeString("MinX", Min.X.ToString(Constants.DecimalParseFormat));
                w.WriteAttributeString("MinY", Min.Y.ToString(Constants.DecimalParseFormat));
                w.WriteAttributeString("MaxX", Max.X.ToString(Constants.DecimalParseFormat));
                w.WriteAttributeString("MaxY", Max.Y.ToString(Constants.DecimalParseFormat));
                w.WriteAttributeString("SizeX", SizeX.ToString(Constants.DecimalParseFormat));
                w.WriteAttributeString("SizeY", SizeY.ToString(Constants.DecimalParseFormat));

                // The setup this map describes. Without it a map cannot be distinguished
                // from one measured on another board, or before the origin moved.
                if (Context.IsKnown)
                {
                    w.WriteAttributeString("SourceFile", Context.SourceFile);
                    w.WriteAttributeString("OriginX", Context.WorkOrigin.X.ToString(Constants.DecimalParseFormat));
                    w.WriteAttributeString("OriginY", Context.WorkOrigin.Y.ToString(Constants.DecimalParseFormat));
                    w.WriteAttributeString("OriginZ", Context.WorkOrigin.Z.ToString(Constants.DecimalParseFormat));
                }

                for (int x = 0; x < SizeX; x++)
                {
                    for (int y = 0; y < SizeY; y++)
                    {
                        if (!Points[x, y].HasValue)
                        {
                            continue;
                        }

                        w.WriteStartElement("point");
                        w.WriteAttributeString("X", x.ToString());
                        w.WriteAttributeString("Y", y.ToString());
                        w.WriteString(Points[x, y].Value.ToString(Constants.DecimalParseFormat));
                        w.WriteEndElement();
                    }
                }

                w.WriteEndElement();
            });
        }

        /// <summary>
        /// Whether any node holds a height, read from the count the grid already keeps so it
        /// cannot disagree with MinHeight and MaxHeight. Not Progress, which counts points
        /// taken off the queue and so includes skipped probes.
        /// </summary>
        public bool HasValidHeights => _measuredCount > 0;

        public string GetInfo()
        {
            string zRange = HasValidHeights
                ? $"Z range: {MinHeight:F3} to {MaxHeight:F3}"
                : "Z range: --";

            int pct = TotalPoints > 0 ? (int)Math.Round(100.0 * Progress / TotalPoints) : 0;
            string progressText = HasCompleteData
                ? $"Progress: {Progress}/{TotalPoints} (complete)"
                : $"Progress: {Progress}/{TotalPoints} ({pct}%)";

            return $"Probe Grid: {SizeX}x{SizeY} points\n" +
                   $"Area: X[{Min.X:F3} to {Max.X:F3}] Y[{Min.Y:F3} to {Max.Y:F3}]\n" +
                   $"Grid: {GridX:F3} x {GridY:F3}\n" +
                   $"{progressText}\n" +
                   zRange;
        }
    }
}
