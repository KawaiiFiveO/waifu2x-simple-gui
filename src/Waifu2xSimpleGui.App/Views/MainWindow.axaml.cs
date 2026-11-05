using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
        CopyLogLines.Click += (_, _) => CopyLog(selectedOnly: true);
        CopyLogAll.Click += (_, _) => CopyLog(selectedOnly: false);
        LogList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control))
            {
                CopyLog(selectedOnly: true);
                e.Handled = true;
            }
        };
    }

    /// <summary>Selected log lines (in log order; all of them when nothing is selected) to the clipboard.</summary>
    private async void CopyLog(bool selectedOnly)
    {
        if (DataContext is not MainViewModel vm || Clipboard is not { } clipboard)
        {
            return;
        }

        // Indexes, not texts: identical lines (same message in the same second) stay distinct.
        var selected = LogList.Selection.SelectedIndexes.Where(i => i < vm.Log.Count).Order().ToList();
        var lines = selectedOnly && selected.Count > 0 ? selected.Select(i => vm.Log[i]) : vm.Log;
        await clipboard.SetTextAsync(string.Join(Environment.NewLine, lines)); // async void: UI event handler
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
