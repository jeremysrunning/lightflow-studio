using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;

namespace LightflowStudio;

/// <summary>True collapses; false (or anything else) shows. The inverse of the standard converter.</summary>
internal sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound string is null/empty; collapsed otherwise. Drives a search box's placeholder text.</summary>
internal sealed class StringEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// One media tile in the Browser thumbnail grid. Identity is the stable, Catalog-normalized
/// <see cref="MediaFolderEntry.RelativePathKey"/> rather than any visual container, so selection and
/// thumbnail state survive recycling, reflow, and non-destructive folder refresh.
/// </summary>
internal sealed class BrowserGridTile : INotifyPropertyChanged
{
    private bool _isSelected;
    private string? _thumbnailPath;
    private int _index;
    private DateTime? _captureDate;
    private double? _durationSeconds;
    private BrowserAssetState _assetState;

    public BrowserGridTile(MediaFolderEntry entry, int index)
    {
        RootId = entry.RootId;
        RelativePath = entry.RelativePath;
        Key = entry.RelativePathKey;
        Name = entry.Name;
        Category = entry.MediaType.Category;
        FileSizeBytes = entry.FileSizeBytes;
        ModifiedUtc = entry.LastWriteUtc;
        _index = index;
    }

    public Guid RootId { get; }
    public string RelativePath { get; }
    public string Key { get; }
    public string Name { get; }
    public MediaTypeCategory Category { get; }
    public long? FileSizeBytes { get; }

    /// <summary>Filesystem last-write time from enumeration; always available without probing a source.</summary>
    public DateTimeOffset ModifiedUtc { get; }

    /// <summary>EXIF capture time (images only) or null until #91 metadata probing completes/finds none. See <see cref="BrowserQueryEngine.ParseExifCaptureDate"/>.</summary>
    public DateTime? CaptureDate
    {
        get => _captureDate;
        private set { if (_captureDate != value) { _captureDate = value; OnPropertyChanged(); } }
    }

    /// <summary>Video duration from #91 metadata probing, or null until probed/unavailable.</summary>
    public double? DurationSeconds
    {
        get => _durationSeconds;
        private set { if (_durationSeconds != value) { _durationSeconds = value; OnPropertyChanged(); } }
    }

    /// <summary>True once a metadata probe result (success or not) has been applied at least once, so callers avoid redundant Preview-store lookups.</summary>
    public bool MetadataApplied { get; private set; }

    public Guid? AssetId { get; private set; }

    /// <summary>
    /// A tile only ever represents a supported still image, RAW image, or video asset — see
    /// <see cref="BrowserGridModel.IsPresentable"/>. This glyph set is therefore exhaustive.
    /// </summary>
    public string CategoryGlyph => Category switch
    {
        MediaTypeCategory.StillImage => "",
        MediaTypeCategory.RawImage => "",
        MediaTypeCategory.Video => "",
        _ => throw new InvalidOperationException($"{Category} is not a presentable Browser media category.")
    };

    public string AutomationLabel => $"{Name}, {Category} media";

    public int Index
    {
        get => _index;
        set { if (_index != value) { _index = value; OnPropertyChanged(); } }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    /// <summary>Absolute path of the generated Preview thumbnail, or null while a placeholder is shown.</summary>
    public string? ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            if (_thumbnailPath == value) return;
            _thumbnailPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnailPath is not null;

    /// <summary>Durable, user-authored Catalog state projected for Browser presentation; never Preview state.</summary>
    public BrowserAssetState AssetState
    {
        get => _assetState;
        private set
        {
            if (_assetState == value) return;
            _assetState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUserAuthoredState));
            OnPropertyChanged(nameof(AssetStateLabel));
        }
    }

    public bool HasUserAuthoredState => AssetState != BrowserAssetState.None;

    public string AssetStateLabel => AssetState.HasFlag(BrowserAssetState.ReviewRange)
        ? "Saved In/Out range"
        : "User-authored asset state";

    public void SetAssetId(Guid assetId)
    {
        if (AssetId == assetId) return;
        AssetId = assetId;
        OnPropertyChanged(nameof(AssetId));
    }

    public void SetAssetState(BrowserAssetState state) => AssetState = state;

    /// <summary>Applies a metadata probe result in place. Returns true if either sortable value actually changed, so callers can coalesce a re-sort.</summary>
    public bool ApplyMetadata(DateTime? captureDate, double? durationSeconds)
    {
        var changed = _captureDate != captureDate || _durationSeconds != durationSeconds;
        CaptureDate = captureDate;
        DurationSeconds = durationSeconds;
        MetadataApplied = true;
        return changed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A bounded-width row of tiles. Rows are the outer virtualization unit for the grid.</summary>
internal sealed class BrowserGridRow(IReadOnlyList<BrowserGridTile> tiles)
{
    public ObservableCollection<BrowserGridTile> Tiles { get; } = new(tiles);

    public void SetTiles(IReadOnlyList<BrowserGridTile> tiles)
    {
        for (var index = 0; index < tiles.Count; index++)
        {
            if (index < Tiles.Count) { if (!ReferenceEquals(Tiles[index], tiles[index])) Tiles[index] = tiles[index]; }
            else Tiles.Add(tiles[index]);
        }
        while (Tiles.Count > tiles.Count) Tiles.RemoveAt(Tiles.Count - 1);
    }
}

/// <summary>
/// #125: the Browser's discrete, user-adjustable thumbnail presentation sizes — presentation state only,
/// never <see cref="BrowserQuery"/>, Browser scope, Catalog, or Preview-generation policy. A small semantic
/// setting (an ordered level, see <see cref="BrowserGridLayout.ThumbnailSizes"/>) rather than a raw pixel
/// value scattered through WPF code, so a future list/alternate presentation mode can extend the same shape.
/// <c>Huge</c>/<c>Maximum</c> were appended after hands-on testing found the original four-stop range topped
/// out too small; they are declared last (values 4/5) specifically so the original Small/Medium/Large/
/// ExtraLarge keep their original 0-3 values and every already-persisted level continues to mean exactly the
/// same size.
/// </summary>
internal enum BrowserThumbnailSize { Small, Medium, Large, ExtraLarge, Huge, Maximum }

/// <summary>
/// Pure column/row arithmetic for the responsive thumbnail grid. Kept independent of WPF so reflow
/// behavior is directly unit-testable.
/// </summary>
internal static class BrowserGridLayout
{
    /// <summary>The Medium/default tile footprint — unchanged from the single fixed size that existed before #125, so a legacy/missing persisted size restores pixel-identical behavior.</summary>
    public const double TileWidth = 168;
    public const double TileSpacing = 12;

    /// <summary>The Medium/default thumbnail-image area height — unchanged from before #125.</summary>
    public const double ThumbnailAreaHeight = 108;

    /// <summary>
    /// Every level in display/slider order — the single authoritative source for both "how many levels
    /// exist" (the slider's Minimum/Maximum and persisted-value clamping both derive from this) and "what
    /// index does a level occupy" (persisted/restored as a plain level index, see <see cref="ThumbnailSizeFromLevel"/>).
    /// </summary>
    public static readonly IReadOnlyList<BrowserThumbnailSize> ThumbnailSizes =
        [BrowserThumbnailSize.Small, BrowserThumbnailSize.Medium, BrowserThumbnailSize.Large, BrowserThumbnailSize.ExtraLarge,
            BrowserThumbnailSize.Huge, BrowserThumbnailSize.Maximum];

    public const BrowserThumbnailSize DefaultThumbnailSize = BrowserThumbnailSize.Medium;

    /// <summary>Clamps an arbitrary (e.g. persisted, possibly stale or out-of-range) level index into a valid <see cref="BrowserThumbnailSize"/>. Never throws.</summary>
    public static BrowserThumbnailSize ThumbnailSizeFromLevel(int level) =>
        ThumbnailSizes[Math.Clamp(level, 0, ThumbnailSizes.Count - 1)];

    /// <summary>
    /// Moves exactly one level up (<paramref name="delta"/> = 1) or down (-1) within the valid range,
    /// clamped rather than wrapping at either end — the shared step operation behind the presentation
    /// control's decrease/increase buttons. <see cref="BrowserThumbnailSize"/>'s declared order matches
    /// <see cref="ThumbnailSizes"/>' index order exactly, so the enum's own underlying value already *is*
    /// its list index; this reuses <see cref="ThumbnailSizeFromLevel"/>'s existing clamp rather than a
    /// second bounds check.
    /// </summary>
    public static BrowserThumbnailSize StepLevel(BrowserThumbnailSize current, int delta) =>
        ThumbnailSizeFromLevel((int)current + delta);

    /// <summary>
    /// Tile footprint per size level. <c>Maximum</c>'s 512px width sits exactly at the #70 thumbnail
    /// generator's maximum source dimension (see <c>WicImageThumbnailRenderer</c>/<c>FfmpegVideoThumbnailRenderer</c>,
    /// both <c>maximumPixelDimension = 512</c>) — the largest size a landscape-oriented cached thumbnail can
    /// fill without any upscaling, and the practical ceiling this revision works within rather than
    /// introducing a second, higher-resolution Preview tier. Every level at or under 512 on both axes stays
    /// within that ceiling.
    /// </summary>
    public static double TileWidthFor(BrowserThumbnailSize size) => size switch
    {
        BrowserThumbnailSize.Small => 120,
        BrowserThumbnailSize.Medium => TileWidth,
        BrowserThumbnailSize.Large => 224,
        BrowserThumbnailSize.ExtraLarge => 288,
        BrowserThumbnailSize.Huge => 384,
        BrowserThumbnailSize.Maximum => 512,
        _ => throw new ArgumentOutOfRangeException(nameof(size))
    };

    /// <summary>Thumbnail-image area height per size level, proportional to <see cref="TileWidthFor"/>.</summary>
    public static double ThumbnailAreaHeightFor(BrowserThumbnailSize size) => size switch
    {
        BrowserThumbnailSize.Small => 78,
        BrowserThumbnailSize.Medium => ThumbnailAreaHeight,
        BrowserThumbnailSize.Large => 144,
        BrowserThumbnailSize.ExtraLarge => 184,
        BrowserThumbnailSize.Huge => 248,
        BrowserThumbnailSize.Maximum => 330,
        _ => throw new ArgumentOutOfRangeException(nameof(size))
    };

    /// <summary>
    /// Every tile's margin trails the tile itself (right and bottom), including the last tile in a row —
    /// WPF measures that trailing margin as part of the tile's layout footprint regardless of position, so
    /// each tile (not just the gaps between them) reserves exactly <paramref name="tileWidth"/> + <paramref name="spacing"/>.
    /// Using a smaller per-tile footprint here (e.g. treating spacing as only between tiles) overcounts how
    /// many tiles actually fit at specific boundary widths, producing a tile the row's panel cannot actually
    /// place on that line — the previous cause of an orphaned/misaligned row.
    /// </summary>
    public static int ComputeColumns(double availableWidth, double tileWidth = TileWidth, double spacing = TileSpacing)
    {
        if (tileWidth <= 0) throw new ArgumentOutOfRangeException(nameof(tileWidth));
        var footprint = tileWidth + spacing;
        if (availableWidth < footprint) return 1;
        return Math.Max(1, (int)(availableWidth / footprint));
    }

    public static IReadOnlyList<IReadOnlyList<BrowserGridTile>> BuildRows(IReadOnlyList<BrowserGridTile> tiles, int columns)
    {
        columns = Math.Max(1, columns);
        var rows = new List<IReadOnlyList<BrowserGridTile>>();
        for (var index = 0; index < tiles.Count; index += columns)
            rows.Add(tiles.Skip(index).Take(columns).ToArray());
        return rows;
    }
}

/// <summary>
/// Desktop multi-selection over stable tile keys. Selection never depends on visual-container identity,
/// so it survives recycling and non-destructive refresh.
/// </summary>
internal sealed class BrowserGridSelection
{
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    public int? AnchorIndex { get; private set; }
    public bool IsSelected(string key) => _selected.Contains(key);
    public IReadOnlySet<string> Snapshot() => new HashSet<string>(_selected, StringComparer.Ordinal);

    public void SelectSingle(string key, int index)
    {
        _selected.Clear();
        _selected.Add(key);
        AnchorIndex = index;
    }

    public void ToggleCtrl(string key, int index)
    {
        if (!_selected.Remove(key)) _selected.Add(key);
        AnchorIndex = index;
    }

    /// <summary>Replaces the selection with the given keys without moving the shift-range anchor.</summary>
    public void SelectRange(IReadOnlyList<string> keys)
    {
        _selected.Clear();
        foreach (var key in keys) _selected.Add(key);
    }

    public void SelectAll(IEnumerable<string> keys)
    {
        _selected.Clear();
        foreach (var key in keys) _selected.Add(key);
    }

    public void Clear()
    {
        _selected.Clear();
        AnchorIndex = null;
    }

    /// <summary>Drops selected keys that no longer exist, e.g. after a non-destructive refresh.</summary>
    public void Retain(IEnumerable<string> stillPresentKeys) => _selected.IntersectWith(stillPresentKeys);

    /// <summary>
    /// Clears the shift-range anchor without touching the selection itself. A changed sort/filter/search
    /// reorders and re-numbers the visible set, so an old anchor index would produce a wrong range on the
    /// next Shift+click; the selected items themselves remain exactly as they were.
    /// </summary>
    public void ResetAnchor() => AnchorIndex = null;
}

/// <summary>
/// Owns the Browser thumbnail grid's tiles, row-based virtualization grouping, and selection state.
/// Consumes only the existing #98-#100 discovery/reconciliation/derived-work outputs; it never enumerates
/// files, resolves Catalog identity, or generates thumbnails itself.
/// </summary>
internal sealed class BrowserGridModel
{
    // The full presentable set for the current scope (identity/thumbnail/metadata home). Never reordered or
    // filtered directly — that projection is _visibleTiles, recomputed from this master list plus the
    // current query. Keeping both means a sort/filter/search change is pure re-projection over already-owned
    // tile instances: it never touches the filesystem, Catalog, or Preview store, and selection (tracked by
    // stable key against this master list) is naturally unaffected by which items the query currently shows.
    private List<BrowserGridTile> _allTiles = [];
    private List<BrowserGridTile> _visibleTiles = [];
    private readonly Dictionary<Guid, BrowserGridTile> _tilesByAsset = [];
    private readonly BrowserGridSelection _selection = new();
    private int _columns = 1;

    public ObservableCollection<BrowserGridRow> Rows { get; } = [];

    /// <summary>The current query's visible, ordered projection — what is actually rendered.</summary>
    public IReadOnlyList<BrowserGridTile> Tiles => _visibleTiles;

    public int TotalCount => _allTiles.Count;
    public int VisibleCount => _visibleTiles.Count;
    public BrowserQuery Query { get; private set; } = BrowserQuery.Default;
    public IReadOnlySet<string> SelectedKeys => _selection.Snapshot();

    /// <summary>
    /// Sum of <see cref="BrowserGridTile.FileSizeBytes"/> for every selected item, regardless of whether the
    /// current query currently shows it — selection is a property of the item, not of the current view.
    /// </summary>
    public long SelectedTotalSizeBytes
    {
        get
        {
            var selected = _selection.Snapshot();
            return _allTiles.Where(tile => selected.Contains(tile.Key)).Sum(tile => tile.FileSizeBytes ?? 0);
        }
    }

    /// <summary>
    /// The Browser's supported presentation categories, in display order. The single authoritative source
    /// for "is this category presentable" (<see cref="IsPresentable"/>) and for generating one UI control
    /// per category (e.g. the quick-filter buttons) — never duplicated as a second, hand-maintained list
    /// that could silently drift from what the grid actually shows.
    /// </summary>
    public static readonly IReadOnlyList<MediaTypeCategory> PresentableCategories =
        [MediaTypeCategory.StillImage, MediaTypeCategory.RawImage, MediaTypeCategory.Video];

    /// <summary>
    /// Lightflow's Browser is a media browser, not a general-purpose file browser: the central canvas
    /// presents only supported still image, RAW image, and video assets. This reads the classification
    /// the existing #98 media type registry already assigned to the entry; it does not maintain a second,
    /// WPF-owned extension list. Folders, standalone audio, and unknown/unsupported files are excluded
    /// from presentation entirely rather than shown as a placeholder tile.
    /// </summary>
    public static bool IsPresentable(MediaFolderEntry entry) => !entry.IsDirectory && PresentableCategories.Contains(entry.MediaType.Category);

    /// <summary>
    /// Replaces the master tile set from the current folder's file entries, reusing existing tile instances
    /// by stable key so already-resolved thumbnails, metadata, and selection survive a non-destructive
    /// refresh. The current query (sort/filter/search) is preserved and re-applied over the new set.
    /// </summary>
    public void Populate(IReadOnlyList<MediaFolderEntry> entries)
    {
        var existingByKey = _allTiles.ToDictionary(tile => tile.Key, StringComparer.Ordinal);
        var desired = new List<BrowserGridTile>(entries.Count);
        foreach (var entry in entries)
        {
            if (!IsPresentable(entry)) continue;
            desired.Add(existingByKey.TryGetValue(entry.RelativePathKey, out var prior) ? prior : new BrowserGridTile(entry, 0));
        }

        _allTiles = desired;
        _tilesByAsset.Clear();
        foreach (var tile in _allTiles.Where(tile => tile.AssetId is not null))
            _tilesByAsset[tile.AssetId!.Value] = tile;

        _selection.Retain(_allTiles.Select(tile => tile.Key));
        var selected = _selection.Snapshot();
        foreach (var tile in _allTiles) tile.IsSelected = selected.Contains(tile.Key);

        RecomputeVisible();
    }

    /// <summary>
    /// Applies a new sort/filter/search intent. Selection is left entirely untouched (it is not scoped to
    /// the current view — see <see cref="SelectedTotalSizeBytes"/>); only the shift-range anchor resets,
    /// since the visible order/positions it referred to no longer apply. Always recomputes rather than
    /// short-circuiting on an unchanged query: <see cref="BrowserQuery.Filters"/> is a list, so record
    /// equality on <see cref="BrowserQuery"/> would only catch reference-identical predicate collections,
    /// not equivalent ones — recomputation is cheap enough that a reliable no-op check is not worth the
    /// complexity it would add to an otherwise plain, equatable query representation.
    /// </summary>
    public void SetQuery(BrowserQuery query)
    {
        Query = query;
        _selection.ResetAnchor();
        RecomputeVisible();
    }

    /// <summary>
    /// Re-applies the unchanged current query, for when underlying tile data a metadata-dependent sort
    /// (capture date/duration) relies on has changed in place — e.g. progressively-arriving #91 metadata —
    /// without the query itself changing. A plain <see cref="SetQuery"/> call would no-op on an identical
    /// query; this bypasses that to force recomputation. Does not reset the selection anchor: nothing the
    /// user did caused this, so an in-progress Shift+range gesture should not be disturbed by it.
    /// </summary>
    public void ReapplyQuery() => RecomputeVisible();

    /// <summary>Maps freshly-reconciled Catalog identity onto tiles so later thumbnail/metadata results can be applied.</summary>
    public void ApplyAssetIdentities(IReadOnlyList<CatalogReconciliationItem> items)
    {
        if (_allTiles.Count == 0 || items.Count == 0) return;
        var byKey = _allTiles.ToDictionary(tile => tile.Key, StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = MediaPathSemantics.RelativePathKey(item.RelativePath);
            if (!byKey.TryGetValue(key, out var tile)) continue;
            tile.SetAssetId(item.AssetId);
            _tilesByAsset[item.AssetId] = tile;
        }
    }

    public bool HasThumbnail(Guid assetId) => _tilesByAsset.TryGetValue(assetId, out var tile) && tile.HasThumbnail;
    public bool HasMetadataApplied(Guid assetId) => _tilesByAsset.TryGetValue(assetId, out var tile) && tile.MetadataApplied;

    /// <summary>Updates one tile's thumbnail in place. Never rebuilds rows/visible order or touches unrelated tiles.</summary>
    public void ApplyThumbnail(Guid assetId, string absoluteThumbnailPath)
    {
        if (_tilesByAsset.TryGetValue(assetId, out var tile)) tile.ThumbnailPath = absoluteThumbnailPath;
    }

    /// <summary>
    /// Updates one tile's capture date/duration in place — never rebuilds rows/visible order or moves other
    /// tiles by itself. Returns true when a value the active query could sort by actually changed, so a
    /// caller may choose to coalesce a later <see cref="SetQuery"/> re-application rather than resorting
    /// per-item while metadata streams in from a large folder.
    /// </summary>
    public bool ApplyMetadata(Guid assetId, DateTime? captureDate, double? durationSeconds) =>
        _tilesByAsset.TryGetValue(assetId, out var tile) && tile.ApplyMetadata(captureDate, durationSeconds);

    /// <summary>Updates Catalog-backed presentation state in place without rebuilding rows or thumbnails.</summary>
    public void ApplyAssetState(Guid assetId, BrowserAssetState state)
    {
        if (_tilesByAsset.TryGetValue(assetId, out var tile)) tile.SetAssetState(state);
    }

    public void ApplyAssetStates(IReadOnlyDictionary<Guid, BrowserAssetState> states)
    {
        foreach (var (assetId, state) in states) ApplyAssetState(assetId, state);
    }

    public void SetColumns(int columns)
    {
        columns = Math.Max(1, columns);
        if (columns == _columns) return;
        _columns = columns;
        Rebuild();
    }

    public void SelectSingle(int index) => ApplySelectionChange(() =>
    {
        if (index < 0 || index >= _visibleTiles.Count) return;
        _selection.SelectSingle(_visibleTiles[index].Key, index);
    });

    public void ToggleCtrl(int index) => ApplySelectionChange(() =>
    {
        if (index < 0 || index >= _visibleTiles.Count) return;
        _selection.ToggleCtrl(_visibleTiles[index].Key, index);
    });

    public void SelectRange(int index) => ApplySelectionChange(() =>
    {
        if (index < 0 || index >= _visibleTiles.Count) return;
        var anchor = _selection.AnchorIndex ?? index;
        var low = Math.Min(anchor, index);
        var high = Math.Max(anchor, index);
        var keys = _visibleTiles.Skip(low).Take(high - low + 1).Select(tile => tile.Key).ToArray();
        _selection.SelectRange(keys);
    });

    /// <summary>Selects every currently visible item, replacing any prior selection — matching Ctrl+A over the active filtered/searched view.</summary>
    public void SelectAll() => ApplySelectionChange(() => _selection.SelectAll(_visibleTiles.Select(tile => tile.Key)));

    public void ClearSelection() => ApplySelectionChange(_selection.Clear);

    private void ApplySelectionChange(Action mutate)
    {
        var before = _selection.Snapshot();
        mutate();
        var after = _selection.Snapshot();
        if (before.SetEquals(after)) return;
        // Synced against the full master set, not just the visible projection, so an item hidden by the
        // current filter still carries the correct IsSelected flag if/when the filter later reveals it again.
        foreach (var tile in _allTiles)
        {
            var selected = after.Contains(tile.Key);
            if (tile.IsSelected != selected) tile.IsSelected = selected;
        }
    }

    private void RecomputeVisible()
    {
        _visibleTiles = BrowserQueryEngine.Apply(_allTiles, Query).ToList();
        for (var index = 0; index < _visibleTiles.Count; index++) _visibleTiles[index].Index = index;
        Rebuild();
    }

    private void Rebuild()
    {
        var grouped = BrowserGridLayout.BuildRows(_visibleTiles, _columns);
        for (var index = 0; index < grouped.Count; index++)
        {
            if (index < Rows.Count) Rows[index].SetTiles(grouped[index]);
            else Rows.Add(new BrowserGridRow(grouped[index]));
        }
        while (Rows.Count > grouped.Count) Rows.RemoveAt(Rows.Count - 1);
    }
}

/// <summary>
/// Pure projection over derived-work results, used to decide which completed assets still need their
/// generated thumbnail path applied to the grid. Kept separate from Dispatcher/UI glue for testability.
/// </summary>
internal static class BrowserDerivedWorkProjection
{
    /// <summary>
    /// A thumbnail is worth looking up whenever the scheduler reports the thumbnail component as
    /// <see cref="DerivedWorkComponentOutcome.Succeeded"/> or <see cref="DerivedWorkComponentOutcome.Current"/>
    /// (freshly generated or freshly verified this batch), or <see cref="DerivedWorkComponentOutcome.NotNeeded"/>
    /// — which is what an asset reports when its thumbnail was already current *before* this batch started,
    /// so the scheduler skipped calling the generator at all. That is the common case for any previously-viewed
    /// folder and must still resolve the already-existing cached thumbnail, not just newly-generated ones.
    /// <see cref="DerivedWorkComponentOutcome.Failed"/>, <see cref="DerivedWorkComponentOutcome.SkippedUnavailable"/>,
    /// and <see cref="DerivedWorkComponentOutcome.Canceled"/> have no thumbnail to fetch.
    /// </summary>
    public static IReadOnlyList<Guid> AssetsNeedingThumbnailLookup(
        IReadOnlyList<DerivedWorkItemResult> results, Func<Guid, bool> alreadyHasThumbnail) =>
        results.Where(result => result.Thumbnail is DerivedWorkComponentOutcome.Succeeded or
            DerivedWorkComponentOutcome.Current or DerivedWorkComponentOutcome.NotNeeded)
            .Select(result => result.AssetId)
            .Where(assetId => !alreadyHasThumbnail(assetId))
            .Distinct()
            .ToArray();

    /// <summary>
    /// Mirrors <see cref="AssetsNeedingThumbnailLookup"/> for the metadata component (#109's sort-by-capture-
    /// date/duration data): worth looking up whenever the scheduler reports it Succeeded, Current, or
    /// NotNeeded (already current before this batch started — the common case for any previously-viewed
    /// folder). Failed/SkippedUnavailable/Canceled have no metadata to fetch; already-applied assets are
    /// skipped so a large folder does not repeatedly re-query the Preview store on every progress tick.
    /// </summary>
    public static IReadOnlyList<Guid> AssetsNeedingMetadataLookup(
        IReadOnlyList<DerivedWorkItemResult> results, Func<Guid, bool> alreadyApplied) =>
        results.Where(result => result.Metadata is DerivedWorkComponentOutcome.Succeeded or
            DerivedWorkComponentOutcome.Current or DerivedWorkComponentOutcome.NotNeeded)
            .Select(result => result.AssetId)
            .Where(assetId => !alreadyApplied(assetId))
            .Distinct()
            .ToArray();
}
