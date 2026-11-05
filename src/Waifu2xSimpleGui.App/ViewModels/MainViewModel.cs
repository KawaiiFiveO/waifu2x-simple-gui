using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Waifu2xSimpleGui.App.Services;
using Waifu2xSimpleGui.Core.Engines;
using Waifu2xSimpleGui.Core.Jobs;
using Waifu2xSimpleGui.Core.Media;
using Waifu2xSimpleGui.Core.Pipelines;
using Waifu2xSimpleGui.Core.Processes;
using Waifu2xSimpleGui.Core.Settings;
using Waifu2xSimpleGui.Core.Tools;

namespace Waifu2xSimpleGui.App.ViewModels;

/// <summary>GPU choice shown in the UI; <see cref="Ids"/> maps to <see cref="AppSettings.GpuIds"/>.</summary>
public sealed record GpuOption(string Label, IReadOnlyList<int> Ids)
{
    public static GpuOption Auto { get; } = new("Auto", []);

    public static GpuOption Cpu { get; } = new("CPU (slow)", [-1]);
}

public sealed record SizeModeChoice(OutputSizeMode Mode, string Label)
{
    public static IReadOnlyList<SizeModeChoice> All { get; } =
        [new(OutputSizeMode.ModelScale, "Model scale"), new(OutputSizeMode.Factor, "Exact factor"), new(OutputSizeMode.Fit, "Fit within")];
}

/// <summary>Frame-rate multiplier choice; 1 = off.</summary>
public sealed record InterpolationChoice(int Factor, string Label)
{
    public static IReadOnlyList<InterpolationChoice> All { get; } =
        [new(1, "Off"), new(2, "2x fps"), new(3, "3x fps"), new(4, "4x fps")];
}

public partial class MainViewModel : ViewModelBase
{
    private const int MaxLogLines = 500;

    private readonly SettingsStore _store;
    private readonly IFileDialogs _dialogs;
    private AppSettings _settings = new();
    private CancellationTokenSource? _runCts;
    private PauseController? _pause;
    private Task _run = Task.CompletedTask;
    private readonly System.Diagnostics.Stopwatch _active = new();
    private readonly Avalonia.Threading.DispatcherTimer _etaClock = new() { Interval = TimeSpan.FromSeconds(1) };
    private EtaEstimator? _eta;
    private List<FileItemViewModel> _runItems = [];

    public MainViewModel(SettingsStore store, IFileDialogs dialogs)
    {
        _store = store;
        _dialogs = dialogs;
        SelectedEngine = UpscalerCatalog.Descriptors[0];
        _etaClock.Tick += (_, _) => UpdateSummary(); // keeps the countdown moving between reports
    }

    public ObservableCollection<FileItemViewModel> Files { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    public ObservableCollection<GpuOption> Gpus { get; } = [GpuOption.Auto, GpuOption.Cpu];

    public IReadOnlyList<UpscalerDescriptor> Engines => UpscalerCatalog.Descriptors;

    public IReadOnlyList<ImageFormat> ImageFormats { get; } = Enum.GetValues<ImageFormat>();

    public IReadOnlyList<AnimationFormat> AnimationFormats { get; } = Enum.GetValues<AnimationFormat>();

    public IReadOnlyList<string> VideoEncoders => VideoCommands.Encoders;

    /// <summary>x264/x265 presets, ProRes profiles, or empty.</summary>
    public IReadOnlyList<string> VideoPresets => VideoCommands.PresetsFor(VideoEncoder);

    public IReadOnlyList<VideoContainer> VideoContainers { get; } = Enum.GetValues<VideoContainer>();

    [ObservableProperty]
    public partial VideoContainer VideoContainer { get; set; }

    [ObservableProperty]
    public partial UpscalerDescriptor SelectedEngine { get; set; }

    [ObservableProperty]
    public partial ModelInfo? SelectedModel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedScale))]
    public partial int Scale { get; set; } = 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedNoiseLevel))]
    public partial int NoiseLevel { get; set; }

    // ComboBox bindings: a ComboBox writes null when its items are replaced; ignore that.
    public int? SelectedScale
    {
        get => Scale;
        set => Scale = value ?? Scale;
    }

    public int? SelectedNoiseLevel
    {
        get => NoiseLevel;
        set => NoiseLevel = value ?? NoiseLevel;
    }

    [ObservableProperty]
    public partial bool Tta { get; set; }

    [ObservableProperty]
    public partial bool LowPriority { get; set; } = true;

    /// <summary><c>null</c> until GPUs are probed, so a saved choice isn't overwritten early.</summary>
    [ObservableProperty]
    public partial GpuOption? SelectedGpu { get; set; }

    [ObservableProperty]
    public partial ImageFormat ImageFormat { get; set; }

    [ObservableProperty]
    public partial AnimationFormat GifOutput { get; set; }

    [ObservableProperty]
    public partial string? OutputDirectory { get; set; }

    [ObservableProperty]
    public partial string FileNameTemplate { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool OverwriteExisting { get; set; }

    [ObservableProperty]
    public partial bool ScanSubfolders { get; set; } = true;

    [ObservableProperty]
    public partial int TileSize { get; set; }

    [ObservableProperty]
    public partial string Threads { get; set; } = ThreadConfig.Default.ToString();

    [ObservableProperty]
    public partial string? TempDirectory { get; set; }

    // SelectedVideoPreset last: the combo re-selects once its new list is in place.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VideoPresets), nameof(HasVideoPreset), nameof(HasVideoQuality), nameof(PresetLabel), nameof(SelectedVideoPreset))]
    public partial string VideoEncoder { get; set; } = "libx264";

    [ObservableProperty]
    public partial int VideoCrf { get; set; }

    [ObservableProperty]
    public partial string VideoPreset { get; set; } = "medium";

    /// <summary>Combo binding: ignores the null a combo writes while its item list is being swapped.</summary>
    public string? SelectedVideoPreset
    {
        get => VideoPreset;
        set => VideoPreset = value ?? VideoPreset;
    }

    [ObservableProperty]
    public partial int VideoSegmentFrames { get; set; }

    public bool HasVideoPreset => VideoPresets.Count > 0;

    /// <summary>ProRes quality comes from its profile.</summary>
    public bool HasVideoQuality => VideoEncoder != VideoCommands.ProRes;

    public string PresetLabel => VideoEncoder == VideoCommands.ProRes ? "Profile" : "Preset";

    partial void OnVideoEncoderChanged(string value)
    {
        var presets = VideoCommands.PresetsFor(value);
        if (presets.Count > 0 && !presets.Contains(VideoPreset))
        {
            VideoPreset = VideoCommands.DefaultPreset(value);
        }
    }

    public IReadOnlyList<SizeModeChoice> SizeModes => SizeModeChoice.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFactorMode), nameof(IsFitMode), nameof(ShowScale), nameof(FactorHint), nameof(CanResizeOnly), nameof(ShowEngineScale))]
    public partial SizeModeChoice SizeMode { get; set; } = SizeModeChoice.All[0];

    /// <summary>Skip the engine; Factor/Fit apply to the inputs as they are.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEngineScale))]
    public partial bool ResizeOnly { get; set; }

    public bool CanResizeOnly => SizeMode.Mode != OutputSizeMode.ModelScale;

    /// <summary>Scale/denoise row: hidden while resizing only.</summary>
    public bool ShowEngineScale => !(ResizeOnly && CanResizeOnly);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FactorHint))]
    public partial double TargetFactor { get; set; } = 3;

    [ObservableProperty]
    public partial int FitWidth { get; set; } = 3840;

    [ObservableProperty]
    public partial int FitHeight { get; set; } = 2160;

    public bool IsFactorMode => SizeMode.Mode == OutputSizeMode.Factor;

    public bool IsFitMode => SizeMode.Mode == OutputSizeMode.Fit;

    public bool ShowScale => !IsFactorMode;

    /// <summary>Which model scale Factor mode will run, e.g. "Engine runs at 4x, then resizes."</summary>
    public string FactorHint
    {
        get
        {
            if (!IsFactorMode || SelectedModel is null)
            {
                return string.Empty;
            }

            var scale = OutputSize.EngineScale(Snapshot());
            return TargetFactor > scale
                ? $"{scale}x (model max), then plain resize"
                : $"{scale}x, then resized";
        }
    }

    public IReadOnlyList<InterpolationChoice> InterpolationChoices => InterpolationChoice.All;

    public IReadOnlyList<RifeModel> RifeModels => RifeNcnnVulkan.Models;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInterpolating))]
    public partial InterpolationChoice Interpolation { get; set; } = InterpolationChoice.All[0];

    [ObservableProperty]
    public partial RifeModel SelectedRifeModel { get; set; } = RifeNcnnVulkan.Models[0];

    [ObservableProperty]
    public partial bool InterpolateOnly { get; set; }

    [ObservableProperty]
    public partial bool VfrPassthrough { get; set; }

    /// <summary>Dark by default; applied app-wide immediately.</summary>
    [ObservableProperty]
    public partial bool LightTheme { get; set; }

    partial void OnLightThemeChanged(bool value) => ApplyTheme(value);

    public static void ApplyTheme(bool light)
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        }
    }

    public bool IsInterpolating => Interpolation.Factor > 1;

    partial void OnInterpolationChanged(InterpolationChoice value) => RefreshEngineStatus();

    [ObservableProperty]
    public partial string? EnginesDirectory { get; set; }

    [ObservableProperty]
    public partial string EngineStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double OverallProgress { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand), nameof(ClearFilesCommand), nameof(TogglePauseCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseLabel))]
    public partial bool IsPaused { get; set; }

    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    public IReadOnlyList<ModelInfo> AvailableModels => SelectedEngine.Models;

    public IReadOnlyList<int> AvailableScales => SelectedModel?.Scales ?? [];

    public IReadOnlyList<int> AvailableNoiseLevels => SelectedModel?.NoiseLevels ?? [];

    public bool HasDenoise => AvailableNoiseLevels.Count > 0;

    /// <param name="settings">Loaded by the app before the window opens (theme applies without a flash).</param>
    public async Task InitializeAsync(AppSettings settings)
    {
        _settings = settings;
        LightTheme = settings.LightTheme;
        SelectedEngine = UpscalerCatalog.Get(_settings.Engine);
        SelectedModel = SelectedEngine.Models.FirstOrDefault(m => m.Name == _settings.Model) ?? SelectedModel;
        Scale = _settings.Scale;
        NoiseLevel = _settings.NoiseLevel;
        CoerceToModel();
        Tta = _settings.Tta;
        LowPriority = _settings.LowPriority;
        ImageFormat = _settings.ImageFormat;
        GifOutput = _settings.GifOutput;
        OutputDirectory = _settings.OutputDirectory;
        FileNameTemplate = _settings.FileNameTemplate;
        OverwriteExisting = _settings.OverwriteExisting;
        ScanSubfolders = _settings.ScanSubfolders;
        TileSize = _settings.TileSize;
        Threads = _settings.Threads;
        TempDirectory = _settings.TempDirectory;
        VideoEncoder = _settings.VideoEncoder;
        VideoCrf = _settings.VideoCrf;
        VideoPreset = _settings.VideoPreset;
        OnVideoEncoderChanged(VideoEncoder); // a saved preset may belong to another encoder
        OnPropertyChanged(nameof(SelectedVideoPreset));
        VideoContainer = _settings.VideoContainer;
        VideoSegmentFrames = _settings.VideoSegmentFrames;
        SizeMode = SizeModeChoice.All.FirstOrDefault(c => c.Mode == _settings.SizeMode) ?? SizeModeChoice.All[0];
        TargetFactor = _settings.TargetFactor;
        FitWidth = _settings.FitWidth;
        FitHeight = _settings.FitHeight;
        ResizeOnly = _settings.ResizeOnly;
        Interpolation = InterpolationChoice.All.FirstOrDefault(c => c.Factor == _settings.InterpolationFactor) ?? InterpolationChoice.All[0];
        SelectedRifeModel = RifeNcnnVulkan.Models.FirstOrDefault(m => m.Name == _settings.RifeModel) ?? RifeNcnnVulkan.Models[0];
        InterpolateOnly = _settings.InterpolateOnly;
        VfrPassthrough = _settings.VfrPassthrough;
        EnginesDirectory = _settings.EnginesDirectory;
        RefreshEngineStatus();

        var temp = _settings.TempDirectory;
        var removed = await Task.Run(() => TempWorkspace.CleanupStale(temp));
        if (removed > 0)
        {
            AppendLog($"Removed {removed} leftover temp folder(s).");
        }

        await ProbeGpusAsync();
    }

    /// <summary>Immutable snapshot of the current UI state; jobs run against this, never against live properties.</summary>
    public AppSettings Snapshot() => _settings with
    {
        Engine = SelectedEngine.Id,
        Model = SelectedModel?.Name,
        Scale = Scale,
        NoiseLevel = NoiseLevel,
        Tta = Tta,
        LowPriority = LowPriority,
        GpuIds = SelectedGpu?.Ids ?? _settings.GpuIds,
        ImageFormat = ImageFormat,
        GifOutput = GifOutput,
        OutputDirectory = NullIfBlank(OutputDirectory),
        FileNameTemplate = string.IsNullOrWhiteSpace(FileNameTemplate) ? new AppSettings().FileNameTemplate : FileNameTemplate,
        OverwriteExisting = OverwriteExisting,
        ScanSubfolders = ScanSubfolders,
        TileSize = Math.Max(0, TileSize),
        Threads = Threads.Trim(),
        TempDirectory = NullIfBlank(TempDirectory),
        VideoEncoder = VideoEncoder,
        VideoCrf = Math.Clamp(VideoCrf, 0, 63),
        VideoPreset = VideoPreset,
        VideoContainer = VideoContainer,
        VideoSegmentFrames = Math.Max(30, VideoSegmentFrames),
        SizeMode = SizeMode.Mode,
        TargetFactor = TargetFactor,
        FitWidth = Math.Max(1, FitWidth),
        FitHeight = Math.Max(1, FitHeight),
        ResizeOnly = ResizeOnly,
        InterpolationFactor = Interpolation.Factor,
        RifeModel = SelectedRifeModel.Name,
        InterpolateOnly = InterpolateOnly,
        VfrPassthrough = VfrPassthrough,
        LightTheme = LightTheme,
        EnginesDirectory = NullIfBlank(EnginesDirectory),
    };

    public Task SaveAsync(AppSettings settings) => _store.SaveAsync(settings);

    /// <summary>Cancels a running job and waits until its processes are gone and temp is cleaned.</summary>
    public async Task CancelAndWaitAsync()
    {
        _runCts?.Cancel();
        await _run;
    }

    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var recurse = ScanSubfolders;
        var input = paths.ToList();
        var outputs = OutputNaming.OutputPattern(FileNameTemplate, ResizeOnly && CanResizeOnly);
        var skipped = 0;
        bool IsEarlierOutput(string file)
        {
            if (outputs?.IsMatch(Path.GetFileName(file)) != true)
            {
                return false;
            }

            Interlocked.Increment(ref skipped);
            return true;
        }

        // Directory walks can be slow (network drives, huge trees): keep them off the UI thread.
        var found = await Task.Run(() => MediaClassifier.Expand(input, recurse, IsEarlierOutput).ToList());
        var existing = Files.Select(f => f.Path).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var added = 0;
        foreach (var path in found.Where(existing.Add))
        {
            Files.Add(new FileItemViewModel(path, MediaClassifier.Classify(path)));
            added++;
        }

        AppendLog($"Added {added} file(s)." + (skipped == 0 ? ""
            : ResizeOnly && CanResizeOnly
                ? $" Skipped {skipped} earlier resize output(s)."
                : $" Skipped {skipped} earlier output(s) — to resize upscaled files, choose Exact factor or Fit within, tick \"Resize only\", then add them again."));
        UpdateSummary();
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    partial void OnSelectedEngineChanged(UpscalerDescriptor value)
    {
        OnPropertyChanged(nameof(AvailableModels));
        SelectedModel = value.GetModel(null);
        RefreshEngineStatus();
    }

    partial void OnSelectedModelChanged(ModelInfo? value)
    {
        OnPropertyChanged(nameof(AvailableScales));
        OnPropertyChanged(nameof(AvailableNoiseLevels));
        OnPropertyChanged(nameof(HasDenoise));
        OnPropertyChanged(nameof(FactorHint));
        CoerceToModel();
        // Re-select after the item lists were swapped.
        OnPropertyChanged(nameof(SelectedScale));
        OnPropertyChanged(nameof(SelectedNoiseLevel));
    }

    partial void OnEnginesDirectoryChanged(string? value) => RefreshEngineStatus();

    private void CoerceToModel()
    {
        if (SelectedModel is null)
        {
            return;
        }

        if (!SelectedModel.Scales.Contains(Scale))
        {
            Scale = SelectedModel.Scales.Contains(2) ? 2 : SelectedModel.Scales[0];
        }

        if (SelectedModel.NoiseLevels.Count > 0 && !SelectedModel.NoiseLevels.Contains(NoiseLevel))
        {
            NoiseLevel = SelectedModel.NoiseLevels[0];
        }
    }

    private void RefreshEngineStatus()
    {
        var tools = new ToolLocator(NullIfBlank(EnginesDirectory));
        var engine = UpscalerCatalog.Create(SelectedEngine.Id, tools.EnginesRoot);
        var ffmpeg = tools.FindFfmpeg();
        EngineStatus =
            (engine.IsInstalled ? "Engine: found" : $"Engine: missing — expected {engine.ExecutablePath}")
            + Environment.NewLine
            + (ffmpeg is null ? "ffmpeg: not found (needed for GIF/video/bmp/tiff)" : $"ffmpeg: {ffmpeg}");
        if (IsInterpolating)
        {
            var rife = RifeNcnnVulkan.Create(tools.EnginesRoot);
            EngineStatus += Environment.NewLine + (rife.IsInstalled ? "RIFE: found" : $"RIFE: missing — expected {rife.ExecutablePath}");
        }
    }

    /// <summary>Asks an installed engine which Vulkan devices exist and fills <see cref="Gpus"/>.</summary>
    private async Task ProbeGpusAsync()
    {
        var root = new ToolLocator(NullIfBlank(EnginesDirectory)).EnginesRoot;
        var engine = UpscalerCatalog.Descriptors
            .Prepend(SelectedEngine)
            .Select(d => UpscalerCatalog.Create(d.Id, root))
            .FirstOrDefault(e => e.IsInstalled);

        IReadOnlyList<EngineGpuDetected> found = [];
        if (engine is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                found = await engine.ProbeGpusAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or System.ComponentModel.Win32Exception)
            {
                AppendLog($"GPU probe failed: {ex.Message}");
            }
        }

        var options = found.Select(g => new GpuOption($"{g.Id}: {g.Name}", [g.Id])).ToList();
        if (found.Count > 1)
        {
            options.Add(new GpuOption($"All {found.Count} GPUs", [.. found.Select(g => g.Id)]));
        }

        Gpus.Clear();
        foreach (var o in options.Prepend(GpuOption.Auto).Append(GpuOption.Cpu))
        {
            Gpus.Add(o);
        }

        SelectedGpu = Gpus.FirstOrDefault(o => o.Ids.SequenceEqual(_settings.GpuIds)) ?? GpuOption.Auto;
        if (found.Count > 0)
        {
            AppendLog($"GPUs: {string.Join(", ", found.Select(g => g.Name))}");
        }
    }

    [RelayCommand]
    private async Task AddFilesAsync() => await AddPathsAsync(await _dialogs.PickFilesAsync());

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Add folder");
        if (folder is not null)
        {
            await AddPathsAsync([folder]);
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync() => OutputDirectory = await _dialogs.PickFolderAsync("Output folder") ?? OutputDirectory;

    [RelayCommand]
    private async Task BrowseTempAsync() => TempDirectory = await _dialogs.PickFolderAsync("Temp folder") ?? TempDirectory;

    [RelayCommand]
    private async Task BrowseEnginesAsync() => EnginesDirectory = await _dialogs.PickFolderAsync("Engines folder") ?? EnginesDirectory;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void ClearFiles()
    {
        Files.Clear();
        UpdateSummary();
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var f in Files.Where(f => f.IsDone).ToList())
        {
            Files.Remove(f);
        }

        UpdateSummary();
    }

    private bool CanStart() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync()
    {
        var settings = Snapshot();
        var error = JobRunner.Validate(settings);
        if (error is not null)
        {
            AppendLog(error);
            return Task.CompletedTask;
        }

        var items = Files.Where(f => !f.IsDone).ToList();
        if (items.Count == 0)
        {
            AppendLog("Nothing to do.");
            return Task.CompletedTask;
        }

        foreach (var item in items)
        {
            item.State = FileState.Pending;
            item.Detail = null;
            item.Progress = 0;
            item.Weight = 1;
        }

        _run = RunAsync(items, settings);
        return _run;
    }

    private async Task RunAsync(List<FileItemViewModel> items, AppSettings settings)
    {
        IsBusy = true;
        _runCts = new CancellationTokenSource();
        _pause = new PauseController();
        var pause = _pause;
        var jobFiles = items.Select((f, i) => new JobFile(i, f.Path, f.Kind)).ToList();
        var progress = new UiProgress<JobEvent>(batch => Apply(items, batch));
        var started = DateTime.Now;
        _runItems = items;
        _eta = new EtaEstimator();
        _active.Restart();
        _etaClock.Start();
        var scale = JobRunner.Resolve(settings).Scale;
        AppendLog(settings.SkipsUpscale
            ? $"Started {items.Count} file(s), resize only: "
                + (settings.SizeMode == OutputSizeMode.Factor ? $"{settings.TargetFactor:0.##}x." : $"fit within {settings.FitWidth}×{settings.FitHeight}.")
            : $"Started {items.Count} file(s) with {SelectedEngine.DisplayName}, "
                + (settings.SizeMode == OutputSizeMode.Factor ? $"{settings.TargetFactor:0.##}x (engine {scale}x)." : $"{scale}x."));
        UpdateSummary();
        try
        {
            // Off the UI thread: staging and engine startup must never block the dispatcher.
            await Task.Run(() => JobRunner.RunAsync(jobFiles, settings, progress, _runCts.Token, pause));
            AppendLog($"Finished in {DateTime.Now - started:hh\\:mm\\:ss}.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($"Job failed: {ex.Message}");
        }
        finally
        {
            progress.Flush();
            foreach (var item in items.Where(i => i.State is FileState.Pending or FileState.Processing))
            {
                item.State = FileState.Cancelled;
            }

            _runCts.Dispose();
            _runCts = null;
            _pause = null;
            _eta = null;
            _etaClock.Stop();
            _active.Reset();
            IsPaused = false;
            IsBusy = false;
            UpdateSummary();
        }
    }

    private void Apply(List<FileItemViewModel> items, IReadOnlyList<JobEvent> batch)
    {
        foreach (var ev in batch)
        {
            switch (ev)
            {
                case FileUpdate u:
                    var item = items[u.FileId];
                    item.State = u.State;
                    item.Detail = u.Detail;
                    item.OutputPath = u.OutputPath ?? item.OutputPath;
                    if (u.State == FileState.Failed)
                    {
                        AppendLog($"{item.Name}: {u.Detail}");
                    }

                    break;
                case FileProgress p:
                    items[p.FileId].Progress = p.Percent;
                    break;
                case JobLog log:
                    AppendLog(log.Message);
                    break;
                case FileWeight w:
                    items[w.FileId].Weight = Math.Max(1, w.Weight);
                    break;
            }
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int done = 0, failed = 0, skipped = 0;
        foreach (var f in Files)
        {
            switch (f.State)
            {
                case FileState.Done: done++; break;
                case FileState.Failed: failed++; break;
                case FileState.Skipped: skipped++; break;
            }
        }

        var total = Files.Count;
        var fraction = RunFraction();
        OverallProgress = fraction is { } running ? 100 * running : total == 0 ? 0 : 100.0 * (done + failed + skipped) / total;
        Summary = total == 0 ? string.Empty : $"{done}/{total} done" + (failed > 0 ? $", {failed} failed" : "") + (skipped > 0 ? $", {skipped} skipped" : "")
            + (IsPaused ? " — paused" : Eta(fraction));
    }

    /// <summary>
    /// Weighted progress of the running job (videos by frame count). Skipped files are excluded: they finish
    /// instantly and would make the first estimate far too low.
    /// </summary>
    private double? RunFraction()
    {
        if (_eta is null)
        {
            return null;
        }

        double finished = 0, work = 0;
        foreach (var f in _runItems)
        {
            if (f.State == FileState.Skipped)
            {
                continue;
            }

            work += f.Weight;
            finished += f.Weight * f.State switch
            {
                FileState.Done or FileState.Failed => 1,
                FileState.Processing => f.Progress / 100,
                _ => 0,
            };
        }

        return work == 0 ? null : finished / work;
    }

    private string Eta(double? fraction)
    {
        if (_eta is null || fraction is not { } f)
        {
            return "";
        }

        _eta.Report(f, _active.Elapsed);
        return " — " + EtaEstimator.Format(_eta.Remaining(_active.Elapsed));
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _runCts?.Cancel();

    /// <summary>Suspends the running engine/ffmpeg processes (GPU and CPU go idle) or resumes them.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void TogglePause()
    {
        if (_pause is null)
        {
            return;
        }

        if (_pause.IsPaused)
        {
            _pause.Resume();
        }
        else
        {
            _pause.Pause();
        }

        IsPaused = _pause.IsPaused;
        if (IsPaused)
        {
            _active.Stop();
            _etaClock.Stop();
        }
        else
        {
            _active.Start();
            _etaClock.Start();
        }

        AppendLog(IsPaused ? "Paused." : "Resumed.");
        UpdateSummary();
    }

    private void AppendLog(string message)
    {
        Log.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (Log.Count > MaxLogLines)
        {
            Log.RemoveAt(0);
        }
    }
}
