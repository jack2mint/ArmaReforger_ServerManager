using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using ForgeManager.Models;
using ForgeManager.Services;

namespace ForgeManager;

public partial class MainWindow : Window
{
    private const string CieUdachneModId = "6530BECA1273AEAB";
    private const string CieUdachneName = "CIE Udachne";
    private const int MaxLogEntries = 12000;
    private const int MaxKillFeedEntries = 2000;
    private const int MaxAdminEventEntries = 5000;
    private const int MaxTerminalCharacters = 2_000_000;
    private const int MaxOutputBatch = 600;

    private readonly ConcurrentQueue<(string Line, bool ErrorStream)> _pendingOutput = new();
    private readonly ObservableCollection<LogEntry> _logs = [];
    private readonly ObservableCollection<WorkshopMod> _workshopMods = [];
    private readonly ObservableCollection<WorkshopMod> _availableWorkshopMods = [];
    private readonly ObservableCollection<KillFeedEntry> _killFeed = [];
    private readonly ObservableCollection<AdminEventEntry> _adminEvents = [];
    private readonly ObservableCollection<ActivePlayer> _activePlayers = [];
    private readonly ServerProcessService _serverProcess = new();
    private readonly ConfigService _configService = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly WorkshopService _workshopService = new();
    private readonly ServerEventParser _serverEventParser = new();
    private readonly SteamCmdService _steamCmdService = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _outputFlushTimer;
    private readonly ICollectionView _logView;
    private readonly ICollectionView _modsView;
    private readonly ICollectionView _availableModsView;

    private AppSettings _settings = new();
    private ServerConfig? _config;
    private bool _loaded;
    private bool _restarting;
    private CancellationTokenSource? _workshopCts;
    private CancellationTokenSource? _steamCmdCts;
    private int _workshopBrowsePage = 1;
    private int _terminalCharacterCount;
    private int _statusTickCount;
    private bool _isFlushingOutput;
    private string _lanAddress = "Unavailable";
    private bool _compactNavigation;

    public MainWindow()
    {
        InitializeComponent();

        _logView = CollectionViewSource.GetDefaultView(_logs);
        _logView.Filter = FilterLogEntry;
        LogDataGrid.ItemsSource = _logView;

        _modsView = CollectionViewSource.GetDefaultView(_workshopMods);
        _modsView.Filter = FilterMod;
        ModsItemsControl.ItemsSource = _modsView;

        _availableModsView = CollectionViewSource.GetDefaultView(_availableWorkshopMods);
        AvailableWorkshopItemsControl.ItemsSource = _availableModsView;
        KillFeedDataGrid.ItemsSource = _killFeed;
        AdminEventsDataGrid.ItemsSource = _adminEvents;
        ActivePlayersDataGrid.ItemsSource = _activePlayers;

        _serverProcess.OutputReceived += ServerProcess_OutputReceived;
        _serverProcess.ProcessExited += ServerProcess_ProcessExited;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();

        _outputFlushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _outputFlushTimer.Tick += OutputFlushTimer_Tick;
        _outputFlushTimer.Start();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await _settingsService.LoadAsync();
            PopulateSettingsControls();
            AutoRestartCheckBox.IsChecked = _settings.AutoRestart;
            AutoDetectJoinAddressCheckBox.IsChecked = _settings.AutoDetectLocalJoinAddress;
            AppModeText.Text = _settingsService.IsPortableMode
                ? $"Drop-in mode · {_settingsService.PortableServerRoot}"
                : "Configured mode · paths stored in LocalAppData";
            _lanAddress = NetworkService.GetLanIpv4Address();
            LanAddressText.Text = _lanAddress;
            await LoadConfigAsync(showMessage: false);

            if (_serverProcess.TryAttachToExisting(_settings))
            {
                AppendManagerLog($"Attached to existing Arma Reforger server process (PID {_serverProcess.ProcessId}).");
                var listeners = NetworkService.GetUdpListeners(_config?.BindPort ?? 2001);
                var isListening = listeners.Count > 0;
                SetServerStatus(isListening ? "RUNNING" : "STARTING",
                    isListening ? FindBrush("StatusSuccess", Brushes.LimeGreen) : Brushes.Gold);
            }

            UpdateProcessButtons();
            ApplyResponsiveLayout(ActualWidth);
            UpdateSectionHeader(MainTabs.SelectedIndex);
            UpdateWindowChrome();
            _loaded = true;
        }
        catch (Exception ex)
        {
            ShowError("Initialization failed", ex);
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_serverProcess.IsRunning && _serverProcess.IsExternalInstance)
        {
            // The manager did not create this process. Closing only detaches and leaves the BAT-started
            // server running exactly as it was.
            _serverProcess.Detach();
        }
        else if (_serverProcess.IsRunning)
        {
            var result = MessageBox.Show(
                "The server is still running. Force-stop it and close the manager?",
                "Server still running",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _serverProcess.ForceStop();
        }

        _workshopCts?.Cancel();
        _steamCmdCts?.Cancel();
        _steamCmdService.Cancel();
        _statusTimer.Stop();
        _outputFlushTimer.Stop();
        _workshopService.Dispose();
        _steamCmdService.Dispose();
        _serverProcess.Dispose();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // A mouse-up can race the drag request. WPF safely ignores that case.
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized
        ? WindowState.Normal
        : WindowState.Maximized;

    private void MainWindow_StateChanged(object? sender, EventArgs e) => UpdateWindowChrome();

    private void UpdateWindowChrome()
    {
        if (WindowFrame is null || MaximizeWindowButton is null)
            return;

        var maximized = WindowState == WindowState.Maximized;
        WindowFrame.Margin = maximized ? new Thickness(0) : new Thickness(8);
        WindowFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(14);
        MaximizeWindowButton.Content = maximized ? "❐" : "□";
        MaximizeWindowButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        if (MainTabs is null || DashboardKpiGrid is null || DashboardDetailsGrid is null)
            return;

        var compactNavigation = width < 1180;
        if (_compactNavigation != compactNavigation)
        {
            _compactNavigation = compactNavigation;
            MainTabs.TabStripPlacement = compactNavigation ? Dock.Top : Dock.Left;
        }

        DashboardKpiGrid.Columns = width switch
        {
            < 760 => 1,
            < 1240 => 2,
            _ => 4
        };

        var stackedDetails = width < 1080;
        if (stackedDetails)
        {
            DashboardDetailsGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            DashboardDetailsGrid.ColumnDefinitions[1].Width = new GridLength(0);
            DashboardDetailsGrid.RowDefinitions[0].Height = GridLength.Auto;
            DashboardDetailsGrid.RowDefinitions[1].Height = GridLength.Auto;
            Grid.SetColumn(ActiveConfigCard, 0);
            Grid.SetRow(ActiveConfigCard, 0);
            ActiveConfigCard.Margin = new Thickness(0, 0, 0, 10);
            Grid.SetColumn(DiagnosticsCard, 0);
            Grid.SetRow(DiagnosticsCard, 1);
            DiagnosticsCard.Margin = new Thickness(0);
        }
        else
        {
            DashboardDetailsGrid.ColumnDefinitions[0].Width = new GridLength(1.45, GridUnitType.Star);
            DashboardDetailsGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            DashboardDetailsGrid.RowDefinitions[0].Height = GridLength.Auto;
            DashboardDetailsGrid.RowDefinitions[1].Height = new GridLength(0);
            Grid.SetColumn(ActiveConfigCard, 0);
            Grid.SetRow(ActiveConfigCard, 0);
            ActiveConfigCard.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(DiagnosticsCard, 1);
            Grid.SetRow(DiagnosticsCard, 0);
            DiagnosticsCard.Margin = new Thickness(6, 0, 0, 0);
        }

        HeaderHeroPanel.Visibility = width < 900 ? Visibility.Collapsed : Visibility.Visible;
        HeaderDescriptionText.Visibility = width < 1320 ? Visibility.Collapsed : Visibility.Visible;
        ShortcutHintText.Visibility = width < 1060 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs))
            return;

        UpdateSectionHeader(MainTabs.SelectedIndex);
    }

    private void UpdateSectionHeader(int index)
    {
        if (CurrentSectionTitle is null || CurrentSectionSubtitle is null)
            return;

        (CurrentSectionTitle.Text, CurrentSectionSubtitle.Text) = index switch
        {
            0 => ("Dashboard", "Live server overview and connection health"),
            1 => ("Terminal", "Managed output, attached logs, and server input"),
            2 => ("Log table", "Search, filter, and inspect structured output"),
            3 => ("Kill feed", "Scenario and mod-provided combat events"),
            4 => ("Players and admin events", "Active sessions, joins, leaves, and Game Master actions"),
            5 => ("Configuration", "Guided server settings and raw JSON editing"),
            6 => ("Workshop mods", "Configured dependencies and available addons"),
            7 => ("SteamCMD setup", "Install, update, validate, and repair the server"),
            8 => ("Settings", "Paths, runtime behavior, and local join options"),
            _ => ("ForgeManager", "Arma Reforger server control")
        };
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        var index = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 => 4,
            Key.D6 or Key.NumPad6 => 5,
            Key.D7 or Key.NumPad7 => 6,
            Key.D8 or Key.NumPad8 => 7,
            Key.OemComma => 8,
            _ => -1
        };

        if (index < 0 || index >= MainTabs.Items.Count)
            return;

        MainTabs.SelectedIndex = index;
        e.Handled = true;
    }

    private void OpenTerminal_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 2;

    private void OpenConfiguration_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 5;

    private void CopyConnectionTarget_Click(object sender, RoutedEventArgs e)
    {
        var target = LocalTargetText.Text?.Trim();
        if (string.IsNullOrWhiteSpace(target))
            return;

        try
        {
            Clipboard.SetText(target);
            LocalConnectionStatusText.Text = $"Copied {target} to the clipboard.";
        }
        catch (ExternalException ex)
        {
            AppendManagerLog("Clipboard is temporarily unavailable: " + ex.Message, error: true);
        }
    }

    private async void StartServer_Click(object sender, RoutedEventArgs e) => await StartServerAsync(addonsRepair: false);

    private async void RepairStart_Click(object sender, RoutedEventArgs e) => await StartServerAsync(addonsRepair: true);

    private async Task StartServerAsync(bool addonsRepair)
    {
        try
        {
            ReadSettingsControls();
            await _settingsService.SaveAsync(_settings);

            if (_config is null)
                await LoadConfigAsync(showMessage: false);

            if (_config is not null)
            {
                var errors = _configService.Validate(_config);
                if (errors.Count > 0)
                    throw new InvalidDataException("Configuration validation failed:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
            }

            AppendManagerLog(addonsRepair ? "Starting server with addon repair..." : "Starting server...");
            SetServerStatus("STARTING", Brushes.Gold);
            await _serverProcess.StartAsync(_settings, addonsRepair);
            UpdateProcessButtons();
        }
        catch (Exception ex)
        {
            SetServerStatus("ERROR", FindBrush("Danger", Brushes.Red));
            AppendManagerLog("Start failed: " + ex.Message, error: true);
            ShowError("Could not start server", ex);
        }
    }

    private async void StopServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_serverProcess.IsExternalInstance)
            {
                var result = MessageBox.Show(
                    "This server was started outside the manager. Stop the attached ArmaReforgerServer.exe process?",
                    "Stop attached server",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            AppendManagerLog("Stopping server...");
            await _serverProcess.StopAsync();
            SetServerStatus("OFFLINE", FindBrush("TextMuted", Brushes.Gray));
        }
        catch (Exception ex)
        {
            ShowError("Could not stop server", ex);
        }
        finally
        {
            UpdateProcessButtons();
        }
    }

    private async void RestartServer_Click(object sender, RoutedEventArgs e)
    {
        if (_restarting)
            return;

        _restarting = true;
        try
        {
            AppendManagerLog("Restart requested.");
            await _serverProcess.StopAsync();
            await Task.Delay(700);
            await StartServerAsync(addonsRepair: false);
        }
        catch (Exception ex)
        {
            ShowError("Could not restart server", ex);
        }
        finally
        {
            _restarting = false;
        }
    }

    private void ServerProcess_OutputReceived(string line, bool errorStream) =>
        _pendingOutput.Enqueue((line, errorStream));

    private void OutputFlushTimer_Tick(object? sender, EventArgs e)
    {
        if (_isFlushingOutput || _pendingOutput.IsEmpty)
            return;

        _isFlushingOutput = true;
        try
        {
            var terminalChunk = new StringBuilder();
            var processed = 0;

            // Never mutate the source collection while its CollectionView is inside
            // DeferRefresh. WPF throws InvalidOperationException in that state.
            while (processed < MaxOutputBatch && _pendingOutput.TryDequeue(out var pending))
            {
                ProcessOutputLine(pending.Line, pending.ErrorStream, terminalChunk);
                processed++;
            }

            if (terminalChunk.Length > 0)
                AppendTerminalChunk(terminalChunk.ToString());

            while (_logs.Count > MaxLogEntries)
                _logs.RemoveAt(0);
        }
        finally
        {
            _isFlushingOutput = false;
        }
    }

    private void ProcessOutputLine(string line, bool errorStream, StringBuilder terminalChunk)
    {
        var entry = LogEntry.Parse(line, errorStream);
        _logs.Add(entry);
        LatestEventText.Text = $"[{entry.Category}] {entry.Message}";
        terminalChunk.AppendLine(line);
        ProcessStructuredServerEvent(line);

        // Console errors are informational for the status card. The status only reflects whether
        // the server process exists and whether its UDP listener is active.
        if (_serverProcess.IsRunning &&
            (line.Contains("OnGameStateChanged = GAME", StringComparison.OrdinalIgnoreCase) ||
             line.Contains("session", StringComparison.OrdinalIgnoreCase) &&
             line.Contains("created", StringComparison.OrdinalIgnoreCase)))
        {
            SetServerStatus("RUNNING", FindBrush("StatusSuccess", Brushes.LimeGreen));
        }
    }

    private void AppendTerminalChunk(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (_terminalCharacterCount + text.Length > MaxTerminalCharacters)
        {
            TerminalBox.Document.Blocks.Clear();
            const string marker = "[ForgeManager] Terminal view trimmed to keep the interface responsive. Full logs remain in the server profile.\n";
            TerminalBox.AppendText(marker);
            _terminalCharacterCount = marker.Length;
        }

        TerminalBox.AppendText(text);
        _terminalCharacterCount += text.Length;
        if (AutoScrollCheckBox.IsChecked == true)
            TerminalBox.ScrollToEnd();
    }

    private void ServerProcess_ProcessExited(int exitCode, bool wasManualStop)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            AppendManagerLog($"Server exited with code {exitCode}.", error: !wasManualStop);
            if (!wasManualStop)
                SetServerStatus("ERROR", FindBrush("Danger", Brushes.Red));
            else
                SetServerStatus("OFFLINE", FindBrush("TextMuted", Brushes.Gray));
            if (_activePlayers.Count > 0)
            {
                _activePlayers.Clear();
                AddAdminEvent("SERVER", "—", "—", "Server stopped; active-player list cleared.", string.Empty);
            }
            UpdateProcessButtons();

            if (!wasManualStop && _settings.AutoRestart && !_restarting)
            {
                _restarting = true;
                try
                {
                    var delay = Math.Clamp(_settings.RestartDelaySeconds, 1, 3600);
                    AppendManagerLog($"Automatic restart in {delay} seconds.");
                    await Task.Delay(TimeSpan.FromSeconds(delay));
                    await StartServerAsync(addonsRepair: false);
                }
                finally
                {
                    _restarting = false;
                }
            }
        });
    }

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        _statusTickCount++;
        if (!_serverProcess.IsRunning && _loaded && _statusTickCount % 3 == 0 && _serverProcess.TryAttachToExisting(_settings))
        {
            AppendManagerLog($"Detected and attached to externally started server process (PID {_serverProcess.ProcessId}).");
        }

        UpdateProcessButtons();
        ProcessText.Text = _serverProcess.ProcessId is int pid
            ? (_serverProcess.IsExternalInstance ? $"PID {pid} · Attached" : $"PID {pid} · Managed")
            : "No process";

        if (_serverProcess.StartedAt is { } started)
        {
            var elapsed = DateTimeOffset.Now - started;
            UptimeText.Text = elapsed.ToString(@"dd\.hh\:mm\:ss", CultureInfo.InvariantCulture).TrimStart('0', '.');
            if (string.IsNullOrWhiteSpace(UptimeText.Text))
                UptimeText.Text = "00:00:00";
        }
        else
        {
            UptimeText.Text = "00:00:00";
        }

        var port = _config?.BindPort ?? 2001;
        var listeners = NetworkService.GetUdpListeners(port);
        var isListening = listeners.Count > 0;
        ListenerText.Text = NetworkService.FormatUdpListenerStatus(listeners);

        if (_serverProcess.IsRunning)
        {
            SetServerStatus(
                isListening ? "RUNNING" : "STARTING",
                isListening ? FindBrush("StatusSuccess", Brushes.LimeGreen) : Brushes.Gold);
        }

        if (_statusTickCount % 30 == 0)
        {
            _lanAddress = NetworkService.GetLanIpv4Address();
            LanAddressText.Text = _lanAddress;
        }

        var joinAddress = ResolveLocalJoinAddress(updateControls: true, listeners);
        LocalTargetText.Text = $"{joinAddress}:{port}";
        var localConnectionStatus = NetworkService.GetLocalConnectionStatus(port, joinAddress, listeners);
        LocalConnectionStatusText.Text = localConnectionStatus;
        LocalConnectionStatusText.Foreground = localConnectionStatus.StartsWith("Local socket is available", StringComparison.Ordinal)
            ? FindBrush("StatusSuccess", Brushes.LimeGreen)
            : FindBrush("Warning", Brushes.Gold);
        LaunchLocalClientButton.IsEnabled = isListening;

        foreach (var player in _activePlayers)
            player.RefreshElapsed();
        ActivePlayerCountText.Text = $"{_activePlayers.Count} active player{(_activePlayers.Count == 1 ? string.Empty : "s")}";
        KillFeedStatusText.Text = _killFeed.Count == 0
            ? "Waiting for kill events in server output."
            : $"{_killFeed.Count} captured kill event{(_killFeed.Count == 1 ? string.Empty : "s")}.";
    }

    private void SetServerStatus(string status, Brush brush)
    {
        HeaderStatusText.Text = status;
        DashboardStatusText.Text = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(status.ToLowerInvariant());
        HeaderStatusDot.Fill = brush;
        DashboardStatusText.Foreground = brush;
    }

    private void UpdateProcessButtons()
    {
        var running = _serverProcess.IsRunning;
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        RestartButton.IsEnabled = running;
        TerminalInput.IsEnabled = _serverProcess.CanSendInput;
        SendTerminalButton.IsEnabled = _serverProcess.CanSendInput;
        TerminalInput.ToolTip = _serverProcess.IsExternalInstance
            ? "Live output is attached from console.log. Windows cannot reclaim stdin from a process started by a BAT file."
            : "Send a line to server stdin. Some server builds ignore console input.";
    }

    private void AppendManagerLog(string message, bool error = false)
    {
        var line = $"MANAGER   ({(error ? "E" : "I")}): {message}";
        var entry = LogEntry.Parse(line, error);
        _logs.Add(entry);
        while (_logs.Count > MaxLogEntries)
            _logs.RemoveAt(0);
        LatestEventText.Text = message;
        AppendTerminalChunk($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
    }

    private void ProcessStructuredServerEvent(string line)
    {
        var parsed = _serverEventParser.Parse(line);
        switch (parsed.Kind)
        {
            case ParsedServerEventKind.Kill:
            {
                var killer = ResolvePlayerLabel(parsed.Killer);
                var victim = ResolvePlayerLabel(parsed.Victim);
                var result = parsed.IsSuicide ? "SUICIDE" : parsed.IsFriendlyFire ? "TEAM KILL" : "KILL";
                _killFeed.Insert(0, new KillFeedEntry
                {
                    Killer = killer,
                    Victim = victim,
                    Weapon = string.IsNullOrWhiteSpace(parsed.Weapon) ? "Unknown" : parsed.Weapon,
                    Distance = string.IsNullOrWhiteSpace(parsed.Distance) ? "—" : parsed.Distance,
                    Result = result,
                    Raw = line
                });
                TrimCollection(_killFeed, MaxKillFeedEntries);
                break;
            }
            case ParsedServerEventKind.PlayerJoined:
                UpsertActivePlayer(parsed, addJoinEvent: true);
                break;
            case ParsedServerEventKind.PlayerObserved:
                UpsertActivePlayer(parsed, addJoinEvent: false);
                break;
            case ParsedServerEventKind.PlayerLeft:
                RemoveActivePlayer(parsed, line);
                break;
            case ParsedServerEventKind.GameMaster:
                AddAdminEvent(
                    "GAME MASTER",
                    DisplayPlayer(parsed),
                    DisplayAddress(parsed.Address),
                    parsed.Details,
                    line);
                break;
        }
    }

    private void UpsertActivePlayer(ParsedServerEvent parsed, bool addJoinEvent)
    {
        var key = BuildPlayerKey(parsed);
        if (string.IsNullOrWhiteSpace(key))
        {
            if (addJoinEvent)
                AddAdminEvent("PLAYER JOIN", DisplayPlayer(parsed), DisplayAddress(parsed.Address), parsed.Details, parsed.Details);
            return;
        }

        var player = _activePlayers.FirstOrDefault(item =>
            item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(parsed.PlayerId) && item.PlayerId.Equals(parsed.PlayerId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(parsed.PlayerName) && item.DisplayName.Equals(parsed.PlayerName, StringComparison.OrdinalIgnoreCase)));

        var isNew = player is null;
        if (player is null)
        {
            player = new ActivePlayer
            {
                Key = key,
                ConnectedAt = DateTimeOffset.Now
            };
            _activePlayers.Add(player);
        }

        if (!string.IsNullOrWhiteSpace(parsed.PlayerName))
            player.DisplayName = parsed.PlayerName;
        if (!string.IsNullOrWhiteSpace(parsed.PlayerId))
            player.PlayerId = parsed.PlayerId;
        if (!string.IsNullOrWhiteSpace(parsed.Address))
            player.Address = parsed.Address;

        if (addJoinEvent && isNew)
        {
            AddAdminEvent(
                "PLAYER JOIN",
                player.DisplayName,
                player.Address,
                parsed.Details,
                parsed.Details);
        }
    }

    private void RemoveActivePlayer(ParsedServerEvent parsed, string raw)
    {
        var player = _activePlayers.FirstOrDefault(item =>
            (!string.IsNullOrWhiteSpace(parsed.PlayerId) && item.PlayerId.Equals(parsed.PlayerId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(parsed.PlayerName) && item.DisplayName.Equals(parsed.PlayerName, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(parsed.Address) && item.Address.Equals(parsed.Address, StringComparison.OrdinalIgnoreCase)));

        var displayName = player?.DisplayName ?? DisplayPlayer(parsed);
        var address = player?.Address ?? DisplayAddress(parsed.Address);
        if (player is not null)
            _activePlayers.Remove(player);

        AddAdminEvent("PLAYER LEAVE", displayName, address, parsed.Details, raw);
    }

    private void AddAdminEvent(string eventType, string player, string address, string details, string raw)
    {
        _adminEvents.Insert(0, new AdminEventEntry
        {
            EventType = eventType,
            Player = string.IsNullOrWhiteSpace(player) ? "Unknown player" : player,
            Address = string.IsNullOrWhiteSpace(address) ? "Not exposed by log" : address,
            Details = details,
            Raw = raw
        });
        TrimCollection(_adminEvents, MaxAdminEventEntries);
    }

    private string ResolvePlayerLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unknown";

        var player = _activePlayers.FirstOrDefault(item =>
            item.PlayerId.Equals(value, StringComparison.OrdinalIgnoreCase) ||
            item.DisplayName.Equals(value, StringComparison.OrdinalIgnoreCase));
        return player?.DisplayName ?? value;
    }

    private static string BuildPlayerKey(ParsedServerEvent parsed)
    {
        if (!string.IsNullOrWhiteSpace(parsed.PlayerId))
            return "id:" + parsed.PlayerId.Trim();
        if (!string.IsNullOrWhiteSpace(parsed.PlayerName))
            return "name:" + parsed.PlayerName.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(parsed.Address))
            return "ip:" + parsed.Address.Trim();
        return string.Empty;
    }

    private static string DisplayPlayer(ParsedServerEvent parsed) =>
        !string.IsNullOrWhiteSpace(parsed.PlayerName)
            ? parsed.PlayerName
            : !string.IsNullOrWhiteSpace(parsed.PlayerId)
                ? $"Player {parsed.PlayerId}"
                : "Unknown player";

    private static string DisplayAddress(string address) =>
        string.IsNullOrWhiteSpace(address) ? "Not exposed by log" : address;

    private static void TrimCollection<T>(ObservableCollection<T> collection, int maximum)
    {
        while (collection.Count > maximum)
            collection.RemoveAt(collection.Count - 1);
    }

    private void ClearKillFeed_Click(object sender, RoutedEventArgs e) => _killFeed.Clear();

    private void ClearAdminEvents_Click(object sender, RoutedEventArgs e) => _adminEvents.Clear();

    private async Task LoadConfigAsync(bool showMessage)
    {
        ReadSettingsControls();
        if (string.IsNullOrWhiteSpace(_settings.ConfigPath) || !File.Exists(_settings.ConfigPath))
        {
            ConfigStatusText.Text = "Config path is not set or the file does not exist.";
            return;
        }

        _config = await _configService.LoadAsync(_settings.ConfigPath);
        _workshopMods.Clear();
        WorkshopStatusText.Text = "Configuration loaded. Refresh metadata to inspect dependencies.";
        ModActionStatusText.Text = string.Empty;
        PopulateConfigControls(_config);
        RawJsonBox.Text = await _configService.LoadRawAsync(_settings.ConfigPath);
        UpdateConfigSummary();
        ResolveLocalJoinAddress(updateControls: true);
        ConfigStatusText.Text = $"Loaded {DateTime.Now:T} · {_settings.ConfigPath}";
        if (showMessage)
            MessageBox.Show("Configuration reloaded.", "ForgeManager", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void PopulateConfigControls(ServerConfig config)
    {
        ServerNameBox.Text = config.Game.Name;
        ScenarioIdBox.Text = config.Game.ScenarioId;
        ServerPasswordBox.Password = config.Game.Password;
        AdminPasswordBox.Password = config.Game.PasswordAdmin;
        MaxPlayersBox.Text = config.Game.MaxPlayers.ToString(CultureInfo.InvariantCulture);
        BindAddressBox.Text = config.BindAddress ?? string.Empty;
        PublicAddressBox.Text = config.PublicAddress ?? string.Empty;
        BindPortBox.Text = config.BindPort.ToString(CultureInfo.InvariantCulture);
        PublicPortBox.Text = config.PublicPort.ToString(CultureInfo.InvariantCulture);
        MaxViewDistanceBox.Text = config.Game.GameProperties.ServerMaxViewDistance.ToString(CultureInfo.InvariantCulture);
        NetworkViewDistanceBox.Text = config.Game.GameProperties.NetworkViewDistance.ToString(CultureInfo.InvariantCulture);
        GrassDistanceBox.Text = config.Game.GameProperties.ServerMinGrassDistance.ToString(CultureInfo.InvariantCulture);
        DisableThirdPersonCheckBox.IsChecked = config.Game.GameProperties.DisableThirdPerson;
        BattleEyeCheckBox.IsChecked = config.Game.GameProperties.BattlEye;
        FastValidationCheckBox.IsChecked = config.Game.GameProperties.FastValidation;
        VisibleCheckBox.IsChecked = config.Game.Visible;
        CrossplayCheckBox.IsChecked = config.Game.CrossPlatform;
    }

    private ServerConfig ReadConfigControls()
    {
        var config = _config ?? new ServerConfig();
        config.BindAddress = NullIfWhiteSpace(BindAddressBox.Text);
        config.PublicAddress = NullIfWhiteSpace(PublicAddressBox.Text);
        config.BindPort = ParseInt(BindPortBox.Text, "Bind port");
        config.PublicPort = ParseInt(PublicPortBox.Text, "Public port");
        config.Game.Name = ServerNameBox.Text.Trim();
        config.Game.ScenarioId = ScenarioIdBox.Text.Trim();
        config.Game.Password = ServerPasswordBox.Password;
        config.Game.PasswordAdmin = AdminPasswordBox.Password;
        config.Game.MaxPlayers = ParseInt(MaxPlayersBox.Text, "Max players");
        config.Game.Visible = VisibleCheckBox.IsChecked == true;
        config.Game.CrossPlatform = CrossplayCheckBox.IsChecked == true;
        config.Game.GameProperties.ServerMaxViewDistance = ParseInt(MaxViewDistanceBox.Text, "Max view distance");
        config.Game.GameProperties.NetworkViewDistance = ParseInt(NetworkViewDistanceBox.Text, "Network view distance");
        config.Game.GameProperties.ServerMinGrassDistance = ParseInt(GrassDistanceBox.Text, "Minimum grass distance");
        config.Game.GameProperties.DisableThirdPerson = DisableThirdPersonCheckBox.IsChecked == true;
        config.Game.GameProperties.BattlEye = BattleEyeCheckBox.IsChecked == true;
        config.Game.GameProperties.FastValidation = FastValidationCheckBox.IsChecked == true;
        return config;
    }

    private static int ParseInt(string value, string fieldName)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            throw new InvalidDataException($"{fieldName} must be a whole number.");
        return result;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string ResolveLocalJoinAddress(bool updateControls, IReadOnlyList<IPEndPoint>? listeners = null)
    {
        var port = _config?.BindPort ?? 2001;
        var address = NetworkService.ResolveLocalJoinAddress(
            port,
            _config?.BindAddress,
            _settings.LocalJoinAddress,
            _settings.AutoDetectLocalJoinAddress,
            listeners,
            _lanAddress);

        if (updateControls && _settings.AutoDetectLocalJoinAddress)
        {
            _settings.LocalJoinAddress = address;
            if (!LocalJoinAddressBox.IsKeyboardFocusWithin)
                LocalJoinAddressBox.Text = address;
        }

        return address;
    }

    private void UpdateConfigSummary()
    {
        if (_config is null)
            return;
        SummaryServerName.Text = _config.Game.Name;
        SummaryScenario.Text = _config.Game.ScenarioId;
        SummaryPlayers.Text = _config.Game.MaxPlayers.ToString(CultureInfo.InvariantCulture);
        SummaryMods.Text = _config.Game.Mods.Count.ToString(CultureInfo.InvariantCulture);
    }

    private async void ReloadConfig_Click(object sender, RoutedEventArgs e)
    {
        try { await LoadConfigAsync(showMessage: true); }
        catch (Exception ex) { ShowError("Could not reload config", ex); }
    }

    private void ValidateConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = ReadConfigControls();
            var errors = _configService.Validate(config);
            if (errors.Count == 0)
            {
                ConfigStatusText.Text = "Configuration is valid.";
                MessageBox.Show("Configuration passed local validation.", "Validation", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(string.Join(Environment.NewLine, errors), "Validation errors", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowError("Validation failed", ex);
        }
    }

    private async void SaveGuidedConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReadSettingsControls();
            _config = ReadConfigControls();
            await _configService.SaveAsync(_settings.ConfigPath, _config);
            RawJsonBox.Text = await _configService.LoadRawAsync(_settings.ConfigPath);
            UpdateConfigSummary();
            ConfigStatusText.Text = "Saved with timestamped backup.";
        }
        catch (Exception ex)
        {
            ShowError("Could not save configuration", ex);
        }
    }

    private async void ReloadRawConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReadSettingsControls();
            RawJsonBox.Text = await _configService.LoadRawAsync(_settings.ConfigPath);
        }
        catch (Exception ex)
        {
            ShowError("Could not load raw JSON", ex);
        }
    }

    private async void SaveRawConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReadSettingsControls();
            await _configService.SaveRawAsync(_settings.ConfigPath, RawJsonBox.Text);
            await LoadConfigAsync(showMessage: false);
            ConfigStatusText.Text = "Raw JSON validated and saved.";
        }
        catch (Exception ex)
        {
            ShowError("Raw JSON is invalid or could not be saved", ex);
        }
    }

    private bool FilterLogEntry(object item)
    {
        if (item is not LogEntry entry)
            return false;

        var category = entry.Category.ToUpperInvariant();
        var categoryAllowed = category switch
        {
            "BACKEND" => BackendFilter.IsChecked == true,
            "NETWORK" => NetworkFilter.IsChecked == true,
            "ENGINE" => EngineFilter.IsChecked == true,
            "PLATFORM" => PlatformFilter.IsChecked == true,
            _ => OtherFilter.IsChecked == true
        };

        if (!categoryAllowed)
            return false;
        if (ErrorsOnlyFilter.IsChecked == true && entry.Severity != "ERROR")
            return false;

        var search = LogSearchBox.Text.Trim();
        return search.Length == 0 ||
               entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               entry.Severity.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void LogFilter_Changed(object sender, RoutedEventArgs e) => _logView?.Refresh();

    private void ClearTerminal_Click(object sender, RoutedEventArgs e)
    {
        TerminalBox.Document.Blocks.Clear();
        _terminalCharacterCount = 0;
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e) => _logs.Clear();

    private async void SendTerminalInput_Click(object sender, RoutedEventArgs e) => await SendTerminalInputAsync();

    private async void TerminalInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await SendTerminalInputAsync();
    }

    private async Task SendTerminalInputAsync()
    {
        var command = TerminalInput.Text.Trim();
        if (command.Length == 0)
            return;

        try
        {
            await _serverProcess.SendInputAsync(command);
            AppendManagerLog("> " + command);
            TerminalInput.Clear();
        }
        catch (Exception ex)
        {
            ShowError("Could not send terminal input", ex);
        }
    }

    private async void RefreshWorkshop_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null)
        {
            MessageBox.Show("Load a server configuration first.", "Mods", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _workshopCts?.Cancel();
        _workshopCts = new CancellationTokenSource();
        WorkshopStatusText.Text = "Loading Workshop metadata and dependency tree...";

        try
        {
            var ids = _config.Game.Mods.Select(mod => mod.ModId).ToArray();
            var mods = await _workshopService.FetchTreeAsync(ids, cancellationToken: _workshopCts.Token, forceRefresh: true);
            _workshopMods.Clear();
            foreach (var mod in mods)
                _workshopMods.Add(mod);
            WorkshopStatusText.Text = $"Loaded {_workshopMods.Count} addons ({_workshopMods.Count(mod => mod.IsRoot)} explicit, {_workshopMods.Count(mod => !mod.IsRoot)} dependencies).";
        }
        catch (OperationCanceledException)
        {
            WorkshopStatusText.Text = "Workshop refresh cancelled.";
        }
        catch (Exception ex)
        {
            WorkshopStatusText.Text = "Workshop refresh failed.";
            ShowError("Could not retrieve Workshop data", ex);
        }
    }

    private async void AddModById_Click(object sender, RoutedEventArgs e)
    {
        await AddModToConfigAsync(AddModIdBox.Text);
    }

    private async void AddAvailableMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string modId })
            await AddModToConfigAsync(modId);
    }

    private async Task AddModToConfigAsync(string value)
    {
        if (_config is null)
        {
            MessageBox.Show("Load the server configuration first.", "Mods", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var modId = WorkshopService.NormalizeModId(value);
        if (modId.Length != 16)
        {
            MessageBox.Show("Enter a 16-character Workshop ID or paste an official Workshop URL.", "Invalid mod ID", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_config.Game.Mods.Any(mod => mod.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("That addon is already in the explicit mod list.", "Already configured", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_workshopMods.Any(mod => !mod.IsRoot && mod.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                "That addon is already supplied as a dependency of a configured root addon. It was not duplicated in config.json.",
                "Already resolved as dependency",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            ModActionStatusText.Text = $"Checking Workshop addon {modId}...";
            var metadata = await _workshopService.FetchAsync(modId);
            if (!metadata.Available)
            {
                MessageBox.Show(metadata.Error.Length == 0 ? "The Workshop addon is unavailable." : metadata.Error,
                    "Addon unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _config.Game.Mods.Add(new ModConfig { ModId = modId, Name = metadata.Name });
            await SaveConfigAfterModChangeAsync($"Added {metadata.Name} ({modId}).");
            AddModIdBox.Clear();
            await RefreshConfiguredWorkshopAsync();
            await LoadWorkshopBrowsePageAsync(_workshopBrowsePage);
        }
        catch (Exception ex)
        {
            ShowError("Could not add Workshop addon", ex);
        }
    }

    private async void RemoveConfiguredMod_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null || sender is not Button { Tag: string modId })
            return;

        var mod = _config.Game.Mods.FirstOrDefault(item => item.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase));
        if (mod is null)
            return;

        var result = MessageBox.Show(
            $"Remove {mod.Name ?? mod.ModId} from the explicit server mod list?\n\nDependencies are controlled by the remaining root addons.",
            "Remove mod",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            _config.Game.Mods.Remove(mod);
            await SaveConfigAfterModChangeAsync($"Removed {mod.Name ?? mod.ModId}.");
            await RefreshConfiguredWorkshopAsync();
            await LoadWorkshopBrowsePageAsync(_workshopBrowsePage);
        }
        catch (Exception ex)
        {
            ShowError("Could not remove Workshop addon", ex);
        }
    }

    private async Task SaveConfigAfterModChangeAsync(string status)
    {
        if (_config is null)
            return;

        await _configService.SaveAsync(_settings.ConfigPath, _config);
        RawJsonBox.Text = await _configService.LoadRawAsync(_settings.ConfigPath);
        UpdateConfigSummary();
        ModActionStatusText.Text = status;
    }

    private async Task RefreshConfiguredWorkshopAsync()
    {
        if (_config is null)
            return;

        WorkshopStatusText.Text = "Refreshing configured addons and dependencies...";
        var ids = _config.Game.Mods.Select(mod => mod.ModId).ToArray();
        var mods = await _workshopService.FetchTreeAsync(ids, cancellationToken: CancellationToken.None);
        _workshopMods.Clear();
        foreach (var mod in mods)
            _workshopMods.Add(mod);
        WorkshopStatusText.Text = $"Loaded {_workshopMods.Count} addons ({_workshopMods.Count(mod => mod.IsRoot)} configured, {_workshopMods.Count(mod => !mod.IsRoot)} dependencies).";
    }

    private async void BrowseWorkshopSearch_Click(object sender, RoutedEventArgs e)
    {
        _workshopBrowsePage = 1;
        await LoadWorkshopBrowsePageAsync(_workshopBrowsePage);
    }

    private async void AvailableWorkshopSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        _workshopBrowsePage = 1;
        await LoadWorkshopBrowsePageAsync(_workshopBrowsePage);
    }

    private async void BrowseWorkshopPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_workshopBrowsePage <= 1)
            return;
        await LoadWorkshopBrowsePageAsync(_workshopBrowsePage - 1);
    }

    private async void BrowseWorkshopNext_Click(object sender, RoutedEventArgs e) =>
        await LoadWorkshopBrowsePageAsync(_workshopBrowsePage + 1);

    private async Task LoadWorkshopBrowsePageAsync(int page)
    {
        if (_config is null)
        {
            MessageBox.Show("Load the server configuration first.", "Workshop", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _workshopCts?.Cancel();
        _workshopCts = new CancellationTokenSource();
        AvailableWorkshopStatusText.Text = "Loading official Arma Reforger Workshop page...";
        PreviousWorkshopPageButton.IsEnabled = false;
        NextWorkshopPageButton.IsEnabled = false;

        try
        {
            var result = await _workshopService.FetchBrowsePageAsync(
                AvailableWorkshopSearchBox.Text,
                page,
                _config.Game.Mods.Select(mod => mod.ModId),
                _workshopCts.Token);

            _workshopBrowsePage = result.Page;
            _availableWorkshopMods.Clear();
            foreach (var mod in result.Mods)
                _availableWorkshopMods.Add(mod);

            WorkshopPageText.Text = result.TotalResults > 0
                ? $"Page {_workshopBrowsePage} · {result.TotalResults:N0} results"
                : $"Page {_workshopBrowsePage}";
            AvailableWorkshopStatusText.Text = result.Mods.Count == 0
                ? "No addons were returned for this page/search."
                : $"Loaded {result.Mods.Count} addons from the official Workshop.";
            PreviousWorkshopPageButton.IsEnabled = _workshopBrowsePage > 1;
            NextWorkshopPageButton.IsEnabled = result.Mods.Count == 16;
        }
        catch (OperationCanceledException)
        {
            AvailableWorkshopStatusText.Text = "Workshop request cancelled.";
        }
        catch (Exception ex)
        {
            AvailableWorkshopStatusText.Text = "Workshop page could not be loaded.";
            ShowError("Could not browse the Arma Reforger Workshop", ex);
        }
    }

    private void OpenWorkshopHome_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://reforger.armaplatform.com/workshop") { UseShellExecute = true });

    private async void UseMinimalMods_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null)
        {
            MessageBox.Show("Load the configuration first.", "Mods", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = MessageBox.Show(
            "Replace the explicit mods array with only CIE Udachne? Its Workshop dependencies remain discoverable and are resolved by the Workshop dependency chain.",
            "Use minimal mod list",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
            return;

        try
        {
            _config.Game.Mods =
            [
                new ModConfig { ModId = CieUdachneModId, Name = CieUdachneName }
            ];
            await _configService.SaveAsync(_settings.ConfigPath, _config);
            RawJsonBox.Text = await _configService.LoadRawAsync(_settings.ConfigPath);
            UpdateConfigSummary();
            ModActionStatusText.Text = "Saved minimal one-root-addon configuration.";
            await RefreshConfiguredWorkshopAsync();
            if (_availableWorkshopMods.Count > 0)
                await LoadWorkshopBrowsePageAsync(_workshopBrowsePage);
        }
        catch (Exception ex)
        {
            ShowError("Could not minimize mod list", ex);
        }
    }

    private bool FilterMod(object item)
    {
        if (item is not WorkshopMod mod)
            return false;
        var search = ModSearchBox.Text.Trim();
        return search.Length == 0 ||
               mod.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               mod.ModId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               mod.Role.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void ModSearch_Changed(object sender, TextChangedEventArgs e) => _modsView.Refresh();

    private void OpenWorkshop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url))
            return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        ReadSettingsControls();
        var directory = Path.GetDirectoryName(_settings.ConfigPath);
        if (directory is null || !Directory.Exists(directory))
        {
            MessageBox.Show("The config folder does not exist.", "Config folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(directory);
        Process.Start(startInfo);
    }

    private async void AddFirewallRule_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var port = _config?.BindPort ?? 2001;
            var code = await NetworkService.AddFirewallRuleAsync(port);
            MessageBox.Show(code == 0 ? $"Allowed inbound UDP {port}." : $"netsh exited with code {code}.",
                "Windows Firewall", MessageBoxButton.OK, code == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show("Administrator elevation was cancelled.", "Windows Firewall", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowError("Could not add firewall rule", ex);
        }
    }

    private void LaunchLocalClient_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReadSettingsControls();
            var port = _config?.BindPort ?? 2001;
            if (!NetworkService.HasUdpListener(port))
                throw new InvalidOperationException($"No local UDP listener was found on port {port}. Wait until the server has finished starting.");

            var joinAddress = ResolveLocalJoinAddress(updateControls: true);
            AppendManagerLog($"Launching local client for {joinAddress}:{port}.");
            NetworkService.LaunchClient(_settings.ClientExecutablePath, joinAddress, port);
        }
        catch (Exception ex)
        {
            ShowError("Could not launch local client", ex);
        }
    }

    private async void AutoRestart_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;
        _settings.AutoRestart = AutoRestartCheckBox.IsChecked == true;
        try { await _settingsService.SaveAsync(_settings); } catch { }
    }

    private async void AutoDetectJoinAddress_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;

        _settings.AutoDetectLocalJoinAddress = AutoDetectJoinAddressCheckBox.IsChecked == true;
        LocalJoinAddressBox.IsEnabled = !_settings.AutoDetectLocalJoinAddress;
        ResolveLocalJoinAddress(updateControls: true);
        try { await _settingsService.SaveAsync(_settings); } catch { }
    }

    private void PopulateSettingsControls()
    {
        ServerExePathBox.Text = _settings.ServerExecutablePath;
        ConfigPathBox.Text = _settings.ConfigPath;
        ProfilePathBox.Text = _settings.ProfilePath;
        ClientExePathBox.Text = _settings.ClientExecutablePath;
        SteamCmdPathBox.Text = string.IsNullOrWhiteSpace(_settings.SteamCmdPath) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForgeManager", "SteamCMD") : _settings.SteamCmdPath;
        ServerInstallPathBox.Text = string.IsNullOrWhiteSpace(_settings.ServerInstallPath) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ForgeManager", "ArmaReforgerServer") : _settings.ServerInstallPath;
        ServerBranchBox.SelectedIndex = _settings.UseExperimentalServer ? 1 : 0;
        MaxFpsBox.Text = _settings.MaxFps.ToString(CultureInfo.InvariantCulture);
        RestartDelayBox.Text = _settings.RestartDelaySeconds.ToString(CultureInfo.InvariantCulture);
        LocalJoinAddressBox.Text = _settings.LocalJoinAddress;
        AutoDetectJoinAddressCheckBox.IsChecked = _settings.AutoDetectLocalJoinAddress;
        LocalJoinAddressBox.IsEnabled = !_settings.AutoDetectLocalJoinAddress;
        PortableLayoutText.Text = _settingsService.IsPortableMode
            ? $"Detected drop-in server root: {_settingsService.PortableServerRoot}"
            : "Drop the published manager EXE beside ArmaReforgerServer.exe to enable automatic portable paths.";
    }

    private void ReadSettingsControls()
    {
        _settings.ServerExecutablePath = ServerExePathBox.Text.Trim();
        _settings.ConfigPath = ConfigPathBox.Text.Trim();
        _settings.ProfilePath = ProfilePathBox.Text.Trim();
        _settings.ClientExecutablePath = ClientExePathBox.Text.Trim();
        _settings.SteamCmdPath = SteamCmdPathBox.Text.Trim();
        _settings.ServerInstallPath = ServerInstallPathBox.Text.Trim();
        _settings.UseExperimentalServer = ServerBranchBox.SelectedIndex == 1;
        _settings.MaxFps = int.TryParse(MaxFpsBox.Text, out var maxFps) ? Math.Clamp(maxFps, 10, 240) : 60;
        _settings.RestartDelaySeconds = int.TryParse(RestartDelayBox.Text, out var delay) ? Math.Clamp(delay, 1, 3600) : 10;
        _settings.LocalJoinAddress = string.IsNullOrWhiteSpace(LocalJoinAddressBox.Text) ? "127.0.0.1" : LocalJoinAddressBox.Text.Trim();
        _settings.AutoDetectLocalJoinAddress = AutoDetectJoinAddressCheckBox.IsChecked == true;
        _settings.AutoRestart = AutoRestartCheckBox.IsChecked == true;
        _settingsService.ApplyDetectedLayout(_settings);
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReadSettingsControls();
            await _settingsService.SaveAsync(_settings);
            SettingsStatusText.Text = "Saved " + DateTime.Now.ToString("T");
            PopulateSettingsControls();
            await LoadConfigAsync(showMessage: false);
        }
        catch (Exception ex)
        {
            ShowError("Could not save settings", ex);
        }
    }

    private void BrowseServerExe_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseExecutable("Select ArmaReforgerServer.exe", "ArmaReforgerServer.exe");
        if (path is null)
            return;
        ServerExePathBox.Text = path;
        var root = Path.GetDirectoryName(path)!;
        ConfigPathBox.Text = Path.Combine(root, "configs", "config.json");
        ProfilePathBox.Text = Path.Combine(root, "profile");
    }

    private void BrowseClientExe_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseExecutable("Select ArmaReforgerSteam.exe", "ArmaReforgerSteam.exe");
        if (path is not null)
            ClientExePathBox.Text = path;
    }

    private static string? BrowseExecutable(string title, string expectedName)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = $"{expectedName}|{expectedName}|Executable files|*.exe|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void BrowseConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Arma Reforger server config",
            Filter = "JSON files|*.json|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
            ConfigPathBox.Text = dialog.FileName;
    }

    private void UseDefaultProfile_Click(object sender, RoutedEventArgs e)
    {
        var root = Path.GetDirectoryName(ServerExePathBox.Text.Trim());
        if (!string.IsNullOrWhiteSpace(root))
            ProfilePathBox.Text = Path.Combine(root, "profile");
    }


    private void BrowseSteamCmdFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select SteamCMD folder", Multiselect = false };
        if (dialog.ShowDialog() == true) SteamCmdPathBox.Text = dialog.FolderName;
    }

    private void BrowseServerInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select Arma Reforger server install folder", Multiselect = false };
        if (dialog.ShowDialog() == true) ServerInstallPathBox.Text = dialog.FolderName;
    }

    private async void InstallSteamCmdAndServer_Click(object sender, RoutedEventArgs e) => await RunSteamCmdInstallAsync(ensureSteamCmd: true);
    private async void UpdateServer_Click(object sender, RoutedEventArgs e) => await RunSteamCmdInstallAsync(ensureSteamCmd: false);

    private async Task RunSteamCmdInstallAsync(bool ensureSteamCmd)
    {
        if (_steamCmdCts is not null) return;
        try
        {
            ReadSettingsControls();
            if (string.IsNullOrWhiteSpace(_settings.SteamCmdPath) || string.IsNullOrWhiteSpace(_settings.ServerInstallPath))
                throw new InvalidOperationException("Select both the SteamCMD and server install folders.");
            await _settingsService.SaveAsync(_settings);
            _steamCmdCts = new CancellationTokenSource();
            SetSteamCmdBusy(true);
            SteamCmdOutputBox.Clear(); SteamCmdProgressBar.Value = 0;
            var log = new Progress<string>(line => { SteamCmdOutputBox.AppendText(line + Environment.NewLine); SteamCmdOutputBox.ScrollToEnd(); SteamCmdStatusText.Text = line; });
            var progress = new Progress<double>(value => SteamCmdProgressBar.Value = Math.Clamp(value, 0, 100));
            if (ensureSteamCmd)
                await _steamCmdService.EnsureInstalledAsync(_settings.SteamCmdPath, log, progress, _steamCmdCts.Token);
            var appId = _settings.UseExperimentalServer ? 1890870 : 1874900;
            await _steamCmdService.InstallOrUpdateServerAsync(_settings.SteamCmdPath, _settings.ServerInstallPath, appId, ValidateServerFilesCheckBox.IsChecked == true, log, _steamCmdCts.Token);
            SteamCmdProgressBar.Value = 100; SteamCmdStatusText.Text = "Server installation completed.";
            var exe = Path.Combine(_settings.ServerInstallPath, "ArmaReforgerServer.exe");
            ServerExePathBox.Text = exe; ConfigPathBox.Text = Path.Combine(_settings.ServerInstallPath, "configs", "config.json"); ProfilePathBox.Text = Path.Combine(_settings.ServerInstallPath, "profile");
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPathBox.Text)!); Directory.CreateDirectory(ProfilePathBox.Text);
            if (!File.Exists(ConfigPathBox.Text))
            {
                var sample = Path.Combine(AppContext.BaseDirectory, "configs", "config.minimal.json");
                if (File.Exists(sample)) File.Copy(sample, ConfigPathBox.Text, false);
            }
            ReadSettingsControls(); await _settingsService.SaveAsync(_settings);
            MessageBox.Show("Arma Reforger Server is installed and ForgeManager paths were updated.", "SteamCMD complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { SteamCmdStatusText.Text = "Operation cancelled."; }
        catch (Exception ex) { SteamCmdStatusText.Text = "Install failed: " + ex.Message; ShowError("SteamCMD operation failed", ex); }
        finally { _steamCmdCts?.Dispose(); _steamCmdCts = null; SetSteamCmdBusy(false); }
    }

    private void CancelSteamCmd_Click(object sender, RoutedEventArgs e) { _steamCmdCts?.Cancel(); _steamCmdService.Cancel(); }
    private void OpenServerInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = ServerInstallPathBox.Text.Trim();
        if (!Directory.Exists(path))
            return;

        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }
    private void SetSteamCmdBusy(bool busy)
    {
        InstallSteamCmdButton.IsEnabled = !busy;
        UpdateServerButton.IsEnabled = !busy;
        CancelSteamCmdButton.IsEnabled = busy;
        SteamCmdPathBox.IsEnabled = !busy;
        ServerInstallPathBox.IsEnabled = !busy;
        ServerBranchBox.IsEnabled = !busy;
        ValidateServerFilesCheckBox.IsEnabled = !busy;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableImmersiveDarkTitleBar();
    }

    private void TryEnableImmersiveDarkTitleBar()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var enabled = 1;

            // Attribute 20 is current; attribute 19 is used by some older Windows 10 builds.
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
        catch
        {
            // Theme enhancement only. The app remains usable if DWM rejects the request.
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    private static Brush FindBrush(string key, Brush fallback) =>
        Application.Current.TryFindResource(key) as Brush ?? fallback;

    private static void ShowError(string title, Exception ex)
    {
        var builder = new StringBuilder(ex.Message);
        if (ex.InnerException is not null)
            builder.AppendLine().Append(ex.InnerException.Message);
        MessageBox.Show(builder.ToString(), title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
