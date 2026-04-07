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

        [InputParameter("Max Walls to Display", 40, 1, 50, 1, 0)]
        public int MaxWalls = 10;

        [InputParameter("Delta Range (ticks from wall)", 50, 1, 50, 1, 0)]
        public int DeltaRangeTicks = 3;

        [InputParameter("Spoof Time Threshold (sec)", 60, 1, 60, 1, 0)]
        public int SpoofTimeSeconds = 5;

        [InputParameter("Spoof Flicker Count", 70, 2, 20, 1, 0)]
        public int SpoofFlickerThreshold = 3;

        [InputParameter("Ask Wall Color", 80)]
        public Color AskWallColor = Color.FromArgb(255, 220, 50, 50);

        [InputParameter("Bid Wall Color", 90)]
        public Color BidWallColor = Color.FromArgb(255, 50, 180, 50);

        [InputParameter("Spoof Warning Color", 100)]
        public Color SpoofColor = Color.FromArgb(255, 255, 165, 0);

        [InputParameter("Line Width", 110, 1, 10, 1, 0)]
        public int WallLineWidth = 2;

        [InputParameter("Show Delta Bars", 120, variants: new object[] { "Yes", true, "No", false })]
        public bool ShowDeltaBars = true;

        [InputParameter("Show Absorption Meter", 130, variants: new object[] { "Yes", true, "No", false })]
        public bool ShowAbsorption = true;

        [InputParameter("Show Spoof Warnings", 140, variants: new object[] { "Yes", true, "No", false })]
        public bool ShowSpoofWarnings = true;

        #endregion

        private readonly object _lock = new object();
        private Dictionary<long, TrackedWall> _wallsByPrice = new Dictionary<long, TrackedWall>();
        private List<TrackedWall> _displayWalls = new List<TrackedWall>();
        private double _tickSize = 0.0001;
        private DateTime _lastCleanup = DateTime.MinValue;

        public DOMWallDetector() : base()
        {
            Name = "DOM Wall Detector V2";
            Description = "Detects DOM walls with delta, absorption & spoof detection";
            AddLineSeries("Placeholder", Color.Transparent, 1, LineStyle.Solid);
            SeparateWindow = false;
            OnBackGround = true;
        }

        protected override void OnInit()
        {
            _tickSize = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.0001;
            this.Symbol.NewLevel2 += OnNewLevel2;
            this.Symbol.NewLast += OnNewLast;
        }

        private void OnNewLevel2(Symbol symbol, Level2Quote level2, DOMQuote dom)
        {
            UpdateWalls();
            this.CurrentChart?.RedrawBuffer();
        }

        private void OnNewLast(Symbol symbol, Last last)
        {
            ProcessTrade(last);
        }

        private void ProcessTrade(Last last)
        {
            if (last == null || last.Size <= 0)
                return;

            bool isBuy = last.AggressorFlag == AggressorFlag.Buy;
            bool isSell = last.AggressorFlag == AggressorFlag.Sell;

            if (!isBuy && !isSell)
            {
                isBuy = last.Price >= this.Symbol.Ask;
                isSell = last.Price <= this.Symbol.Bid;
            }

            if (!isBuy && !isSell)
                return;

            double deltaRange = DeltaRangeTicks * _tickSize;

            lock (_lock)
            {
                foreach (var kvp in _wallsByPrice)
                {
                    var wall = kvp.Value;
                    if (!wall.IsVisible)
                        continue;

                    if (Math.Abs(last.Price - wall.Price) <= deltaRange)
                    {
                        if (isBuy)
                            wall.BuyVolume += last.Size;
                        else
                            wall.SellVolume += last.Size;

                        wall.LastTradeTime = DateTime.UtcNow;
                    }
                }
            }
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            if (args.Reason == UpdateReason.HistoricalBar)
                return;

            UpdateWalls();

            if ((DateTime.UtcNow - _lastCleanup).TotalSeconds > 30)
            {
                CleanupStaleWalls();
                _lastCleanup = DateTime.UtcNow;
            }

            this.CurrentChart?.RedrawBuffer();
        }

        private void UpdateWalls()
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
                double threshold = MinWallSize > 0 ? MinWallSize : avgSize * WallMultiplier;
                var now = DateTime.UtcNow;
                var currentPriceKeys = new HashSet<long>();

                lock (_lock)
                {
                    if (domData.Asks != null)
                        ProcessDomSide(domData.Asks, WallSide.Ask, threshold, avgSize, now, currentPriceKeys);
                    if (domData.Bids != null)
                        ProcessDomSide(domData.Bids, WallSide.Bid, threshold, avgSize, now, currentPriceKeys);

                    foreach (var kvp in _wallsByPrice)
                    {
                        var wall = kvp.Value;
                        if (wall.IsVisible && !currentPriceKeys.Contains(kvp.Key))
                        {
                            wall.IsVisible = false;
                            wall.DisappearTime = now;
                            wall.FlickerCount++;

                            double priceDist = Math.Abs(this.Symbol.Last - wall.Price);
                            double tickDist = priceDist / _tickSize;
                            if (tickDist < 10)
                                wall.PulledNearPrice = true;
                        }

                        UpdateSpoofScore(wall, now);
                        UpdateWallStatus(wall);
                    }

                    _displayWalls = _wallsByPrice.Values
                        .Where(w => w.IsVisible || (now - w.DisappearTime).TotalSeconds < 10)
                        .OrderByDescending(w => w.CurrentSize)
                        .Take(MaxWalls)
                        .ToList();
                }
            }
            catch { }
        }

        private void ProcessDomSide(Level2Item[] items, WallSide side, double threshold,
            double avgSize, DateTime now, HashSet<long> currentKeys)
        {
            foreach (var item in items)
            {
                if (item.Size < threshold)
                    continue;

                long priceKey = (long)Math.Round(item.Price / _tickSize);
                currentKeys.Add(priceKey);

                if (_wallsByPrice.TryGetValue(priceKey, out var existing))
                {
                    if (!existing.IsVisible)
                    {
                        existing.FlickerCount++;
                        existing.IsVisible = true;
                    }

                    double sizeDelta = item.Size - existing.CurrentSize;
                    if (sizeDelta < -existing.PeakSize * 0.1 && existing.LastTradeTime > existing.LastSizeChange)
                        existing.AbsorbedVolume += Math.Abs(sizeDelta);

                    existing.CurrentSize = item.Size;
                    existing.PeakSize = Math.Max(existing.PeakSize, item.Size);
                    existing.LastSeen = now;
                    existing.LastSizeChange = now;
                    existing.Strength = item.Size / avgSize;
                }
                else
                {
                    _wallsByPrice[priceKey] = new TrackedWall
                    {
                        Price = item.Price,
                        InitialSize = item.Size,
                        CurrentSize = item.Size,
                        PeakSize = item.Size,
                        Side = side,
                        FirstSeen = now,
                        LastSeen = now,
                        LastSizeChange = now,
                        IsVisible = true,
                        Strength = item.Size / avgSize,
                    };
                }
            }
        }

        private void UpdateSpoofScore(TrackedWall wall, DateTime now)
        {
            double score = 0;

            double durationSec = (wall.LastSeen - wall.FirstSeen).TotalSeconds;
            if (durationSec < 3) score += 35;
            else if (durationSec < SpoofTimeSeconds) score += 25;
            else if (durationSec < 15) score += 10;

            if (wall.FlickerCount >= SpoofFlickerThreshold * 2) score += 30;
            else if (wall.FlickerCount >= SpoofFlickerThreshold) score += 20;
            else if (wall.FlickerCount >= 2) score += 10;

            if (wall.PulledNearPrice) score += 25;

            if (wall.AbsorptionPct < 5 && !wall.IsVisible && durationSec < 30)
                score += 10;

            wall.SpoofScore = Math.Min(100, score);
        }

        private void UpdateWallStatus(TrackedWall wall)
        {
            if (wall.SpoofScore >= 60)
            {
                wall.Status = WallStatus.Spoofed;
                return;
            }

            if (!wall.IsVisible)
            {
                wall.Status = wall.AbsorptionPct > 50 ? WallStatus.Broken : WallStatus.Removed;
                return;
            }

            double aggressiveDelta = wall.Side == WallSide.Ask ? wall.BuyVolume : wall.SellVolume;

            if (wall.AbsorptionPct > 60)
                wall.Status = WallStatus.Breaking;
            else if (wall.AbsorptionPct > 20 || aggressiveDelta > wall.InitialSize * 0.3)
                wall.Status = WallStatus.Absorbing;
            else if ((DateTime.UtcNow - wall.FirstSeen).TotalSeconds > 10)
                wall.Status = WallStatus.Holding;
            else
                wall.Status = WallStatus.New;
        }

        private void CleanupStaleWalls()
        {
            var now = DateTime.UtcNow;
            var toRemove = new List<long>();

            foreach (var kvp in _wallsByPrice)
            {
                if (!kvp.Value.IsVisible && (now - kvp.Value.DisappearTime).TotalSeconds > 120)
                    toRemove.Add(kvp.Key);
            }

            foreach (var key in toRemove)
                _wallsByPrice.Remove(key);
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            if (this.CurrentChart == null) return;
            var mainWindow = this.CurrentChart.MainWindow;
            if (mainWindow == null) return;
            var converter = mainWindow.CoordinatesConverter;
            if (converter == null) return;

            Graphics gr = args.Graphics;
            Rectangle chartRect = args.Rectangle;
            gr.SetClip(chartRect);

            List<TrackedWall> walls;
            lock (_lock) { walls = new List<TrackedWall>(_displayWalls); }

            if (walls.Count == 0) { gr.ResetClip(); return; }

            foreach (var wall in walls)
            {
                int y = (int)converter.GetChartY(wall.Price);
                if (y < chartRect.Top - 30 || y > chartRect.Bottom + 30) continue;

                DrawWall(gr, chartRect, wall, y);
            }

            gr.ResetClip();
        }

        private void DrawWall(Graphics gr, Rectangle chartRect, TrackedWall wall, int y)
        {
            Color baseColor = GetWallColor(wall);
            int alpha = wall.IsVisible ? 200 : 80;
            Color lineColor = Color.FromArgb(alpha, baseColor);

            float width = WallLineWidth * (float)Math.Min(wall.Strength * 0.5 + 0.5, 3.0);
            DashStyle dash = wall.Status == WallStatus.Spoofed ? DashStyle.Dot : DashStyle.Dash;

            using (var pen = new Pen(lineColor, width) { DashStyle = dash })
                gr.DrawLine(pen, chartRect.Left, y, chartRect.Right, y);

            int bgAlpha = (int)(30 * Math.Min(wall.Strength * 0.3, 1.0));
            int bandH = Math.Max(4, (int)(8 * Math.Min(wall.Strength * 0.3, 1.5)));
            using (var brush = new SolidBrush(Color.FromArgb(bgAlpha, baseColor)))
                gr.FillRectangle(brush, chartRect.Left, y - bandH / 2, chartRect.Width, bandH);

            float panelX = chartRect.Right - 320;
            float panelY = y - 28;
            if (panelY < chartRect.Top) panelY = y + 4;

            DrawInfoPanel(gr, wall, panelX, panelY, baseColor);

            if (ShowDeltaBars)
                DrawDeltaBar(gr, wall, panelX - 75, panelY + 2);

            if (ShowAbsorption && wall.AbsorptionPct > 0)
                DrawAbsorptionMeter(gr, wall, panelX - 75, panelY + 15);

            if (ShowSpoofWarnings && wall.SpoofScore >= 40)
                DrawSpoofWarning(gr, wall, panelX - 140, panelY);
        }

        private Color GetWallColor(TrackedWall wall)
        {
            if (wall.SpoofScore >= 60) return SpoofColor;
            if (wall.Status == WallStatus.Breaking) return Color.FromArgb(255, 255, 80, 80);
            return wall.Side == WallSide.Ask ? AskWallColor : BidWallColor;
        }

        private void DrawInfoPanel(Graphics gr, TrackedWall wall, float x, float y, Color baseColor)
        {
            string sizeText = FormatSize(wall.CurrentSize);
            string priceText = this.FormatPrice(wall.Price);
            string statusText = GetStatusText(wall);
            string label = $"{sizeText} @ {priceText}  [{statusText}]";

            using (var font = new Font("Consolas", 8f, FontStyle.Bold))
            using (var bgBrush = new SolidBrush(Color.FromArgb(210, 15, 15, 20)))
            using (var textBrush = new SolidBrush(Color.FromArgb(240, baseColor)))
            using (var statusBrush = new SolidBrush(GetStatusColor(wall)))
            {
                SizeF sz = gr.MeasureString(label, font);
                gr.FillRectangle(bgBrush, x - 4, y - 1, sz.Width + 8, sz.Height + 4);

                string mainPart = $"{sizeText} @ {priceText}  [";
                SizeF mainSz = gr.MeasureString(mainPart, font);
                gr.DrawString(mainPart, font, textBrush, x, y);
                gr.DrawString(statusText, font, statusBrush, x + mainSz.Width - 6, y);

                string closeBracket = "]";
                SizeF statusSz = gr.MeasureString(statusText, font);
                gr.DrawString(closeBracket, font, textBrush, x + mainSz.Width - 6 + statusSz.Width - 6, y);
            }

            string deltaText = $"D: +{FormatSize(wall.BuyVolume)} / -{FormatSize(wall.SellVolume)}  Net:{(wall.NetDelta >= 0 ? "+" : "")}{FormatSize(wall.NetDelta)}";
            using (var font = new Font("Consolas", 7f))
            using (var brush = new SolidBrush(Color.FromArgb(180, 200, 200, 200)))
            {
                gr.DrawString(deltaText, font, brush, x, y + 13);
            }
        }

        private void DrawDeltaBar(Graphics gr, TrackedWall wall, float x, float y)
        {
            double total = wall.BuyVolume + wall.SellVolume;
            if (total <= 0) return;

            float barW = 65;
            float barH = 10;
            float buyPct = (float)(wall.BuyVolume / total);

            using (var bgBrush = new SolidBrush(Color.FromArgb(150, 15, 15, 20)))
                gr.FillRectangle(bgBrush, x, y, barW, barH);

            using (var buyBrush = new SolidBrush(Color.FromArgb(180, 50, 180, 50)))
                gr.FillRectangle(buyBrush, x, y, barW * buyPct, barH);

            using (var sellBrush = new SolidBrush(Color.FromArgb(180, 220, 50, 50)))
                gr.FillRectangle(sellBrush, x + barW * buyPct, y, barW * (1 - buyPct), barH);

            using (var pen = new Pen(Color.FromArgb(100, 255, 255, 255)))
                gr.DrawRectangle(pen, x, y, barW, barH);

            using (var font = new Font("Consolas", 6f))
            using (var brush = new SolidBrush(Color.White))
            {
                string pct = $"{buyPct * 100:F0}%";
                SizeF sz = gr.MeasureString(pct, font);
                gr.DrawString(pct, font, brush, x + (barW - sz.Width) / 2, y);
            }
        }

        private void DrawAbsorptionMeter(Graphics gr, TrackedWall wall, float x, float y)
        {
            float barW = 65;
            float barH = 7;
            float pct = (float)Math.Min(wall.AbsorptionPct / 100.0, 1.0);

            using (var bgBrush = new SolidBrush(Color.FromArgb(150, 15, 15, 20)))
                gr.FillRectangle(bgBrush, x, y, barW, barH);

            Color fillColor = pct > 0.6 ? Color.FromArgb(200, 255, 60, 60)
                            : pct > 0.3 ? Color.FromArgb(200, 255, 200, 50)
                            : Color.FromArgb(200, 50, 200, 50);

            using (var brush = new SolidBrush(fillColor))
                gr.FillRectangle(brush, x, y, barW * pct, barH);

            using (var pen = new Pen(Color.FromArgb(100, 255, 255, 255)))
                gr.DrawRectangle(pen, x, y, barW, barH);

            using (var font = new Font("Consolas", 5.5f))
            using (var brush = new SolidBrush(Color.White))
                gr.DrawString($"ABS {wall.AbsorptionPct:F0}%", font, brush, x + 2, y);
        }

        private void DrawSpoofWarning(Graphics gr, TrackedWall wall, float x, float y)
        {
            Color warnColor = wall.SpoofScore >= 75 ? Color.FromArgb(230, 255, 50, 50)
                            : wall.SpoofScore >= 60 ? Color.FromArgb(230, 255, 165, 0)
                            : Color.FromArgb(200, 255, 255, 50);

            string icon = wall.SpoofScore >= 75 ? "!! SPOOF" : wall.SpoofScore >= 60 ? "! SPOOF?" : "? SUSPECT";

            using (var font = new Font("Consolas", 7.5f, FontStyle.Bold))
            using (var bgBrush = new SolidBrush(Color.FromArgb(220, 40, 0, 0)))
            using (var textBrush = new SolidBrush(warnColor))
            {
                SizeF sz = gr.MeasureString(icon, font);
                gr.FillRectangle(bgBrush, x - 2, y, sz.Width + 4, sz.Height + 2);
                gr.DrawString(icon, font, textBrush, x, y);
            }
        }

        private string GetStatusText(TrackedWall wall)
        {
            return wall.Status switch
            {
                WallStatus.New => "NEW",
                WallStatus.Holding => "HOLD",
                WallStatus.Absorbing => "ABSORB",
                WallStatus.Breaking => "BREAK!",
                WallStatus.Spoofed => "SPOOF",
                WallStatus.Broken => "BROKEN",
                WallStatus.Removed => "GONE",
                _ => "---"
            };
        }

        private Color GetStatusColor(TrackedWall wall)
        {
            return wall.Status switch
            {
                WallStatus.New => Color.FromArgb(230, 100, 180, 255),
                WallStatus.Holding => Color.FromArgb(230, 50, 220, 50),
                WallStatus.Absorbing => Color.FromArgb(230, 255, 200, 50),
                WallStatus.Breaking => Color.FromArgb(230, 255, 60, 60),
                WallStatus.Spoofed => Color.FromArgb(230, 255, 140, 0),
                WallStatus.Broken => Color.FromArgb(150, 150, 150, 150),
                WallStatus.Removed => Color.FromArgb(100, 120, 120, 120),
                _ => Color.Gray
            };
        }

        private static string FormatSize(double size)
        {
            double abs = Math.Abs(size);
            string sign = size < 0 ? "-" : "";
            if (abs >= 1_000_000) return $"{sign}{abs / 1_000_000:F2}M";
            if (abs >= 1_000) return $"{sign}{abs / 1_000:F1}K";
            return $"{sign}{abs:F0}";
        }

        protected override void OnClear()
        {
            this.Symbol.NewLevel2 -= OnNewLevel2;
            this.Symbol.NewLast -= OnNewLast;
            lock (_lock)
            {
                _wallsByPrice.Clear();
                _displayWalls.Clear();
            }
        }

        #region Data Classes

        private class TrackedWall
        {
            public double Price;
            public double InitialSize;
            public double CurrentSize;
            public double PeakSize;
            public WallSide Side;
            public double Strength;
            public bool IsVisible;

            public DateTime FirstSeen;
            public DateTime LastSeen;
            public DateTime DisappearTime;
            public DateTime LastSizeChange;
            public DateTime LastTradeTime;

            public double BuyVolume;
            public double SellVolume;
            public double NetDelta => BuyVolume - SellVolume;

            public double AbsorbedVolume;
            public double AbsorptionPct => PeakSize > 0 ? Math.Min(100, (AbsorbedVolume / PeakSize) * 100) : 0;

            public int FlickerCount;
            public bool PulledNearPrice;
            public double SpoofScore;

            public WallStatus Status;
        }

        private enum WallSide { Ask, Bid }

        private enum WallStatus
        {
            New,
            Holding,
            Absorbing,
            Breaking,
            Broken,
            Spoofed,
            Removed
        }

        #endregion
    }
}
