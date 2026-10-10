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
/// 两个实现要点：
/// 1. **列宽对齐** —— 表头与每一行共用同一份宽度真源 <see cref="_widths"/>，
///    列边界天然严格对齐，不必像 DataGrid 那样维护额外的列宽同步机制。
/// 2. **虚拟化** —— 行容器用 <see cref="VirtualizingStackPanel"/>，几百条日志也只实例化可视区的行。
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

    private IReadOnlyList<LogColumn> _columnDefs = [];
    private INotifyCollectionChanged? _observedRows;

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
        _rowTemplate = new FuncDataTemplate<object>((_, _) => BuildRow());

        _list = new ListBox
        {
            ItemTemplate = _rowTemplate,
            SelectionMode = SelectionMode.Multiple,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
        };
        _list.ItemsPanel = new FuncTemplate<Panel>(() => new VirtualizingStackPanel());
        // 滚动交给外层 _root 统一控制，ListBox 内部不滚
        _list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _list.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
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

    /// <summary>行源。绑定 <c>ObservableCollection</c> 时自动增量刷新。</summary>
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
    private Control BuildRow()
    {
        var grid = new Grid { ColumnDefinitions = SnapshotColumnDefinitions() };
        var i = 0;
        foreach (var def in _columnDefs)
        {
            var presenter = new ContentPresenter
            {
                ContentTemplate = def.CellTemplate,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = def.CellPadding == default ? new Thickness(8, 0, 8, 0) : def.CellPadding,
            };
            Grid.SetColumn(presenter, i);
            grid.Children.Add(presenter);
            i++;
        }

        var border = new Border
        {
            Height = RowHeight,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0),
            Child = grid,
        };
        ApplySeparatorBrush(border, "Brush.BorderSecondary");
        return border;
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
        RefreshRows();
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
                var grip = new ColumnGrip(i, def.MinWidth, _columns);
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
        RefreshRows();
    }

    private void OnRowsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            RefreshRows();
        });

    /// <summary>
    /// 把 Rows 灌进 ListBox。列宽变化后也走这里 —— 让已实例化的行按新宽度重建
    /// （虚拟化的行容器复用旧宽度，靠重新指定 ItemsSource 强制重建最直接）。
    /// </summary>
    private void RefreshRows()
    {
        _list.ItemsSource = Rows is null ? null : Rows.Cast<object>().ToList();
        ScrollToTail();
    }

    private void ScrollToTail()
    {
        if (!FollowTail) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_list.Items is { Count: > 0 } items)
                _list.ScrollIntoView(items[^1]);
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
            RefreshRows();
        }
    }

    /// <summary>
    /// 表头列右缘的拖拽把手。拖动时直接改写 <c>_columns</c> 里对应的 ColumnDefinition 宽度；
    /// 表头 Grid 绑的是同一批实例故即时生效，新实例化的数据行也从同一真源取值。
    /// </summary>
    private sealed class ColumnGrip : Thumb
    {
        private readonly List<ColumnDefinition> _columns;
        private double _startWidth;
        private double _dragStartX;

        public int ColumnIndex { get; }
        public double MinWidth { get; }

        public ColumnGrip(int columnIndex, double minWidth, List<ColumnDefinition> columns)
        {
            ColumnIndex = columnIndex;
            MinWidth = minWidth;
            _columns = columns;

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
            _columns[ColumnIndex].Width = new GridLength(Math.Max(MinWidth, _startWidth + dx));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            e.Pointer.Capture(null);
        }
    }
}