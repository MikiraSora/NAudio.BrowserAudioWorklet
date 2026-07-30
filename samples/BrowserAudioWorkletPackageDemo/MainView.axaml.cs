using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace BrowserAudioWorkletPackageDemo;

public sealed partial class MainView : UserControl, IDisposable
{
    private readonly MainViewModel viewModel = new();
    private bool disposed;

    public MainView()
    {
        InitializeComponent();
        DataContext = viewModel;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        => Dispose();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
        viewModel.Dispose();
    }
}
