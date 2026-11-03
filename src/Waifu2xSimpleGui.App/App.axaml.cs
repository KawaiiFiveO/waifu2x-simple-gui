using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Waifu2xSimpleGui.App.Services;
using Waifu2xSimpleGui.App.ViewModels;
using Waifu2xSimpleGui.App.Views;
using Waifu2xSimpleGui.Core.Settings;

namespace Waifu2xSimpleGui.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Small file, read before the first frame so the saved theme applies without a flash.
            var store = SettingsStore.CreateDefault();
            var settings = Task.Run(() => store.LoadAsync()).GetAwaiter().GetResult();
            MainViewModel.ApplyTheme(settings.LightTheme);
            var window = new MainWindow();
            var vm = new MainViewModel(store, new FileDialogs(window));
            window.DataContext = vm;
            desktop.MainWindow = window;

            // Paths on the command line ("Open with", drag onto the exe) are added to the list.
            var args = desktop.Args ?? [];
            window.Opened += async (_, _) =>
            {
                await vm.InitializeAsync(settings);
                if (args.Length > 0)
                {
                    await vm.AddPathsAsync(args);
                }
            };

            // Exit can't be awaited. Snapshot on the UI thread, then write on the thread pool so the
            // blocking wait can never deadlock against the UI SynchronizationContext.
            desktop.Exit += (_, _) =>
            {
                var current = vm.Snapshot();
                Task.Run(() => vm.SaveAsync(current)).GetAwaiter().GetResult();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
