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

        public int Progress { get { return TotalPoints - NotProbed.Count; } }
        public int TotalPoints { get { return SizeX * SizeY; } }

        public List<Tuple<int, int>> NotProbed { get; private set; } = new List<Tuple<int, int>>();

        public Vector2 Min { get; private set; }
        public Vector2 Max { get; private set; }

        public Vector2 Delta { get { return Max - Min; } }

        public double MinHeight { get; private set; } = double.MaxValue;
        public double MaxHeight { get; private set; } = double.MinValue;

        public event Action MapUpdated;

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
                    NotProbed.Add(new Tuple<int, int>(x, y));
            }
        }

        public double InterpolateZ(double x, double y)
        {
            if (x > Max.X || x < Min.X || y > Max.Y || y < Min.Y)
            {
                return MaxHeight;
            }

            x -= Min.X;
            y -= Min.Y;

            x /= GridX;
            y /= GridY;

            int iLX = (int)Math.Floor(x);   // lower integer part
            int iLY = (int)Math.Floor(y);

            int iHX = (int)Math.Ceiling(x); // upper integer part
            int iHY = (int)Math.Ceiling(y);

            double fX = x - iLX;            // fractional part
            double fY = y - iLY;

            double linUpper = Points[iHX, iHY].Value * fX + Points[iLX, iHY].Value * (1 - fX);  // linear intermediates
            double linLower = Points[iHX, iLY].Value * fX + Points[iLX, iLY].Value * (1 - fX);

            return linUpper * fY + linLower * (1 - fY);  // bilinear result
        }

        public Vector2 GetCoordinates(int x, int y)
        {
            return new Vector2(x * (Delta.X / (SizeX - 1)) + Min.X, y * (Delta.Y / (SizeY - 1)) + Min.Y);
        }

        public Vector2 GetCoordinates(Tuple<int, int> index)
        {
            return GetCoordinates(index.Item1, index.Item2);
        }

        private ProbeGrid()
        {
        }

        public void AddPoint(int x, int y, double height)
        {
            Points[x, y] = height;

            if (height > MaxHeight)
            {
                MaxHeight = height;
            }
            if (height < MinHeight)
            {
                MinHeight = height;
            }

            MapUpdated?.Invoke();
        }

        public static ProbeGrid Load(string path)
        {
            ProbeGrid map = new ProbeGrid();

            XmlReader r = XmlReader.Create(path);

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
                        map.Points = new double?[map.SizeX, map.SizeY];
                        break;
                    case "point":
                        int x = int.Parse(r["X"]);
                        int y = int.Parse(r["Y"]);
                        double height = double.Parse(r.ReadInnerXml(), Constants.DecimalParseFormat);

                        map.Points[x, y] = height;

                        if (height > map.MaxHeight)
                        {
                            map.MaxHeight = height;
                        }
                        if (height < map.MinHeight)
                        {
                            map.MinHeight = height;
                        }

                        break;
                }
            }

            r.Dispose();

            for (int x = 0; x < map.SizeX; x++)
            {
                for (int y = 0; y < map.SizeY; y++)
                {
                    if (!map.Points[x, y].HasValue)
                    {
                        map.NotProbed.Add(new Tuple<int, int>(x, y));
                    }
                }
            }

            return map;
        }

        public void Save(string path)
        {
            XmlWriterSettings set = new XmlWriterSettings();
            set.Indent = true;
            XmlWriter w = XmlWriter.Create(path, set);
            w.WriteStartDocument();
            w.WriteStartElement("heightmap");
            w.WriteAttributeString("MinX", Min.X.ToString(Constants.DecimalParseFormat));
            w.WriteAttributeString("MinY", Min.Y.ToString(Constants.DecimalParseFormat));
            w.WriteAttributeString("MaxX", Max.X.ToString(Constants.DecimalParseFormat));
            w.WriteAttributeString("MaxY", Max.Y.ToString(Constants.DecimalParseFormat));
            w.WriteAttributeString("SizeX", SizeX.ToString(Constants.DecimalParseFormat));
            w.WriteAttributeString("SizeY", SizeY.ToString(Constants.DecimalParseFormat));

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
            w.Close();
        }

        /// <summary>
        /// Check if any points have valid height data.
        /// </summary>
        public bool HasValidHeights => Progress > 0 && MinHeight != double.MaxValue && MaxHeight != double.MinValue;

        /// <summary>
        /// Get information about the probe grid as a string.
        /// </summary>
        public string GetInfo()
        {
            string zRange = HasValidHeights
                ? $"Z range: {MinHeight:F3} to {MaxHeight:F3}"
                : "Z range: --";

            int pct = TotalPoints > 0 ? (int)Math.Round(100.0 * Progress / TotalPoints) : 0;
            string progressText = Progress == TotalPoints
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
