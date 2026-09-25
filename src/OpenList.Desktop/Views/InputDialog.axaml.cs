using System.Threading.Tasks;
using Avalonia.Controls;

namespace OpenList.Desktop.Views;

public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
    }

    public static Task<string?> ShowAsync(Window owner, string title, string label, string? defaultValue)
    {
        var dlg = new InputDialog
        {
            Title = title,
        };
        dlg.LabelText.Text = label;
        dlg.ValueBox.Text = defaultValue ?? "";
        dlg.Opened += (_, _) =>
        {
            dlg.ValueBox.Focus();
            dlg.ValueBox.SelectAll();
        };
        return dlg.ShowDialog<string?>(owner);
    }

    private void OnOk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(ValueBox.Text);
    }
}
