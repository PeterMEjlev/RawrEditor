using System.Windows;
using Rawr.Develop;
using Rawr.Editor.App.ViewModels;

namespace Rawr.Editor.App;

/// <summary>
/// Modal "Export" panel (Image Type / Dimensions / Bit Depth / Compression /
/// Quality / Color Space). On OK it exposes the chosen <see cref="ExportSettings"/>
/// via <see cref="Result"/>; the caller handles the file picker and the actual
/// full-resolution render so this dialog stays purely about the choices.
/// </summary>
public partial class ExportDialog : Window
{
    private readonly ExportDialogViewModel _vm;

    /// <summary>The chosen settings — valid only when <c>ShowDialog()</c> returned true.</summary>
    public ExportSettings? Result { get; private set; }

    public ExportDialog(ExportSettings seed)
    {
        InitializeComponent();
        _vm = new ExportDialogViewModel(seed);
        DataContext = _vm;
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        Result = _vm.BuildSettings();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
