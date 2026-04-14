using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galbox.App.Services;
using Galbox.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Galbox.App.ViewModels;

/// <summary>
/// ViewModel for the Settings Page.
/// Handles all user settings categories with MVVM pattern.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly GalboxDbContext _dbContext;
    private readonly IProcessMonitorService _processMonitorService;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Whether settings are being loaded.
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Whether settings are being saved.
    /// </summary>
    [ObservableProperty]
    private bool _isSaving;

    /// <summary>
    /// Error message to display if operation fails.
    /// </summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// Success message to display after saving.
    /// </summary>
    [ObservableProperty]
    private string? _successMessage;

    /// <summary>
    /// Warning message for "Exit app" launch behavior.
    /// </summary>
    [ObservableProperty]
    private string? _launchBehaviorWarning;

    #region Scraping Settings

    [ObservableProperty]
    private bool _enableBangumi;

    [ObservableProperty]
    private bool _enableVndb;

    [ObservableProperty]
    private bool _enableYmgal;

    [ObservableProperty]
    private bool _enableCngal;

    [ObservableProperty]
    private string? _bangumiAccessToken;

    [ObservableProperty]
    private string? _bangumiUserId;

    [ObservableProperty]
    private int _matchThreshold = 90;

    [ObservableProperty]
    private bool _autoScrapeOnAdd = true;

    /// <summary>
    /// Source priority list for display and editing.
    /// </summary>
    public ObservableCollection<SourcePriorityItem> SourcePriorityList { get; } = new();

    #endregion

    #region Launch Behavior

    [ObservableProperty]
    private LaunchBehavior _onLaunchBehavior = LaunchBehavior.Minimize;

    #endregion

    #region Exit Behavior

    [ObservableProperty]
    private ExitBehavior _onExitBehavior = ExitBehavior.MaximizeToDesktop;

    [ObservableProperty]
    private bool _autoBackupOnExit;

    #endregion

    #region Boss Key Settings

    [ObservableProperty]
    private bool _enableBossKey = true;

    [ObservableProperty]
    private bool _bossKeyAlt;

    [ObservableProperty]
    private bool _bossKeyCtrl;

    [ObservableProperty]
    private bool _bossKeyShift;

    [ObservableProperty]
    private bool _bossKeyWin;

    [ObservableProperty]
    private string _bossKeyKey = "H";

    [ObservableProperty]
    private bool _minimizeToTrayOnBossKey = true;

    [ObservableProperty]
    private bool _showBossKeyNotification = true;

    #endregion

    #region Save Path Settings

    [ObservableProperty]
    private string _defaultBackupPath = string.Empty;

    [ObservableProperty]
    private bool _useCustomBackupPath;

    [ObservableProperty]
    private string _screenshotPath = string.Empty;

    #endregion

    #region Process Monitoring

    [ObservableProperty]
    private bool _enableAdvancedMonitoring;

    [ObservableProperty]
    private bool _autoScreenshotOnExit;

    [ObservableProperty]
    private int _monitoringIntervalMs = 1000;

    [ObservableProperty]
    private ScreenshotFormat _screenshotFormat = ScreenshotFormat.Png;

    [ObservableProperty]
    private int _jpgQuality = 90;

    #endregion

    #region Appearance

    [ObservableProperty]
    private AppTheme _theme = AppTheme.Default;

    [ObservableProperty]
    private AppLanguage _language = AppLanguage.ChineseSimplified;

    [ObservableProperty]
    private LibraryViewMode _libraryViewMode = LibraryViewMode.Grid;

    #endregion

    #region Library Settings

    [ObservableProperty]
    private bool _autoScanOnStartup;

    [ObservableProperty]
    private string _defaultScrapingSource = "Bangumi";

    #endregion

    #region About

    /// <summary>
    /// Application version string.
    /// </summary>
    public string AppVersion => "1.0.0";

    /// <summary>
    /// Developer name.
    /// </summary>
    public string DeveloperInfo => "Galbox Development Team";

    /// <summary>
    /// GitHub repository URL.
    /// </summary>
    public string GitHubUrl => "https://github.com/galbox/galbox";

    /// <summary>
    /// License information.
    /// </summary>
    public string LicenseInfo => "MIT License";

    /// <summary>
    /// Application data path.
    /// </summary>
    public string AppDataPath => GetAppDataPath();

    #endregion

    /// <summary>
    /// Creates a SettingsViewModel with injected dependencies.
    /// </summary>
    public SettingsViewModel(
        GalboxDbContext dbContext,
        IProcessMonitorService processMonitorService,
        ILogger<SettingsViewModel> logger,
        IServiceProvider serviceProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _processMonitorService = processMonitorService ?? throw new ArgumentNullException(nameof(processMonitorService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        // Initialize source priority list
        InitializeSourcePriorityList();
    }

    /// <summary>
    /// Initializes the source priority list with default values.
    /// </summary>
    private void InitializeSourcePriorityList()
    {
        SourcePriorityList.Clear();
        SourcePriorityList.Add(new SourcePriorityItem { Name = "Bangumi", DisplayName = "Bangumi", IsEnabled = true });
        SourcePriorityList.Add(new SourcePriorityItem { Name = "VNDB", DisplayName = "VNDB", IsEnabled = true });
        SourcePriorityList.Add(new SourcePriorityItem { Name = "ymgal", DisplayName = "ymgal", IsEnabled = true });
        SourcePriorityList.Add(new SourcePriorityItem { Name = "cngal", DisplayName = "cngal", IsEnabled = true });
    }

    /// <summary>
    /// Loads settings from database asynchronously.
    /// </summary>
    public async Task LoadSettingsAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // Get or create settings record
            var settings = await _dbContext.UserSettings.FirstOrDefaultAsync();

            if (settings == null)
            {
                // Create default settings
                settings = new UserSettings();
                _dbContext.UserSettings.Add(settings);
                await _dbContext.SaveChangesAsync();
            }

            // Load all settings into observable properties
            LoadSettingsFromEntity(settings);

            _logger.LogInformation("Settings loaded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading settings");
            ErrorMessage = $"Failed to load settings: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Loads settings from entity into observable properties.
    /// </summary>
    private void LoadSettingsFromEntity(UserSettings settings)
    {
        // Scraping
        EnableBangumi = settings.EnableBangumi;
        EnableVndb = settings.EnableVndb;
        EnableYmgal = settings.EnableYmgal;
        EnableCngal = settings.EnableCngal;
        BangumiAccessToken = settings.BangumiAccessToken;
        BangumiUserId = settings.BangumiUserId;
        MatchThreshold = settings.MatchThresholdPercent;
        AutoScrapeOnAdd = settings.AutoScrapeOnAdd;

        // Load source priority
        LoadSourcePriorityFromJson(settings.SourcePriorityJson);

        // Launch behavior
        OnLaunchBehavior = settings.OnLaunchBehavior;
        UpdateLaunchBehaviorWarning();

        // Exit behavior
        OnExitBehavior = settings.OnExitBehavior;
        AutoBackupOnExit = settings.AutoBackupOnExit;

        // Boss key
        EnableBossKey = settings.EnableBossKey;
        LoadBossKeyModifiers(settings.BossKeyModifiers);
        BossKeyKey = GetKeyNameFromVirtualKey(settings.BossKeyVirtualKey);
        MinimizeToTrayOnBossKey = settings.MinimizeToTrayOnBossKey;
        ShowBossKeyNotification = settings.ShowBossKeyNotification;

        // Save paths
        DefaultBackupPath = settings.DefaultBackupPath;
        UseCustomBackupPath = settings.UseCustomBackupPath;
        ScreenshotPath = settings.ScreenshotPath;

        // Process monitoring
        EnableAdvancedMonitoring = settings.EnableAdvancedMonitoring;
        AutoScreenshotOnExit = settings.AutoScreenshotOnExit;
        MonitoringIntervalMs = settings.MonitoringIntervalMs;
        ScreenshotFormat = settings.ScreenshotFormat;
        JpgQuality = settings.JpgQuality;

        // Appearance
        Theme = settings.Theme;
        Language = settings.Language;
        LibraryViewMode = settings.LibraryViewMode;

        // Library
        AutoScanOnStartup = settings.AutoScanOnStartup;
        DefaultScrapingSource = settings.DefaultScrapingSource;
    }

    /// <summary>
    /// Loads source priority from JSON string.
    /// </summary>
    private void LoadSourcePriorityFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            InitializeSourcePriorityList();
            return;
        }

        try
        {
            var priorityList = JsonSerializer.Deserialize<List<string>>(json);
            if (priorityList == null || priorityList.Count == 0)
            {
                InitializeSourcePriorityList();
                return;
            }

            // Clear and rebuild list based on priority
            SourcePriorityList.Clear();

            // Add items in priority order
            foreach (var sourceName in priorityList)
            {
                var existingItem = SourcePriorityList.FirstOrDefault(s => s.Name == sourceName);
                if (existingItem == null)
                {
                    SourcePriorityList.Add(new SourcePriorityItem
                    {
                        Name = sourceName,
                        DisplayName = GetSourceDisplayName(sourceName),
                        IsEnabled = GetSourceEnabled(sourceName)
                    });
                }
            }

            // Add remaining sources not in priority list
            var allSources = new[] { "Bangumi", "VNDB", "ymgal", "cngal" };
            foreach (var sourceName in allSources)
            {
                if (!SourcePriorityList.Any(s => s.Name == sourceName))
                {
                    SourcePriorityList.Add(new SourcePriorityItem
                    {
                        Name = sourceName,
                        DisplayName = GetSourceDisplayName(sourceName),
                        IsEnabled = GetSourceEnabled(sourceName)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse source priority JSON, using defaults");
            InitializeSourcePriorityList();
        }
    }

    /// <summary>
    /// Gets display name for a source.
    /// </summary>
    private string GetSourceDisplayName(string name) => name;

    /// <summary>
    /// Gets enabled status for a source.
    /// </summary>
    private bool GetSourceEnabled(string name) => name switch
    {
        "Bangumi" => EnableBangumi,
        "VNDB" => EnableVndb,
        "ymgal" => EnableYmgal,
        "cngal" => EnableCngal,
        _ => false
    };

    /// <summary>
    /// Loads boss key modifiers from flags.
    /// </summary>
    private void LoadBossKeyModifiers(BossKeyModifiers modifiers)
    {
        BossKeyAlt = modifiers.HasFlag(BossKeyModifiers.Alt);
        BossKeyCtrl = modifiers.HasFlag(BossKeyModifiers.Ctrl);
        BossKeyShift = modifiers.HasFlag(BossKeyModifiers.Shift);
        BossKeyWin = modifiers.HasFlag(BossKeyModifiers.Win);
    }

    /// <summary>
    /// Gets key name from virtual key code.
    /// </summary>
    private string GetKeyNameFromVirtualKey(uint virtualKey)
    {
        // Common key mappings
        return virtualKey switch
        {
            0x41 => "A",
            0x42 => "B",
            0x43 => "C",
            0x44 => "D",
            0x45 => "E",
            0x46 => "F",
            0x47 => "G",
            0x48 => "H",
            0x49 => "I",
            0x4A => "J",
            0x4B => "K",
            0x4C => "L",
            0x4D => "M",
            0x4E => "N",
            0x4F => "O",
            0x50 => "P",
            0x51 => "Q",
            0x52 => "R",
            0x53 => "S",
            0x54 => "T",
            0x55 => "U",
            0x56 => "V",
            0x57 => "W",
            0x58 => "X",
            0x59 => "Y",
            0x5A => "Z",
            0x20 => "Space",
            0x08 => "Back",
            0x1B => "Esc",
            _ => "H" // Default to H
        };
    }

    /// <summary>
    /// Gets virtual key code from key name.
    /// </summary>
    private uint GetVirtualKeyFromKeyName(string keyName)
    {
        return keyName.ToUpperInvariant() switch
        {
            "A" => 0x41,
            "B" => 0x42,
            "C" => 0x43,
            "D" => 0x44,
            "E" => 0x45,
            "F" => 0x46,
            "G" => 0x47,
            "H" => 0x48,
            "I" => 0x49,
            "J" => 0x4A,
            "K" => 0x4B,
            "L" => 0x4C,
            "M" => 0x4D,
            "N" => 0x4E,
            "O" => 0x4F,
            "P" => 0x50,
            "Q" => 0x51,
            "R" => 0x52,
            "S" => 0x53,
            "T" => 0x54,
            "U" => 0x55,
            "V" => 0x56,
            "W" => 0x57,
            "X" => 0x58,
            "Y" => 0x59,
            "Z" => 0x5A,
            "SPACE" => 0x20,
            "BACK" => 0x08,
            "ESC" => 0x1B,
            _ => 0x48 // Default to H
        };
    }

    /// <summary>
    /// Updates launch behavior warning message.
    /// </summary>
    partial void OnOnLaunchBehaviorChanged(LaunchBehavior value)
    {
        UpdateLaunchBehaviorWarning();
    }

    /// <summary>
    /// Updates the launch behavior warning.
    /// </summary>
    private void UpdateLaunchBehaviorWarning()
    {
        LaunchBehaviorWarning = OnLaunchBehavior == LaunchBehavior.ExitApp
            ? "Warning: The application will close completely when launching a game. You will need to manually restart it after the game exits."
            : null;
    }

    /// <summary>
    /// Saves all settings to database asynchronously.
    /// </summary>
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        IsSaving = true;
        ErrorMessage = null;
        SuccessMessage = null;

        try
        {
            // Get existing settings
            var settings = await _dbContext.UserSettings.FirstOrDefaultAsync();

            if (settings == null)
            {
                settings = new UserSettings();
                _dbContext.UserSettings.Add(settings);
            }

            // Update all settings from observable properties
            UpdateEntityFromSettings(settings);

            settings.LastModified = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();

            // Apply runtime settings
            ApplyRuntimeSettings();

            SuccessMessage = "Settings saved successfully";
            _logger.LogInformation("Settings saved successfully");

            // Clear success message after delay
            await Task.Delay(3000);
            SuccessMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving settings");
            ErrorMessage = $"Failed to save settings: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>
    /// Updates entity from observable properties.
    /// </summary>
    private void UpdateEntityFromSettings(UserSettings settings)
    {
        // Scraping
        settings.EnableBangumi = EnableBangumi;
        settings.EnableVndb = EnableVndb;
        settings.EnableYmgal = EnableYmgal;
        settings.EnableCngal = EnableCngal;
        settings.BangumiAccessToken = BangumiAccessToken;
        settings.BangumiUserId = BangumiUserId;
        settings.MatchThresholdPercent = MatchThreshold;
        settings.AutoScrapeOnAdd = AutoScrapeOnAdd;

        // Source priority JSON
        settings.SourcePriorityJson = JsonSerializer.Serialize(
            SourcePriorityList.Select(s => s.Name).ToList());

        // Launch behavior
        settings.OnLaunchBehavior = OnLaunchBehavior;

        // Exit behavior
        settings.OnExitBehavior = OnExitBehavior;
        settings.AutoBackupOnExit = AutoBackupOnExit;

        // Boss key
        settings.EnableBossKey = EnableBossKey;
        settings.BossKeyModifiers = GetBossKeyModifiers();
        settings.BossKeyVirtualKey = GetVirtualKeyFromKeyName(BossKeyKey);
        settings.MinimizeToTrayOnBossKey = MinimizeToTrayOnBossKey;
        settings.ShowBossKeyNotification = ShowBossKeyNotification;

        // Save paths
        settings.DefaultBackupPath = DefaultBackupPath;
        settings.UseCustomBackupPath = UseCustomBackupPath;
        settings.ScreenshotPath = ScreenshotPath;

        // Process monitoring
        settings.EnableAdvancedMonitoring = EnableAdvancedMonitoring;
        settings.AutoScreenshotOnExit = AutoScreenshotOnExit;
        settings.MonitoringIntervalMs = MonitoringIntervalMs;
        settings.ScreenshotFormat = ScreenshotFormat;
        settings.JpgQuality = JpgQuality;

        // Appearance
        settings.Theme = Theme;
        settings.Language = Language;
        settings.LibraryViewMode = LibraryViewMode;

        // Library
        settings.AutoScanOnStartup = AutoScanOnStartup;
        settings.DefaultScrapingSource = DefaultScrapingSource;
    }

    /// <summary>
    /// Gets boss key modifiers from toggle states.
    /// </summary>
    private BossKeyModifiers GetBossKeyModifiers()
    {
        var modifiers = BossKeyModifiers.None;
        if (BossKeyAlt) modifiers |= BossKeyModifiers.Alt;
        if (BossKeyCtrl) modifiers |= BossKeyModifiers.Ctrl;
        if (BossKeyShift) modifiers |= BossKeyModifiers.Shift;
        if (BossKeyWin) modifiers |= BossKeyModifiers.Win;
        return modifiers;
    }

    /// <summary>
    /// Applies runtime settings to services.
    /// </summary>
    private void ApplyRuntimeSettings()
    {
        try
        {
            // Update process monitor config
            var config = _processMonitorService.Config;
            config.EnableBossKey = EnableBossKey;
            config.BossKeyModifiers = (Services.BossKeyModifiers)GetBossKeyModifiers();
            config.BossKeyVirtualKey = GetVirtualKeyFromKeyName(BossKeyKey);
            config.MinimizeToTrayOnBossKey = MinimizeToTrayOnBossKey;
            config.ShowBossKeyNotification = ShowBossKeyNotification;
            config.EnableAdvancedMonitoring = EnableAdvancedMonitoring;
            config.AutoScreenshotOnExit = AutoScreenshotOnExit;
            config.MonitoringIntervalMs = MonitoringIntervalMs;
            config.ScreenshotFormat = (Services.ScreenshotFormat)ScreenshotFormat;
            config.JpgQuality = JpgQuality;

            _processMonitorService.UpdateConfig(config);

            // Update screenshot directory if custom path specified
            if (!string.IsNullOrWhiteSpace(ScreenshotPath))
            {
                _processMonitorService.SetScreenshotDirectory(ScreenshotPath);
            }

            _logger.LogInformation("Runtime settings applied successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply some runtime settings");
        }
    }

    /// <summary>
    /// Moves a source up in priority list.
    /// </summary>
    [RelayCommand]
    private void MoveSourceUp(SourcePriorityItem? item)
    {
        if (item == null) return;

        var index = SourcePriorityList.IndexOf(item);
        if (index > 0)
        {
            SourcePriorityList.Move(index, index - 1);
            _logger.LogDebug("Moved {Source} up in priority", item.Name);
        }
    }

    /// <summary>
    /// Moves a source down in priority list.
    /// </summary>
    [RelayCommand]
    private void MoveSourceDown(SourcePriorityItem? item)
    {
        if (item == null) return;

        var index = SourcePriorityList.IndexOf(item);
        if (index < SourcePriorityList.Count - 1)
        {
            SourcePriorityList.Move(index, index + 1);
            _logger.LogDebug("Moved {Source} down in priority", item.Name);
        }
    }

    /// <summary>
    /// Opens the data folder in Windows Explorer.
    /// </summary>
    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            var path = GetAppDataPath();
            if (System.IO.Directory.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                    Verb = "open"
                });
                _logger.LogInformation("Opened data folder: {Path}", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open data folder");
            ErrorMessage = $"Failed to open data folder: {ex.Message}";
        }
    }

    /// <summary>
    /// Opens GitHub repository in browser.
    /// </summary>
    [RelayCommand]
    private void OpenGitHub()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = GitHubUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open GitHub");
            ErrorMessage = $"Failed to open GitHub: {ex.Message}";
        }
    }

    /// <summary>
    /// Gets the application data path.
    /// </summary>
    private static string GetAppDataPath()
    {
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Galbox");
    }

    /// <summary>
    /// Gets the formatted boss key display string.
    /// </summary>
    public string BossKeyDisplay
    {
        get
        {
            var parts = new List<string>();
            if (BossKeyCtrl) parts.Add("Ctrl");
            if (BossKeyAlt) parts.Add("Alt");
            if (BossKeyShift) parts.Add("Shift");
            if (BossKeyWin) parts.Add("Win");
            parts.Add(BossKeyKey.ToUpperInvariant());
            return parts.Join(" + ");
        }
    }

    /// <summary>
    /// Called when BossKeyAlt changes - updates BossKeyDisplay.
    /// </summary>
    partial void OnBossKeyAltChanged(bool value)
    {
        OnPropertyChanged(nameof(BossKeyDisplay));
    }

    /// <summary>
    /// Called when BossKeyCtrl changes - updates BossKeyDisplay.
    /// </summary>
    partial void OnBossKeyCtrlChanged(bool value)
    {
        OnPropertyChanged(nameof(BossKeyDisplay));
    }

    /// <summary>
    /// Called when BossKeyShift changes - updates BossKeyDisplay.
    /// </summary>
    partial void OnBossKeyShiftChanged(bool value)
    {
        OnPropertyChanged(nameof(BossKeyDisplay));
    }

    /// <summary>
    /// Called when BossKeyWin changes - updates BossKeyDisplay.
    /// </summary>
    partial void OnBossKeyWinChanged(bool value)
    {
        OnPropertyChanged(nameof(BossKeyDisplay));
    }

    /// <summary>
    /// Called when BossKeyKey changes - updates BossKeyDisplay.
    /// </summary>
    partial void OnBossKeyKeyChanged(string value)
    {
        OnPropertyChanged(nameof(BossKeyDisplay));
    }
}

/// <summary>
/// Represents a source priority item for display in settings.
/// Implements ObservableObject for property change notifications.
/// </summary>
public partial class SourcePriorityItem : ObservableObject
{
    /// <summary>
    /// Source identifier name.
    /// </summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>
    /// Display name for UI.
    /// </summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>
    /// Whether this source is enabled.
    /// </summary>
    [ObservableProperty]
    private bool _isEnabled = true;
}