using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Waifu2xSimpleGui.App.ViewModels;

namespace Waifu2xSimpleGui.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);
        FileList.DoubleTapped += (_, e) => Compare((e.Source as Avalonia.StyledElement)?.DataContext as FileItemViewModel);
        CompareButton.Click += (_, _) => Compare(FileList.SelectedItem as FileItemViewModel);
    }

    /// <summary>Before/after window for a finished file.</summary>
    private void Compare(FileItemViewModel? item)
    {
        if (DataContext is MainViewModel vm && item is { IsDone: true, OutputPath: { } output })
        {
            PreviewWindow.Open(this, item.Path, output, item.Kind, vm.Snapshot());
        }
    }

    private bool _closeAfterCancel;

    /// <summary>Closing mid-job: cancel first so engine processes are killed and temp is cleaned.</summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeAfterCancel || DataContext is not MainViewModel { IsBusy: true } vm)
        {
            return;
        }

        e.Cancel = true;
        _closeAfterCancel = true;
        try
        {
            await vm.CancelAndWaitAsync(); // override: can't return Task; RunAsync already catches job errors
        }
        finally
        {
            Close();
        }
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm || e.DataTransfer.TryGetFiles() is not { } items)
        {
            return;
        }

        var paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();
        await vm.AddPathsAsync(paths);
    }
}
