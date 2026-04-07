# DOM Wall Detector - Quantower Indicator

A custom indicator for [Quantower](https://www.quantower.com/) trading platform that detects large order walls in the DOM (Depth of Market) and draws them as horizontal lines directly on the price chart.

## What It Does

- Subscribes to real-time Level2 (order book) data
- Identifies abnormally large orders ("walls") on both bid and ask sides
- Draws colored horizontal lines on the chart at wall price levels
- Shows size labels with wall strength multiplier

**Ask walls** (sell walls) are drawn in red, **Bid walls** (buy walls) in green.

## Parameters

| Parameter | Default | Description |
|---|---|---|
| Wall Threshold Multiplier | 3.0 | How many times larger than average a level must be to qualify as a wall |
| Levels Count | 50 | Number of DOM levels to scan |
| Min Wall Size | 0 (auto) | Fixed minimum size threshold (0 = use multiplier) |
| Line Opacity | 180 | Line transparency (0-255) |
| Show Size Labels | Yes | Show/hide price and size labels |
| Ask Wall Color | Red | Color for ask (sell) walls |
| Bid Wall Color | Green | Color for bid (buy) walls |
| Line Width | 2 | Base line thickness |
| Line Style | Dash | Solid / Dash / Dot / DashDot |
| Show Background Fill | Yes | Semi-transparent band behind the line |
| Background Opacity | 25 | Background band transparency |
| Max Walls to Display | 10 | Limit number of walls shown |

## Installation

### Prerequisites

- [Quantower](https://www.quantower.com/) trading platform
- [Visual Studio](https://visualstudio.microsoft.com/) with [Quantower Algo extension](https://marketplace.visualstudio.com/items?itemName=Quantower.quantoweralgo)
- .NET 8.0 SDK

### Build & Install

1. Clone this repository
2. Open `DOMWallDetector.csproj` in Visual Studio
3. Update the `TradingPlatform.BusinessLayer.dll` reference path in `.csproj` to match your Quantower installation
4. Build the project (F6)
5. Copy the output DLL to your Quantower indicators folder:
   ```
   <Quantower>\Settings\Scripts\Indicators\DOMWallDetector\
   ```
6. Restart Quantower, add the indicator to any chart

## Quantower API

Built using [Quantower API](https://api.quantower.com/). Key APIs used:

- `Symbol.NewLevel2` - Real-time Level2 data subscription
- `DepthOfMarket.GetDepthOfMarketAggregatedCollections()` - Order book snapshot
- `OnPaintChart()` + GDI+ - Custom chart drawing
- `IChartWindowCoordinatesConverter.GetChartY()` - Price to pixel conversion

## License

MIT
