using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace DOMWallDetector
{
    public class DOMWallDetector : Indicator
    {
        #region Input Parameters

        [InputParameter("Wall Threshold Multiplier", 10, 1.5, 50.0, 0.5, 1)]
        public double WallMultiplier = 3.0;

        [InputParameter("Levels Count", 20, 5, 200, 1, 0)]
        public int LevelsCount = 50;

        [InputParameter("Min Wall Size (0=auto)", 30, 0, 999999999, 1, 0)]
        public double MinWallSize = 0;

        [InputParameter("Line Opacity (0-255)", 40, 10, 255, 1, 0)]
        public int LineOpacity = 180;

        [InputParameter("Show Size Labels", 50, variants: new object[] {
            "Yes", true,
            "No", false
        })]
        public bool ShowSizeLabels = true;

        [InputParameter("Ask Wall Color", 60)]
        public Color AskWallColor = Color.FromArgb(255, 220, 50, 50);

        [InputParameter("Bid Wall Color", 70)]
        public Color BidWallColor = Color.FromArgb(255, 50, 180, 50);

        [InputParameter("Line Width", 80, 1, 10, 1, 0)]
        public int WallLineWidth = 2;

        [InputParameter("Line Style", 90, variants: new object[] {
            "Solid", 0,
            "Dash", 1,
            "Dot", 2,
            "DashDot", 3
        })]
        public int WallLineStyle = 1;

        [InputParameter("Show Background Fill", 100, variants: new object[] {
            "Yes", true,
            "No", false
        })]
        public bool ShowBackground = true;

        [InputParameter("Background Opacity", 110, 5, 100, 1, 0)]
        public int BgOpacity = 25;

        [InputParameter("Max Walls to Display", 120, 1, 50, 1, 0)]
        public int MaxWalls = 10;

        #endregion

        private readonly object _lock = new object();
        private List<WallInfo> _currentWalls = new List<WallInfo>();

        public DOMWallDetector()
            : base()
        {
            Name = "DOM Wall Detector";
            Description = "Detects large order walls in DOM and displays them on the chart";
            AddLineSeries("Placeholder", Color.Transparent, 1, LineStyle.Solid);
            SeparateWindow = false;
            OnBackGround = true;
        }

        protected override void OnInit()
        {
            this.Symbol.NewLevel2 += OnNewLevel2;
        }

        private void OnNewLevel2(Symbol symbol, Level2Quote level2, DOMQuote dom)
        {
            ScanForWalls();
            this.CurrentChart?.RedrawBuffer();
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (args.Reason == UpdateReason.HistoricalBar)
                return;

            ScanForWalls();
            this.CurrentChart?.RedrawBuffer();
        }

        private void ScanForWalls()
        {
            try
            {
                var domData = this.Symbol.DepthOfMarket.GetDepthOfMarketAggregatedCollections(
                    new GetLevel2ItemsParameters()
                    {
                        AggregateMethod = AggregateMethod.ByPriceLVL,
                        LevelsCount = this.LevelsCount,
                        CalculateCumulative = false
                    });

                if (domData == null)
                    return;

                var allLevels = new List<Level2Item>();
                if (domData.Asks != null) allLevels.AddRange(domData.Asks);
                if (domData.Bids != null) allLevels.AddRange(domData.Bids);

                if (allLevels.Count == 0)
                    return;

                double avgSize = allLevels.Average(l => l.Size);
                double threshold = MinWallSize > 0
                    ? MinWallSize
                    : avgSize * WallMultiplier;

                var walls = new List<WallInfo>();

                if (domData.Asks != null)
                {
                    foreach (var ask in domData.Asks)
                    {
                        if (ask.Size >= threshold)
                        {
                            walls.Add(new WallInfo
                            {
                                Price = ask.Price,
                                Size = ask.Size,
                                Side = WallSide.Ask,
                                Strength = ask.Size / avgSize
                            });
                        }
                    }
                }

                if (domData.Bids != null)
                {
                    foreach (var bid in domData.Bids)
                    {
                        if (bid.Size >= threshold)
                        {
                            walls.Add(new WallInfo
                            {
                                Price = bid.Price,
                                Size = bid.Size,
                                Side = WallSide.Bid,
                                Strength = bid.Size / avgSize
                            });
                        }
                    }
                }

                walls = walls
                    .OrderByDescending(w => w.Size)
                    .Take(MaxWalls)
                    .ToList();

                lock (_lock)
                {
                    _currentWalls = walls;
                }
            }
            catch
            {
                // Silently handle any data access errors during initialization
            }
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            if (this.CurrentChart == null)
                return;

            var mainWindow = this.CurrentChart.MainWindow;
            if (mainWindow == null)
                return;

            var converter = mainWindow.CoordinatesConverter;
            if (converter == null)
                return;

            Graphics gr = args.Graphics;
            Rectangle chartRect = args.Rectangle;
            gr.SetClip(chartRect);

            List<WallInfo> walls;
            lock (_lock)
            {
                walls = new List<WallInfo>(_currentWalls);
            }

            if (walls.Count == 0)
                return;

            double maxStrength = walls.Max(w => w.Strength);

            foreach (var wall in walls)
            {
                int y = (int)converter.GetChartY(wall.Price);

                if (y < chartRect.Top || y > chartRect.Bottom)
                    continue;

                Color baseColor = wall.Side == WallSide.Ask ? AskWallColor : BidWallColor;
                Color lineColor = Color.FromArgb(LineOpacity, baseColor);

                DashStyle dashStyle = WallLineStyle switch
                {
                    1 => DashStyle.Dash,
                    2 => DashStyle.Dot,
                    3 => DashStyle.DashDot,
                    _ => DashStyle.Solid
                };

                float dynamicWidth = WallLineWidth * (float)Math.Min(wall.Strength / maxStrength + 0.5, 2.0);

                using (var pen = new Pen(lineColor, dynamicWidth))
                {
                    pen.DashStyle = dashStyle;
                    gr.DrawLine(pen, chartRect.Left, y, chartRect.Right, y);
                }

                if (ShowBackground)
                {
                    int bgAlpha = (int)(BgOpacity * Math.Min(wall.Strength / maxStrength, 1.0));
                    Color bgColor = Color.FromArgb(bgAlpha, baseColor);
                    int bandHeight = Math.Max(4, (int)(6 * (wall.Strength / maxStrength)));

                    using (var brush = new SolidBrush(bgColor))
                    {
                        gr.FillRectangle(brush, chartRect.Left, y - bandHeight / 2,
                            chartRect.Width, bandHeight);
                    }
                }

                if (ShowSizeLabels)
                {
                    string label = $"{FormatSize(wall.Size)} @ {this.FormatPrice(wall.Price)}";
                    string strengthLabel = $"({wall.Strength:F1}x)";
                    string fullLabel = $"{label} {strengthLabel}";

                    using (var font = new Font("Segoe UI", 8f, FontStyle.Bold))
                    using (var bgBrush = new SolidBrush(Color.FromArgb(200, 20, 20, 20)))
                    using (var textBrush = new SolidBrush(Color.FromArgb(230, baseColor)))
                    {
                        SizeF textSize = gr.MeasureString(fullLabel, font);

                        float labelX = chartRect.Right - textSize.Width - 10;
                        float labelY = y - textSize.Height - 2;

                        if (labelY < chartRect.Top)
                            labelY = y + 2;

                        gr.FillRectangle(bgBrush, labelX - 3, labelY - 1,
                            textSize.Width + 6, textSize.Height + 2);

                        gr.DrawString(fullLabel, font, textBrush, labelX, labelY);
                    }
                }
            }

            gr.ResetClip();
        }

        private static string FormatSize(double size)
        {
            if (size >= 1_000_000)
                return $"{size / 1_000_000:F2}M";
            if (size >= 1_000)
                return $"{size / 1_000:F1}K";
            return size.ToString("F0");
        }

        protected override void OnClear()
        {
            this.Symbol.NewLevel2 -= OnNewLevel2;

            lock (_lock)
            {
                _currentWalls.Clear();
            }
        }

        private class WallInfo
        {
            public double Price { get; set; }
            public double Size { get; set; }
            public WallSide Side { get; set; }
            public double Strength { get; set; }
        }

        private enum WallSide
        {
            Ask,
            Bid
        }
    }
}
