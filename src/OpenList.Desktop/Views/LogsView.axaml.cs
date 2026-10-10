using Avalonia.Controls;
using OpenList.Desktop.ViewModels;

namespace OpenList.Desktop.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();

        // 本视图由 DataTemplate 生成，构造函数跑的时候 DataContext 还没继承到，
        // 因此挂到 DataContextChanged 上补一次列定义装配。
        DataContextChanged += OnDataContextChanged;
        OnDataContextChanged(this, EventArgs.Empty);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // 单元格模板取自本视图的资源字典，必须在 Avalonia 起来之后才能解析
        if (DataContext is LogsViewModel vm)
            vm.BuildColumns(Resources);
    }
}