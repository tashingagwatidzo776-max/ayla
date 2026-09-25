# Terminal chart trading & drawing — implementation plan

Three slices on the Terminal tab, each built, tested and committed
separately. All wiring points verified against the code as of M2 wrap
(`main` @ beb79c2).

## Slice A — Market Watch context menus

Right-click a symbol → **New Order** / **Create Alert** / **Open Chart**.

- `TerminalViewModel`: new optional ctor param `PriceAlertEngine? alerts = null`
  (appended last — existing named-arg tests keep compiling; DI in
  `App.xaml.cs` passes the existing `PriceAlertEngine` singleton).
- Commands (each takes the row, null-safe):
  - `SymbolNewOrderAsync(row)` — `Mt5Symbol = row.Symbol`, then selects the
    order ticket area (status hint). No order is sent from the menu.
  - `SymbolCreateAlert(row)` — one alert above + one below the row's last
    price (±1 big-figure of spread-aware offset: above = last + spread,
    below = last − spread); `PriceAlertEngine.Add` dedupes; status confirms.
  - `SymbolOpenChart(row)` — `SelectedSymbol = row` (existing machinery
    loads candles + ladder + settings sync).
- `MainWindow.xaml`: `ContextMenu` on the symbols `DataGrid`; items bind
  `PlacementTarget.SelectedItem` so the right-clicked (or selected) row is
  the parameter.
- Tests: `TerminalContextCommandsTests` — alert add/dedupe, symbol handoff,
  chart switch, null-row no-ops.

## Slice B — Drawing tools on `CandleChartControl`

- `ChartDrawing(string Kind, int Index1, double Price1, int Index2, double
  Price2)` — anchors are (bar index, price) so drawings stay glued to bars
  through zoom/pan. `Kind` is `"trend"` (segment) or `"hlevel"` (Index2
  ignored; spans the plot at Price1).
- API: `AddDrawing`, `Drawings`, `ClearDrawings`; event
  `ChartContextMenuRequested(Point pos, double price, int barIndex)`.
- Input: **right-drag** previews a trendline; release commits it when the
  drag exceeds 4px, otherwise raises the context-menu event (the menu then
  offers New Order / Add Alert / Add Horizontal Line / Clear Drawings).
- Render: dashed gold trendlines, dashed cyan h-levels with price tags;
  scale computation extracted into one private helper shared with `OnRender`.
- Trim safety: when the 600-bar window trims k bars from the front,
  drawing indices shift by k and drawings with both anchors off-window are
  dropped (h-levels are price-based and never drop).
- Tests (STA, same pattern as the existing chart tests): add/clear, trim
  shift, index clamp; context event fires on plain right-click.

## Slice C — Chart trading

- `CandleChartControl`: `ChartPriceLine(long? Ticket, string Kind, double
  Price)` records (`entry`/`sl`/`tp`), `SetPriceLines(...)`, and:
  - hover within ±5px of a line → cursor `SizeNS`, left-drag moves it,
    `LineDragged(line, newPrice)` fires on release (live preview while dragging);
  - the context-menu event also feeds chart orders.
- `TerminalViewModel`:
  - order-command refactor: the guard sequence (kill switch → Mt5MaxLots →
    lots sanity → pending-type prices → bridge → real-money gate) is
    extracted into `ExecuteMt5OrderAsync(...)`; both the existing ticket
    command and the new `PlaceChartOrderCommand(side)` call it — chart
    orders are market orders at bid/ask, sized with `Mt5Lots`, full gate.
  - `ChartLineDraggedCommand(ticket, kind, price)` → `ModifyPositionAsync`
    with only the changed leg; journaled, status line, re-poll.
  - price lines rebuilt whenever positions/orders refresh, filtered to the
    selected symbol: entry (position open price / pending order price) + SL
    + TP.
- `MainWindow.xaml.cs` (`OnLoaded`, where the chart is already handed to
  the VM): wire `ChartOrderRequested` → a small `ContextMenu` (Buy at ask /
  Sell at bid / Add horizontal line / Clear drawings); `LineDragged` → VM.
- Tests: chart-order guards (kill switch, lots cap, gate fail-closed),
  success path against the fake handler, line-drag modify call, price-line
  rebuild filtering.
