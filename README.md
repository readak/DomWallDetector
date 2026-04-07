# DOM Wall Detector V2 - Quantower Indicator

A professional indicator for [Quantower](https://www.quantower.com/) that detects large order walls in the DOM, tracks delta aggression, monitors absorption, and detects spoofing.

## Features

### Wall Detection
- Real-time scanning of Level2 (order book) data
- Automatic threshold calculation based on average order size
- Configurable multiplier and manual minimum size

### Delta Tracking
- Tracks every trade (tick) via `Symbol.NewLast` with `AggressorFlag`
- Accumulates buy/sell volume at each wall price level
- Shows net delta and buy/sell ratio with visual bar

### Absorption Detection
- Monitors wall size changes over time
- Calculates absorption percentage (how much of the wall has been eaten)
- Visual meter showing absorption progress
- Status transitions: NEW → HOLD → ABSORB → BREAK!

### Spoof Detection
Scoring system (0-100) based on:
- **Duration**: Walls that appear and disappear within seconds score high
- **Flickering**: Walls that repeatedly appear/disappear
- **Pull-ahead**: Walls that are removed when price approaches
- **No-fill removal**: Walls removed without being absorbed

Score thresholds:
- 0-39: Normal wall
- 40-59: `? SUSPECT` - yellow warning
- 60-74: `! SPOOF?` - orange warning  
- 75-100: `!! SPOOF` - red warning

### Wall Status System
| Status | Meaning |
|--------|---------|
| `NEW` | Just appeared (< 10 sec) |
| `HOLD` | Stable, holding its ground |
| `ABSORB` | Being eaten by aggressive orders |
| `BREAK!` | About to break (> 60% absorbed) |
| `SPOOF` | Detected as likely spoofing |
| `BROKEN` | Was absorbed and removed |
| `GONE` | Disappeared from DOM |

## Visual Elements

Each wall displays on the chart:
1. **Horizontal line** - dashed for normal, dotted for spoof
2. **Info panel** - size, price, status with color coding
3. **Delta text** - buy/sell volumes and net delta
4. **Delta bar** - green/red ratio visualization
5. **Absorption meter** - progress bar (green → yellow → red)
6. **Spoof warning** - alert badge when score is high

## Parameters

| Parameter | Default | Description |
|---|---|---|
| Wall Threshold Multiplier | 3.0 | Size multiplier over average to detect walls |
| Levels Count | 50 | DOM depth to scan |
| Min Wall Size | 0 (auto) | Fixed minimum threshold |
| Max Walls | 10 | Maximum walls displayed |
| Delta Range (ticks) | 3 | How close a trade must be to a wall to count |
| Spoof Time Threshold | 5 sec | Duration below which walls are suspicious |
| Spoof Flicker Count | 3 | Flicker count to trigger spoof warning |
| Show Delta Bars | Yes | Toggle delta visualization |
| Show Absorption Meter | Yes | Toggle absorption meter |
| Show Spoof Warnings | Yes | Toggle spoof alerts |

## Installation

1. Clone this repository
2. Open `DOMWallDetector.csproj` in Visual Studio
3. Update the `TradingPlatform.BusinessLayer.dll` reference path to match your Quantower installation
4. Build (F6)
5. Copy output DLL to: `<Quantower>\Settings\Scripts\Indicators\DOMWallDetector\`
6. Restart Quantower and add "DOM Wall Detector V2" to any chart

## API Used

- `Symbol.NewLevel2` - DOM data stream
- `Symbol.NewLast` + `Last.AggressorFlag` - Trade ticks with buy/sell side
- `DepthOfMarket.GetDepthOfMarketAggregatedCollections()` - Order book snapshot
- `OnPaintChart()` + GDI+ - Custom chart rendering
- `IChartWindowCoordinatesConverter.GetChartY()` - Price to pixel mapping

## License

MIT
