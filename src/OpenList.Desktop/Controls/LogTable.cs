using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace OpenList.Desktop.Controls;

/// <summary>日志表格的一列定义。</summary>
/// <param name="HeaderKey">表头本地化资源键（完整键名，如 <c>L.log.colTime</c>）。</param>
/// <param name="CellTemplate">单元格内容模板，DataContext 为行 VM。</param>
/// <param name="DefaultWidth">初始像素宽度。</param>
/// <param name="Star">星号权重，仅消息列传 7，其余为 0。</param>
/// <param name="MinWidth">拖拽调宽时的下限。</param>
/// <param name="Resizable">是否允许拖拽调宽。星号列铺满剩余空间，不参与。</param>
/// <param name="CellPadding">单元格内边距。</param>
public sealed record LogColumn(
    string HeaderKey,
    IDataTemplate CellTemplate,
    double DefaultWidth,
    double Star = 0,
    double MinWidth = 40,
    bool Resizable = true,
    Thickness CellPadding = default)
{
    /// <summary>星号列不拖拽（宽度由剩余空间决定）。</summary>
    public bool Draggable => Resizable && Star <= 0;
}

/// <summary>
/// 纯只读日志表格，替代 <c>Avalonia.Controls.DataGrid</c>。
///
/// 原表格只用到只读展示（IsReadOnly、不排序、不编辑），DataGrid 的编辑、排序、列类型体系
/// 全都用不上，却把整个 DataGrid 依赖拖进桌面端。这里按实际需求重做一个够用的：
/// 表头 + 虚拟化行 + 水平分隔线 + 拖拽调宽。
///
/// 三个实现要点：
/// 1. **列宽对齐** —— 表头与每一行共用同一份宽度真源 <see cref="_columns"/>，
///    列边界天然严格对齐，不必像 DataGrid 那样维护额外的列宽同步机制。
/// 2. **虚拟化** —— 行容器用 <see cref="VirtualizingStackPanel"/>，几百条日志也只实例化可视区的行。
/// 3. **滚动归 ListBox 自己管** —— 表格内容会超出可视区，纵向滚动必须留在 ListBox 内部的
///    ScrollViewer（表头在它外面，天然固定）。早先版本把内部滚动一并禁掉、又没在外层补
///    ScrollViewer，结果超出可视区的日志既滚不到、也追不到尾部，看上去就是"日志不刷新"。
/// </summary>
public class LogTable : UserControl
{
    private const double HeaderHeight = 30;
    private const double DefaultRowHeight = 26;
    private const double SplitterWidth = 6;

    private readonly Grid _root;
    private readonly Grid _headerRow;
    private readonly ListBox _list;
    private readonly FuncDataTemplate<object> _rowTemplate;

    /// <summary>列宽的唯一真源：表头 Grid 与每个数据行 Grid 都从这里取宽度。</summary>
    private readonly List<ColumnDefinition> _columns = [];

    private readonly List<TextBlock> _headerCells = [];

    /// <summary>已实例化的行（虚拟化后只有可视区那几行）。拖拽调宽时逐个同步，不必整表重建。</summary>
    private readonly HashSet<LogRow> _liveRows = [];

    private IReadOnlyList<LogColumn> _columnDefs = [];
    private INotifyCollectionChanged? _observedRows;

    /// <summary>同一轮 UI 消息里可能追加多条日志，尾部滚动只排一次。</summary>
    private bool _tailScrollQueued;

    public static readonly StyledProperty<System.Collections.IEnumerable?> RowsProperty =
        AvaloniaProperty.Register<LogTable, System.Collections.IEnumerable?>(nameof(Rows));

    public static readonly StyledProperty<IReadOnlyList<LogColumn>?> ColumnsProperty =
        AvaloniaProperty.Register<LogTable, IReadOnlyList<LogColumn>?>(nameof(Columns));

    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<LogTable, double>(nameof(RowHeight), DefaultRowHeight);

    public static readonly StyledProperty<bool> FollowTailProperty =
        AvaloniaProperty.Register<LogTable, bool>(nameof(FollowTail));

    static LogTable()
    {
        AffectsMeasure<LogTable>(ColumnsProperty, RowHeightProperty);
        AffectsArrange<LogTable>(ColumnsProperty, RowHeightProperty);
    }

    public LogTable()
    {
        // 每一行 = Border（画下边框当分隔线）+ 内层 Grid（按列宽铺单元格）
        _rowTemplate = new FuncDataTemplate<object>((data, _) => BuildRow(data));

        _list = new ListBox
        {
            ItemTemplate = _rowTemplate,
            SelectionMode = SelectionMode.Multiple,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
        };
        _list.ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        // 行本来就按表格宽度铺满，横向不需要滚；纵向保持可滚（默认 Auto，带滚动条便于回看）
        _list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        // 视图是在日志已经堆了一批之后才被创建/切换回来的（点进日志页时
        // Snapshot 早已灌满行源），这时 `Scroll` 要等 ListBox 模板套用后才可用，
        // 所以在挂上可视树时补一次尾部定位
        _list.AttachedToVisualTree += (_, _) => QueueTailScroll();
        // 默认选中底色过于抢眼，日志表格只要一个很淡的提示
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(PaddingProperty, new Thickness(0)),
                new Setter(MarginProperty, new Thickness(0)),
                new Setter(BackgroundProperty, Brushes.Transparent),
            },
        });

        _headerRow = new Grid { Height = HeaderHeight };

        var headerBorder = new Border
        {
            Child = _headerRow,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0),
        };
        ApplySeparatorBrush(headerBorder, "Brush.BorderSecondary");

        _root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        _root.Children.Add(headerBorder);
        _root.Children.Add(_list);
        Grid.SetRow(headerBorder, 0);
        Grid.SetRow(_list, 1);
        Content = _root;
    }

    /// <summary>行源。绑定 <c>ObservableCollection</c> 时由 ListBox 直接消费，增删即时生效。</summary>
    public System.Collections.IEnumerable? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    /// <summary>列定义。</summary>
    public IReadOnlyList<LogColumn>? Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>行高（须与实际内容高度一致，虚拟化按行高回收）。</summary>
    public double RowHeight
    {
        get => GetValue(RowHeightProperty);
        set => SetValue(RowHeightProperty, value);
    }

    /// <summary>内容变化时是否滚到底部（日志实时追加场景）。</summary>
    public bool FollowTail
    {
        get => GetValue(FollowTailProperty);
        set => SetValue(FollowTailProperty, value);
    }

    /// <summary>
    /// 把分隔线画刷绑到主题资源。Avalonia 12 没有 <c>SetResourceReference</c>，
    /// 用 DynamicResource 表达式绑定（随语言/主题切换自动刷新）。
    /// </summary>
    private static void ApplySeparatorBrush(Border border, string resourceKey) =>
        border.Bind(Border.BorderBrushProperty, new DynamicResourceExtension(resourceKey));

    /// <summary>构造一行：按当前列宽铺单元格。</summary>
    /// <param name="data">行 VM，交给每个单元格的 ContentPresenter 当内容
    /// （ContentPresenter 只把 <c>Content</c> 交给 ContentTemplate 当 DataContext，
    /// 光给 ContentTemplate 不给 Content，单元格模板会绑在空 DataContext 上显示空白）。</param>
    private Control BuildRow(object? data)
    {
        var grid = new Grid { ColumnDefinitions = SnapshotColumnDefinitions() };
        var i = 0;
        foreach (var def in _columnDefs)
        {
            var presenter = new ContentPresenter
            {
                Content = data,
                ContentTemplate = def.CellTemplate,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = def.CellPadding == default ? new Thickness(8, 0, 8, 0) : def.CellPadding,
            };
            Grid.SetColumn(presenter, i);
            grid.Children.Add(presenter);
            i++;
        }

        var row = new LogRow(grid)
        {
            Height = RowHeight,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0),
        };
        ApplySeparatorBrush(row, "Brush.BorderSecondary");

        // 只在挂上可视树期间登记，虚拟化回收后自动出栈，不会留住已丢弃的行
        row.AttachedToVisualTree += (_, _) => _liveRows.Add(row);
        row.DetachedFromVisualTree += (_, _) => _liveRows.Remove(row);
        return row;
    }

    /// <summary>复制一份列宽给某个 Grid，避免多行共享同一个 ColumnDefinitions 实例。</summary>
    private ColumnDefinitions SnapshotColumnDefinitions()
    {
        var defs = new ColumnDefinitions();
        foreach (var c in _columns)
            defs.Add(new ColumnDefinition(c.Width));
        return defs;
    }

    private void RebuildColumns()
    {
        _columnDefs = Columns ?? [];
        _columns.Clear();
        foreach (var def in _columnDefs)
            _columns.Add(new ColumnDefinition(def.Star > 0
                ? new GridLength(def.Star, GridUnitType.Star)
                : new GridLength(def.DefaultWidth)));

        BuildHeader();

        // 列的数量/种类变了，已实例化的行必须按新列重建；重新指定 ItemsSource 是让
        // 虚拟化容器重新走一遍 ItemTemplate 最直接的办法（只在列定义变化时发生，
        // 不在日志追加路径上）。单纯拖拽调宽走 SyncLiveRowWidths，不做重建。
        _list.ItemsSource = null;
        _list.ItemsSource = Rows;
        QueueTailScroll();
    }

    private void BuildHeader()
    {
        _headerRow.Children.Clear();
        _headerRow.ColumnDefinitions = new ColumnDefinitions();
        _headerCells.Clear();

        foreach (var def in _columnDefs)
        {
            var i = _headerRow.ColumnDefinitions.Count;
            _headerRow.ColumnDefinitions.Add(new ColumnDefinition(_columns[i].Width));

            var cell = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = def.CellPadding == default ? new Thickness(8, 0, 8, 0) : def.CellPadding,
            };
            // 表头走 DynamicResource：语言切换后自动刷新，无需重建
            cell.Bind(TextBlock.TextProperty, new DynamicResourceExtension(def.HeaderKey));
            Grid.SetColumn(cell, i);
            _headerRow.Children.Add(cell);
            _headerCells.Add(cell);

            // 拖拽把手：盖在列右缘
            if (def.Draggable)
            {
                var grip = new ColumnGrip(i, def.MinWidth, _columns, SyncLiveRowWidths);
                Grid.SetColumn(grip, i);
                _headerRow.Children.Add(grip);
            }
        }
    }

    private void SyncRows()
    {
        if (_observedRows is not null)
        {
            _observedRows.CollectionChanged -= OnRowsCollectionChanged;
            _observedRows = null;
        }

        if (Rows is INotifyCollectionChanged obs)
        {
            _observedRows = obs;
            obs.CollectionChanged += OnRowsCollectionChanged;
        }

        // 直接把行源交给 ListBox：ObservableCollection 的增删由它自己增量处理。
        // 不要每次变化都灌一份 ToList() 快照 —— 那会整表重建容器，滚动位置和选中都会丢失。
        _list.ItemsSource = null;
        _list.ItemsSource = Rows;
        QueueTailScroll();
    }

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        QueueTailScroll();

    /// <summary>表头列宽变化后，同步到已实例化的行（其余行在实例化时自然取新宽度）。</summary>
    private void SyncLiveRowWidths()
    {
        foreach (var row in _liveRows)
            row.SyncWidths(_columns);
    }

    private void ApplyRowHeight()
    {
        foreach (var row in _liveRows)
            row.Height = RowHeight;
    }

    /// <summary>
    /// 滚到底部。合并同一轮消息里的多次请求；排到 Background 优先级，
    /// 此时布局已完成、ScrollViewer 的 Extent 是包含新行的最终值。
    /// </summary>
    private void QueueTailScroll()
    {
        if (_tailScrollQueued || !FollowTail) return;
        _tailScrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _tailScrollQueued = false;
            if (!FollowTail || _list.Scroll is not { } scroll) return;
            if (scroll is ScrollViewer viewer)
            {
                viewer.ScrollToEnd();
                return;
            }
            // 非 ScrollViewer 的 IScrollable（极少见）：按 extent/viewport 直接算到底部
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        }, DispatcherPriority.Background);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColumnsProperty)
        {
            RebuildColumns();
        }
        else if (change.Property == RowsProperty)
        {
            SyncRows();
        }
        else if (change.Property == RowHeightProperty)
        {
            ApplyRowHeight();
        }
        else if (change.Property == FollowTailProperty && FollowTail)
        {
            // 从"暂停跟随"切回来时立刻追一次尾部
            QueueTailScroll();
        }
    }

    /// <summary>一行：外圈 Border 负责分隔线与行高，内层 Grid 按列宽铺单元格。</summary>
    private sealed class LogRow : Border
    {
        private readonly ColumnDefinitions _columns;

        public LogRow(Grid layout)
        {
            _columns = layout.ColumnDefinitions;
            Child = layout;
        }

        /// <summary>把表头当前的列宽同步到本行（拖拽调宽时调用）。</summary>
        public void SyncWidths(List<ColumnDefinition> source)
        {
            for (var i = 0; i < _columns.Count && i < source.Count; i++)
            {
                if (!_columns[i].Width.Equals(source[i].Width))
                    _columns[i].Width = source[i].Width;
            }
        }
    }

    /// <summary>
    /// 表头列右缘的拖拽把手。拖动时直接改写 <c>_columns</c> 里对应的 ColumnDefinition 宽度；
    /// 表头 Grid 绑的是同一批实例故即时生效，已实例化的行由回调同步，新行从同一真源取值。
    /// </summary>
    private sealed class ColumnGrip : Thumb
    {
        private readonly List<ColumnDefinition> _columns;
        private readonly Action _widthsChanged;
        private double _startWidth;
        private double _dragStartX;

        public int ColumnIndex { get; }
        /// <summary>拖拽时的宽度下限（不叫 MinWidth：那是 Layoutable 的排版属性，会遮蔽它）。</summary>
        public double MinColumnWidth { get; }

        public ColumnGrip(int columnIndex, double minWidth, List<ColumnDefinition> columns, Action widthsChanged)
        {
            ColumnIndex = columnIndex;
            MinColumnWidth = minWidth;
            _columns = columns;
            _widthsChanged = widthsChanged;

            Width = SplitterWidth + 4;
            Background = Brushes.Transparent;
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            Cursor = new Cursor(StandardCursorType.SizeWestEast);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (ColumnIndex >= _columns.Count) return;

            var width = _columns[ColumnIndex].Width;
            _startWidth = width.Value;
            _dragStartX = e.GetPosition(this).X;
            e.Pointer.Capture(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (ColumnIndex >= _columns.Count) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            var dx = e.GetPosition(this).X - _dragStartX;
            _columns[ColumnIndex].Width = new GridLength(Math.Max(MinColumnWidth, _startWidth + dx));
            _widthsChanged();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            e.Pointer.Capture(null);
        }
    }
}
