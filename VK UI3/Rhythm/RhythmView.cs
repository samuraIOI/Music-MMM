using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VK_UI3.Helpers;
using VK_UI3.Services;
using Windows.Media.Playback;
using Windows.Storage.Pickers;
using Windows.System;

namespace VK_UI3.Rhythm;

public sealed class RhythmView : UserControl
{
    private readonly CanvasAnimatedControl canvas = new() { IsFixedTimeStep = false, ClearColor = Colors.Transparent };
    private readonly TextBlock status = new() { Text = "Скачайте трек для генерации или выберите сохранённую карту.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .65 }, footer = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Image artwork = new() { Width = 40, Height = 40 };
    private readonly TextBlock controls = new() { Text = "F / J или ЛКМ — центр   ·   D / K или ПКМ — край   ·   Esc — пауза", FontSize = 14, Opacity = .75, TextWrapping = TextWrapping.Wrap };
    private readonly InfoBar toast = new() { IsClosable = true, Severity = InfoBarSeverity.Error, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, MaxWidth = 440 };
    private readonly ComboBox library = new() { PlaceholderText = "Сохранённые карты", MinWidth = 180, DisplayMemberPath = "Title" };
    private readonly Button start = new() { Content = "Играть сначала" }, export = new() { Content = "Скачать для генерации" }, import = new() { Content = "Добавить .osz" };
    private readonly HashSet<VirtualKey> held = new();
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly System.Diagnostics.Stopwatch audioClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly PlaybackClock clock = new();
    private readonly TaikoRenderer renderer = new();
    private readonly object gameGate = new();
    private readonly DispatcherTimer hudTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Expander optionsPanel = new() { Header = "Карты и синхронизация", IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly System.Threading.CancellationTokenSource exportCancellation = new();
    private readonly DispatcherTimer trackTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private float canvasWidth, canvasHeight;
    private double frameTime, duration;
    private bool playbackPaused;
    private readonly MediaPlayer player = MediaPlayerService.MediaPlayer;
    private Beatmap beatmap;
    private IMediaPlaybackSource observedSource;
    private TaikoSession game;
    private StoredMap selected;
    private ExtendedAudio original;
    private string observedTrackId;
    private bool busy, finished, practice;
    private volatile bool running, disposed, preparing;
    private volatile int generation, completedGeneration = -1;
    private double countdownEnd;
    private string gameId;
    private double offset;
    public RhythmView()
    {
        IsTabStop = true;
        var root = new Grid { Padding = new Thickness(12), RowSpacing = 8 };
        root.Background = new SolidColorBrush(Colors.Transparent);
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = new StackPanel { Spacing = 10 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var icons = new[] { "\uE896", "\uE8B5", "\uE768" };
        int iconIndex = 0;
        foreach (var b in new[] { export, import, start })
        {
            var caption = b.Content.ToString();
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, caption);
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(new FontIcon { Glyph = icons[iconIndex++], FontSize = 13 });
            content.Children.Add(new TextBlock { Text = caption });
            b.Content = content; b.Padding = new Thickness(12, 8, 12, 8);
            actions.Children.Add(b);
        }
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accentStyle)) start.Style = (Style)accentStyle;
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        options.Children.Add(library);
        var calibration = new NumberBox { Header = "Смещение, мс", Minimum = -500, Maximum = 500, Value = 0, Width = 150 };
        calibration.ValueChanged += (_, e) => { lock (gameGate) offset = double.IsFinite(e.NewValue) ? e.NewValue : 0; };
        library.Header = "Карта для текущего трека";
        options.Children.Add(calibration); optionsPanel.Content = options;
        toolbar.Children.Add(actions); toolbar.Children.Add(optionsPanel); toolbar.Children.Add(status);
        root.Children.Add(toolbar);
        Grid.SetRow(canvas, 1); root.Children.Add(canvas);
        var timingLabels = new Grid { Width = 300, Height = 24, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 20), IsHitTestVisible = false };
        timingLabels.Children.Add(new TextBlock { Text = "Раньше", FontSize = 13, Opacity = .65, HorizontalAlignment = HorizontalAlignment.Left });
        timingLabels.Children.Add(new TextBlock { Text = "Позже", FontSize = 13, Opacity = .65, HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetRow(timingLabels, 1); root.Children.Add(timingLabels);
        canvas.SizeChanged += (_, e) => timingLabels.Visibility = e.NewSize.Height >= 270 ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(controls, 2); root.Children.Add(controls);
        var bottom = new Grid { ColumnSpacing = 12 }; bottom.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); bottom.ColumnDefinitions.Add(new()); bottom.Children.Add(artwork); Grid.SetColumn(footer, 1); bottom.Children.Add(footer); Grid.SetRow(bottom, 3); root.Children.Add(bottom);
        Grid.SetRowSpan(toast, 4); root.Children.Add(toast); Content = root;
        canvas.Draw += Draw; canvas.Update += Update;
        canvas.SizeChanged += (_, e) => { lock (gameGate) { canvasWidth = (float)e.NewSize.Width; canvasHeight = (float)e.NewSize.Height; } };
        ActualThemeChanged += (_, _) => RefreshPalette();
        hudTimer.Tick += (_, _) => RefreshHud();
        trackTimer.Tick += (_, _) => { trackTimer.Stop(); RefreshTrack(); };
        canvas.PointerPressed += (_, e) => { Focus(FocusState.Programmatic); var p = e.GetCurrentPoint(canvas); if (p.Properties.IsLeftButtonPressed) Strike(false, 0); else if (p.Properties.IsRightButtonPressed) Strike(true, 0); e.Handled = true; };
        export.Click += async (_, _) => await Protect(Export);
        import.Click += async (_, _) => await Protect(Import);
        start.Click += async (_, _) => await Protect(Start);
        LosingFocus += (_, _) => { held.Clear(); };
        toastTimer.Tick += (_, _) => { toast.IsOpen = false; toastTimer.Stop(); };
        Loaded += OnLoaded; Unloaded += (_, _) => Close();
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshPalette(); hudTimer.Start();
        MediaPlayerService.AudioPlayedChangeEvent += TrackChanged;
        VK_UI3.Views.MainView.mainView.KeyDown += OnKeyDown;
        VK_UI3.Views.MainView.mainView.KeyUp += OnKeyUp;
        player.MediaOpened += PlayerOpened;
        player.MediaEnded += PlayerEnded;
        player.PlaybackSession.SeekCompleted += SeekCompleted;
        player.PlaybackSession.PlaybackStateChanged += PlaybackStateChanged;
        MainWindow.mainWindow.Activated += WindowActivated;
        _ = RhythmAnalytics.Write("game_mode_opened", TrackData());
        RefreshTrack();
    }
    private void RefreshPalette()
    {
        // Use Music M's themed accent brush (the same one as the Play button).
        // UISettings alone can return a different Windows system accent.
        var accent = start.Background is SolidColorBrush brush ? brush.Color
            : new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        lock (gameGate) { renderer.Dark = ActualTheme == ElementTheme.Dark; renderer.Accent = accent; }
    }
    private void WindowActivated(object sender, WindowActivatedEventArgs args)
    { if (args.WindowActivationState == WindowActivationState.Deactivated && running && player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) { player.Pause(); held.Clear(); } }
    private void TrackChanged(object sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { if (!disposed) { trackTimer.Stop(); trackTimer.Start(); } });
    private void PlayerOpened(MediaPlayer sender, object args) => TrackChanged(sender, EventArgs.Empty);
    private void PlayerEnded(MediaPlayer sender, object args)
    {
        var version = generation;
        completedGeneration = version;
        DispatcherQueue.TryEnqueue(() => { if (!disposed && generation == version) Finish(true); });
    }
    private void RefreshTrack()
    {
        if (disposed) return;
        var current = MediaPlayerService.PlayingTrack;
        var identity = current == null ? "" : $"{current.audio.OwnerId}_{current.audio.Id}";
        var source = player.Source;
        bool changed = identity != observedTrackId || source != observedSource;
        if (changed)
        {
            Finish(completedGeneration == generation);
            generation++; observedTrackId = identity; observedSource = source;
            selected = null; beatmap = null;
        }
        original = MediaPlayerService.PlayingTrack;
        footer.Text = original == null ? "Выберите трек в Music M" : $"{original.audio.Artist} — {original.audio.Title}";
        var thumb = original?.audio.Album?.Thumb;
        var url = thumb?.Photo300 ?? thumb?.Photo270;
        artwork.Source = url == null ? null : new BitmapImage(new Uri(url.ToString()));
        if (changed)
        {
            var maps = RhythmStorage.Library().Where(m => m.TrackId == TrackId()).OrderBy(m => m.Title).ToList();
            library.ItemsSource = maps;
            var preferred = RhythmStorage.PreferredMap(TrackId());
            library.SelectedItem = maps.FirstOrDefault(m => m.Id == preferred) ?? maps.FirstOrDefault();
            status.Text = maps.Count == 0 ? "Для этого трека пока нет карты. Скачайте аудио для генерации или добавьте .osz." : "Сохранённая карта выбрана. Подготовка к игре…";
        }
        if (!busy && !running && !preparing && selected == null && library.SelectedItem != null && source != null && player.PlaybackSession.NaturalDuration > TimeSpan.Zero)
            _ = Protect(Start);
    }
    private string TrackId() => original == null ? "" : $"{original.audio.OwnerId}_{original.audio.Id}";
    private object TrackData()
    {
        var audio = original ?? MediaPlayerService.PlayingTrack;
        var position = MediaPlayerService.MediaPlayer.PlaybackSession.Position.TotalMilliseconds;
        var duration = MediaPlayerService.MediaPlayer.PlaybackSession.NaturalDuration.TotalMilliseconds;
        return new { track_id = audio == null ? "" : $"{audio.audio.OwnerId}_{audio.audio.Id}", track_title = audio?.audio.Title, artist = audio?.audio.Artist, position_ms = position, duration_ms = duration, position_percent = duration > 0 ? Math.Clamp(position / duration * 100, 0, 100) : (double?)null, playback_context = running ? "game" : "player" };
    }
    private void Acquire() => player.Pause();
    private async Task Protect(Func<Task> operation)
    {
        if (busy || disposed) return;
        var version = generation;
        busy = true; export.IsEnabled = import.IsEnabled = start.IsEnabled = false;
        try { await operation(); }
        catch (Exception e) { if (!disposed && version == generation) { status.Text = "Операция не завершена. Можно повторить попытку."; Error(e.Message); } }
        finally { busy = false; if (!disposed) { export.IsEnabled = import.IsEnabled = start.IsEnabled = true; if (version != generation) RefreshTrack(); } }
    }
    private async Task Export()
    {
        Acquire();
        var track = original; var trackId = TrackId(); var version = generation;
        if (track?.audio.Url == null) throw new InvalidOperationException("Сначала выберите доступный трек в Music M.");
        var title = $"{track.audio.Artist} - {track.audio.Title}";
        var picker = new FileSavePicker { SuggestedFileName = string.Concat(title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)) };
        picker.FileTypeChoices.Add("MP3", new[] { ".mp3" }); picker.FileTypeChoices.Add("Ogg", new[] { ".ogg" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.mainWindow));
        var file = await picker.PickSaveFileAsync(); if (file == null || disposed || version != generation) return;
        status.Text = "Подготовка аудио…";
        var input = track.audio.Url.ToString();
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe");
        if (!File.Exists(ffmpeg)) ffmpeg = new VK_UI3.DownloadTrack.CheckFFmpeg().GetFFmpegPath();
        if (!File.Exists(ffmpeg)) throw new InvalidOperationException("Для экспорта нужен ffmpeg.exe в папке Tools приложения. Библиотек DLL плеера недостаточно.");
        using (var client = AudioExportService.CreateClient())
        {
            try { await new AudioExportService(client).Export(new Uri(input), file.Path, ffmpeg, exportCancellation.Token); }
            catch (Exception e)
            {
                var diagnosticsDirectory = Path.Combine(RhythmStorage.Root, "diagnostics"); Directory.CreateDirectory(diagnosticsDirectory);
                await File.WriteAllTextAsync(Path.Combine(diagnosticsDirectory, "export-last-error.txt"), AudioExportService.Redact(e.ToString()));
                throw;
            }
        }
        if (disposed) return;
        RhythmStorage.SaveExport(new(trackId, await RhythmStorage.Hash(file.Path), file.Path, track.audio.Title));
        if (version == generation) status.Text = "Создайте в Colab taiko-карту (gamemode=1) с этим файлом. Затем добавьте .osz.";
    }
    private async Task Import()
    {
        Acquire();
        var trackId = TrackId(); var version = generation;
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".osz");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.mainWindow));
        var file = await picker.PickSingleFileAsync(); if (file == null || disposed || version != generation) return;
        status.Text = "Проверка карты и аудио…";
        var maps = await RhythmStorage.Import(file.Path, trackId);
        if (disposed || version != generation) return;
        library.ItemsSource = RhythmStorage.Library().Where(m => m.TrackId == TrackId()).ToList(); library.SelectedItem = maps[0];
        // Select the catalog instance rather than a detached imported record.
        library.SelectedItem = ((IEnumerable<StoredMap>)library.ItemsSource).First(m => m.Id == maps[0].Id);
        status.Text = "Карта сохранена. Подготовка к игре…";
        await Start();
    }
    private Task Start()
    {
        var map = library.SelectedItem as StoredMap ?? throw new InvalidOperationException("Выберите сохранённую карту.");
        var expected = RhythmStorage.FindExport(TrackId());
        return StartVerified(map, expected);
    }
    private async Task StartVerified(StoredMap map, ExportedTrack expected)
    {
        var version = generation;
        if (expected == null || await RhythmStorage.Hash(map.AudioPath) != expected.Hash) throw new InvalidDataException("Не удалось подтвердить соответствие аудио.");
        var parsed = Beatmap.Parse(await File.ReadAllTextAsync(map.MapPath));
        if (disposed || version != generation || map.TrackId != TrackId()) return;
        Finish(false);
        selected = map; beatmap = parsed; gameId = Guid.NewGuid().ToString("N"); finished = false; practice = false;
        RhythmStorage.RememberMap(map);
        lock (gameGate)
        {
            player.Pause(); preparing = true; MediaPlayerService.RhythmGameActive = true;
            ResetSession(0);
            // Keep the verified local audio and native transport on ONE player.
            // Register our own source before assignment so its SourceChanged/
            // MediaOpened notifications don't look like a different VK track.
            observedSource = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(map.AudioPath));
            player.Source = observedSource;
            countdownEnd = audioClock.Elapsed.TotalMilliseconds + Math.Max(0, 3000 - beatmap.Notes[0].Time);
        }
        optionsPanel.Visibility = status.Visibility = Visibility.Collapsed;
        _ = RhythmAnalytics.Write("game_started", new { game_session_id = gameId, track_id = TrackId(), track_title = original?.audio.Title, beatmap_id = map.Id, mode = "taiko", ruleset_version = "lazer-77df204-prototype" });
        var reserve = Math.Max(0, 3000 - beatmap.Notes[0].Time);
        if (reserve > 0) await Task.Delay((int)Math.Ceiling(reserve));
        if (!disposed && version == generation && preparing) CompleteStart();
    }
    private void CompleteStart()
    {
        lock (gameGate) { if (!preparing || disposed) return; preparing = false; running = true; clock.Reset(); player.Play(); }
        Focus(FocusState.Programmatic);
    }
    private void ResetSession(double time)
    {
        game = new TaikoSession(beatmap, time); renderer.Reset(beatmap); clock.Reset(); frameTime = time;
        game.Judgement += hit => renderer.Judge(hit, audioClock.Elapsed.TotalMilliseconds);
    }
    private void PlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        // Explicit Play during the countdown starts immediately; standard controls
        // always address the same player, including while the game is preparing.
        if (preparing && sender.PlaybackState == MediaPlaybackState.Playing)
            DispatcherQueue.TryEnqueue(() => { if (preparing) CompleteStart(); });
    }
    private void SeekCompleted(MediaPlaybackSession sender, object args)
    {
        lock (gameGate)
        {
            if (disposed || !running || beatmap == null || player.Source != observedSource) return;
            var time = sender.Position.TotalMilliseconds;
            var previousScore = game.Score;
            practice = time > 250; ResetSession(time);
            _ = RhythmAnalytics.Write("game_seek", new { game_session_id = gameId, track_id = TrackId(), position_ms = time, previous_score = previousScore, score_reset = true, practice });
        }
    }
    private void Finish(bool completed)
    {
        if (game == null || finished || (!running && !preparing)) return;
        lock (gameGate)
        {
            finished = true; running = preparing = false;
            if (completed) game.Advance(double.MaxValue);
            MediaPlayerService.RhythmGameActive = false;
        }
        optionsPanel.Visibility = status.Visibility = Visibility.Visible;
        export.IsEnabled = import.IsEnabled = start.IsEnabled = true;
        status.Text = $"{(practice ? "Тренировка" : completed ? "Результат" : "Прервано")}: {game.Score:N0} · {game.Rank} · {game.Accuracy:P1} · комбо {game.MaxCombo}";
        _ = RhythmAnalytics.Write("game_finished", new { game_session_id = gameId, track_id = TrackId(), beatmap_id = selected.Id, score = game.Score, accuracy = game.Accuracy, rank = game.Rank, practice, status = completed ? "completed" : "aborted" });
    }
    private void TogglePause()
    {
        lock (gameGate)
        {
            if (!running && !preparing) return;
            if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) player.Pause();
            else { clock.Reset(); player.Play(); }
        }
        held.Remove(VirtualKey.D); held.Remove(VirtualKey.F); held.Remove(VirtualKey.J); held.Remove(VirtualKey.K);
    }
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or NumberBox or ComboBox) return;
        if (e.Key == VirtualKey.Escape) { if (held.Add(e.Key)) TogglePause(); e.Handled = true; return; }
        if (!running || e.Key is not (VirtualKey.D or VirtualKey.F or VirtualKey.J or VirtualKey.K)) return;
        if (held.Add(e.Key)) Strike(e.Key is VirtualKey.D or VirtualKey.K, e.Key is VirtualKey.D or VirtualKey.F ? 0 : 1);
        e.Handled = true;
    }
    private void OnKeyUp(object sender, KeyRoutedEventArgs e) => held.Remove(e.Key);
    // Caller holds gameGate. MediaPlaybackSession is agile and can be sampled on
    // Win2D's game thread; XAML properties are only accessed on the UI thread.
    private double GameTime() => clock.Read(player.PlaybackSession.Position.TotalMilliseconds, audioClock.Elapsed.TotalMilliseconds,
        player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) + offset;
    private void Strike(bool rim, int hand)
    {
        lock (gameGate)
        {
            if (disposed) return;
            renderer.Press(rim, audioClock.Elapsed.TotalMilliseconds);
            if (running && player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) game.Strike(GameTime(), rim, hand);
        }
    }
    private void Update(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        lock (gameGate)
        {
            if (disposed) return;
            if (preparing) { frameTime = Math.Min(0, audioClock.Elapsed.TotalMilliseconds - countdownEnd); playbackPaused = false; return; }
            if (!running) return;
            frameTime = GameTime();
            playbackPaused = player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing;
            duration = player.PlaybackSession.NaturalDuration.TotalMilliseconds;
            // Don't turn paused or buffering audio into misses.
            if (!playbackPaused) game.Advance(frameTime);
        }
    }
    private void RefreshHud()
    {
        if (disposed || !running) return;
        double time, length; bool paused;
        lock (gameGate) { time = frameTime; length = duration; paused = playbackPaused; }
        footer.Text = $"{original?.audio.Artist} — {original?.audio.Title} · осталось {TimeSpan.FromMilliseconds(Math.Max(0, length - time)):mm\\:ss}";
        status.Text = paused ? "Пауза · Esc — продолжить" : "Совместите ноту с целью. Кольцо замкнётся в момент удара.";
    }
    private void Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        lock (gameGate)
        {
            if (disposed) return;
            renderer.Draw(args.DrawingSession, canvasWidth, canvasHeight, game, frameTime, audioClock.Elapsed.TotalMilliseconds, running || preparing, playbackPaused, duration);
        }
    }
    private void Error(string message) { toast.Message = message; toast.IsOpen = true; toastTimer.Stop(); toastTimer.Start(); }
    public void Close()
    {
        if (disposed) return;
        _ = RhythmAnalytics.Write("game_mode_closed", new { track = TrackData(), game_session_id = gameId, game_position_ms = player?.PlaybackSession.Position.TotalMilliseconds, game_duration_ms = player?.PlaybackSession.NaturalDuration.TotalMilliseconds });
        // Closing the overlay leaves shared playback at its current position and state.
        if (game != null && !finished) Finish(false); disposed = true; generation++;
        exportCancellation.Cancel(); held.Clear(); toastTimer.Stop(); hudTimer.Stop(); trackTimer.Stop();
        lock (gameGate) { MediaPlayerService.RhythmGameActive = false; renderer.Dispose(); }
        player.MediaOpened -= PlayerOpened; player.MediaEnded -= PlayerEnded;
        player.PlaybackSession.SeekCompleted -= SeekCompleted;
        player.PlaybackSession.PlaybackStateChanged -= PlaybackStateChanged;
        MediaPlayerService.AudioPlayedChangeEvent -= TrackChanged; MainWindow.mainWindow.Activated -= WindowActivated;
        VK_UI3.Views.MainView.mainView.KeyDown -= OnKeyDown;
        VK_UI3.Views.MainView.mainView.KeyUp -= OnKeyUp;
        canvas.RemoveFromVisualTree();
    }
}
