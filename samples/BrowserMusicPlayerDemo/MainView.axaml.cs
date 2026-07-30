using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace BrowserMusicPlayerDemo;

public sealed partial class MainView : UserControl, IDisposable
{
    private readonly MainViewModel viewModel = new();
    private bool disposed;

    public MainView()
    {
        InitializeComponent();
        DataContext = viewModel;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        => viewModel.TopLevel = TopLevel.GetTopLevel(this);

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        => Dispose();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        AttachedToVisualTree -= OnAttachedToVisualTree;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
        viewModel.Dispose();
    }
}
