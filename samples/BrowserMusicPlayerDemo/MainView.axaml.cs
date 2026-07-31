using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace BrowserMusicPlayerDemo;

public sealed partial class MainView : UserControl, IDisposable
{
    private readonly MainViewModel viewModel = new();
    private readonly Action<TimeSpan> animationFrameCallback;
    private TopLevel? topLevel;
    private bool disposed;

    public MainView()
    {
        animationFrameCallback = OnAnimationFrame;
        InitializeComponent();
        DataContext = viewModel;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        topLevel = TopLevel.GetTopLevel(this);
        viewModel.TopLevel = topLevel;
        topLevel?.RequestAnimationFrame(animationFrameCallback);
    }

    private void OnAnimationFrame(TimeSpan _)
    {
        if (disposed || topLevel == null)
        {
            return;
        }

        viewModel.RefreshConsumed();
        topLevel.RequestAnimationFrame(animationFrameCallback);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        => Dispose();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        topLevel = null;
        AttachedToVisualTree -= OnAttachedToVisualTree;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
        viewModel.Dispose();
    }
}
