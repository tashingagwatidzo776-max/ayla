using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DongGfx.App.Services;
using DongGfx.Core.Fx;

namespace DongGfx.App.ViewModels;

/// <summary>
/// The Maps tab: one selector-driven canvas over the eight market surfaces
/// (volume surface, volume profile, liquidity, order flow, volatility,
/// correlation, SMC/ICT sweeps, AI probability). Read-only by construction —
/// it renders computed grids; it never touches the order path.
///
/// Data sources: bars from the Terminal's bridge candle poll (the same M1
/// closes the chart shows) and ticks from the TickArchive. Degenerate data
/// (empty archive, thin bars, single symbol) renders an honest empty or
/// flat map — never an error.
/// </summary>
public partial class MapsViewModel : ObservableObject
{
    /// <summary>The renderable surfaces, in tab order.</summary>
    public static readonly string[] MapNames =
    [
        "Volume surface", "MTF confluence", "Volume profile", "Liquidity",
        "Order flow", "Volatility", "Correlation", "SMC/ICT sweeps", "AI probability",
    ];

    /// <summary>Bars for bar-based maps. The Terminal feeds this every
    /// candle poll; an empty list renders an empty map.</summary>
    public Func<IReadOnlyList<FxBar>>? BarsProvider { get; set; }

    /// <summary>Archived ticks for tick-based maps, pre-classified by the
    /// tick rule by the caller (TickArchive → ticks_to_bars convention).</summary>
    public Func<IReadOnlyList<(double Price, double Vol, long TimeMs, int Direction)>>? TicksProvider { get; set; }

    /// <summary>Per-symbol close series for the correlation map.</summary>
    public Func<IReadOnlyList<(string Symbol, IReadOnlyList<double> Closes)>>? CorrelationProvider { get; set; }

    /// <summary>The brain's per-symbol edge beliefs for the AI map
    /// (0..1). Null renders the honest "no beliefs yet" map.</summary>
    public Func<IReadOnlyList<(string Symbol, double Probability)>>? AiBeliefsProvider { get; set; }

    /// <summary>Per-timeframe bar frames for the MTF confluence map
    /// (oldest first). Null/empty renders the honest neutral map.</summary>
    public Func<IReadOnlyList<(string Timeframe, IReadOnlyList<FxBar> Bars)>>? ConfluenceProvider { get; set; }

    [ObservableProperty]
    private string selectedMap = MapNames[0];

    [ObservableProperty]
    private string statusText = "loading…";

    /// <summary>The currently rendered grid (bound: ItemsSource of the canvas).</summary>
    public FxHeatMap? CurrentMap { get; private set; }

    /// <summary>Buy-share overlay for the split volume surface (null on other maps).</summary>
    public double[,]? CurrentBuyShare { get; private set; }

    public ObservableCollection<string> Maps { get; } = new(MapNames);

    /// <summary>Colormap picker choices: Auto = per-map defaults
    /// (<see cref="FxCmap.DefaultFor"/>), the rest are explicit.</summary>
    public static readonly string[] ColormapChoices =
    [
        "Auto", "Turbo", "Viridis", "Inferno", "Plasma", "Magma", "CoolHot",
    ];

    [ObservableProperty]
    private string selectedColormap = "Auto";

    /// <summary>Bridge candles fetched off the render path, keyed by
    /// timeframe (primary symbol). The confluence provider reads this
    /// cache; the async preloader fills it — no sync-over-async anywhere
    /// on the UI thread (a .GetAwaiter().GetResult() here deadlocks the
    /// dispatcher — seen live 2026-09-27).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<FxBar>> CachedCandles { get; } =
        new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-symbol close series for the correlation map, same
    /// preloader contract as <see cref="CachedCandles"/>.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, double[]> CachedCloses { get; } =
        new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Manual refresh button. A plain RelayCommand rather than a
    /// [RelayCommand]-generated one, so the XAML binding name is explicit
    /// and unaffected by generator naming rules.</summary>
    public System.Windows.Input.ICommand RefreshCommand { get; } =
        new RelayCommand(() => { });

    public MapsViewModel() { }

    partial void OnSelectedMapChanged(string value) => _ = RefreshAsync();

    /// <summary>Async refresh: awaits nothing blocking, so it is safe on
    /// the UI thread. The timer tick and map-switch handler await this.</summary>
    public async Task RefreshAsync()
    {
        await Task.Yield();   // keep the async state machine; body stays sync-fast
        Refresh();
    }

    /// <summary>Synchronous recompute of the selected map from the
    /// providers + caches. Cheap by contract: providers read caches,
    /// never the network.</summary>
    public void Refresh()
    {
        try
        {
            (CurrentMap, CurrentBuyShare) = SelectedMap switch
            {
                "Volume surface" => VolumeSurfaceMaps(),
                "MTF confluence" => (Safe(() => FxMaps.Confluence(Confluence() ?? [])), null),
                "Volume profile" => (Safe(() => FxMaps.VolumeProfile(Bars())),
                    null),
                "Liquidity" => (Safe(() => FxMaps.Liquidity(Quotes())), null),
                "Order flow" => (Safe(() => FxMaps.OrderFlow(Ticks()
                    .Select(t => (t.Price, t.Vol, t.TimeMs)).ToList())), null),
                "Volatility" => (Safe(() => FxMaps.Volatility(Bars())), null),
                "Correlation" => (Safe(() => FxMaps.Correlation(Correlation() ?? DefaultCorrelation())), null),
                "SMC/ICT sweeps" => (Safe(() => FxMaps.SmcIct(Bars())), null),
                "AI probability" => (Safe(() => FxMaps.AiProbability(AiBeliefs() ?? [])), null),
                _ => (null, null),
            };
            StatusText = CurrentMap is null
                ? "no data yet"
                : $"{CurrentMap.Name} — {CurrentMap.Columns}×{CurrentMap.Rows}, {CurrentMap.Unit} range {CurrentMap.Min:0.####}..{CurrentMap.Max:0.####}";
        }
        catch (Exception ex)
        {
            StatusText = $"map error: {ex.Message}";
            CurrentMap = null;
            CurrentBuyShare = null;
        }
        OnPropertyChanged(nameof(CurrentMap));
        OnPropertyChanged(nameof(CurrentBuyShare));
    }

    private IReadOnlyList<FxBar> Bars() => BarsProvider?.Invoke() ?? [];

    private IReadOnlyList<(double Price, double Vol, long TimeMs, int Direction)> Ticks()
        => TicksProvider?.Invoke() ?? [];

    /// <summary>Real bid/ask quotes when the provider supplies them; the
    /// tick-archive fallback has no spread, so it degrades to mid-only
    /// (flat liquidity map, honestly labeled by its near-zero range).</summary>
    public Func<IReadOnlyList<(double Bid, double Ask, long TimeMs)>>? QuotesProvider { get; set; }

    private IReadOnlyList<(double Bid, double Ask, long TimeMs)> Quotes()
        => QuotesProvider?.Invoke()
           ?? Ticks().Select(t => (Bid: t.Price, Ask: t.Price, TimeMs: t.TimeMs)).ToList();

    private IReadOnlyList<(string Symbol, IReadOnlyList<double> Closes)>? Correlation()
        => CorrelationProvider?.Invoke();

    private IReadOnlyList<(string Symbol, double Probability)>? AiBeliefs()
        => AiBeliefsProvider?.Invoke();

    private IReadOnlyList<(string Timeframe, IReadOnlyList<FxBar> Bars)>? Confluence()
        => ConfluenceProvider?.Invoke();

    private static IReadOnlyList<(string, IReadOnlyList<double>)> DefaultCorrelation() => [];

    private static FxHeatMap Safe(Func<FxHeatMap> build)
    {
        try
        {
            return build();
        }
        catch (Exception)
        {
            return new FxHeatMap("unavailable", "—", "—", 1, 1,
                new double[1, 1], 0, 0, "");
        }
    }

    private (FxHeatMap, double[,]?) VolumeSurfaceMaps()
    {
        var ticks = Ticks();
        if (ticks.Count == 0)
        {
            return (FxMaps.VolumeSurface([]), null);
        }
        var (map, share) = FxMaps.VolumeSurfaceSplit(ticks);
        return (map, share);
    }
}
