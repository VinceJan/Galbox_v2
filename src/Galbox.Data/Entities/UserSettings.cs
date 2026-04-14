using System.ComponentModel.DataAnnotations;

namespace Galbox.Data.Entities;

/// <summary>
/// Represents user settings/preferences for Galbox application.
/// Stored in database and persisted across sessions.
/// </summary>
public class UserSettings
{
    /// <summary>
    /// Unique identifier for settings (singleton record, ID = 1).
    /// </summary>
    [Key]
    public int Id { get; set; } = 1;

    #region Scraping Settings

    /// <summary>
    /// Whether Bangumi scraping source is enabled.
    /// </summary>
    public bool EnableBangumi { get; set; } = true;

    /// <summary>
    /// Whether VNDB scraping source is enabled.
    /// </summary>
    public bool EnableVndb { get; set; } = true;

    /// <summary>
    /// Whether ymgal scraping source is enabled.
    /// </summary>
    public bool EnableYmgal { get; set; } = true;

    /// <summary>
    /// Whether cngal scraping source is enabled.
    /// </summary>
    public bool EnableCngal { get; set; } = true;

    /// <summary>
    /// Bangumi API access token (for OAuth or API key authentication).
    /// </summary>
    [MaxLength(500)]
    public string? BangumiAccessToken { get; set; }

    /// <summary>
    /// Bangumi user ID (after OAuth login).
    /// </summary>
    [MaxLength(100)]
    public string? BangumiUserId { get; set; }

    /// <summary>
    /// Source priority order (JSON serialized list of source names).
    /// Default: ["Bangumi", "VNDB", "ymgal", "cngal"]
    /// </summary>
    public string? SourcePriorityJson { get; set; } = "[\"Bangumi\",\"VNDB\",\"ymgal\",\"cngal\"]";

    /// <summary>
    /// Match threshold percentage for auto-accepting scraped data (0-100).
    /// Default: 90% auto-accept.
    /// </summary>
    public int MatchThresholdPercent { get; set; } = 90;

    /// <summary>
    /// Whether to auto-scrape metadata for newly added games.
    /// </summary>
    public bool AutoScrapeOnAdd { get; set; } = true;

    #endregion

    #region Launch Behavior

    /// <summary>
    /// Behavior when launching a game.
    /// Options: NoAction, Minimize, MinimizeToTray, ExitApp.
    /// </summary>
    public LaunchBehavior OnLaunchBehavior { get; set; } = LaunchBehavior.Minimize;

    #endregion

    #region Exit Behavior

    /// <summary>
    /// Behavior when game process exits.
    /// Options: MaximizeToDesktop, StayInTray, Minimize.
    /// </summary>
    public ExitBehavior OnExitBehavior { get; set; } = ExitBehavior.MaximizeToDesktop;

    /// <summary>
    /// Whether to auto-backup saves when game exits.
    /// </summary>
    public bool AutoBackupOnExit { get; set; } = false;

    #endregion

    #region Boss Key Settings

    /// <summary>
    /// Whether boss key feature is enabled.
    /// </summary>
    public bool EnableBossKey { get; set; } = true;

    /// <summary>
    /// Boss key modifiers (Alt, Ctrl, Shift, Win combination).
    /// </summary>
    public BossKeyModifiers BossKeyModifiers { get; set; } = BossKeyModifiers.Alt | BossKeyModifiers.Shift;

    /// <summary>
    /// Boss key virtual key code (e.g., 0x48 for 'H').
    /// </summary>
    public uint BossKeyVirtualKey { get; set; } = 0x48; // 'H'

    /// <summary>
    /// Whether to minimize to system tray when boss key is activated.
    /// </summary>
    public bool MinimizeToTrayOnBossKey { get; set; } = true;

    /// <summary>
    /// Whether to show notification when boss key is activated.
    /// </summary>
    public bool ShowBossKeyNotification { get; set; } = true;

    #endregion

    #region Save Path Settings

    /// <summary>
    /// Default backup storage path for save backups.
    /// If empty, uses default app data path.
    /// </summary>
    [MaxLength(2000)]
    public string DefaultBackupPath { get; set; } = string.Empty;

    /// <summary>
    /// Whether to use custom backup path (instead of default).
    /// </summary>
    public bool UseCustomBackupPath { get; set; } = false;

    /// <summary>
    /// Screenshot storage path.
    /// If empty, uses default app data path.
    /// </summary>
    [MaxLength(2000)]
    public string ScreenshotPath { get; set; } = string.Empty;

    #endregion

    #region Process Monitoring

    /// <summary>
    /// Whether advanced monitoring (CPU/memory tracking) is enabled.
    /// </summary>
    public bool EnableAdvancedMonitoring { get; set; } = false;

    /// <summary>
    /// Whether to auto-capture screenshot when game exits.
    /// </summary>
    public bool AutoScreenshotOnExit { get; set; } = false;

    /// <summary>
    /// Monitoring interval in milliseconds.
    /// Default: 1000ms (1 second).
    /// </summary>
    public int MonitoringIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Screenshot format (PNG or JPG).
    /// </summary>
    public ScreenshotFormat ScreenshotFormat { get; set; } = ScreenshotFormat.Png;

    /// <summary>
    /// JPG quality (1-100), only used when ScreenshotFormat is JPG.
    /// </summary>
    public int JpgQuality { get; set; } = 90;

    #endregion

    #region Appearance

    /// <summary>
    /// Application theme preference.
    /// Options: Default (follow system), Dark, Light.
    /// </summary>
    public AppTheme Theme { get; set; } = AppTheme.Default;

    /// <summary>
    /// Application language preference.
    /// Options: ChineseSimplified, English, Japanese.
    /// </summary>
    public AppLanguage Language { get; set; } = AppLanguage.ChineseSimplified;

    /// <summary>
    /// Default view mode for library (Grid or Table).
    /// </summary>
    public LibraryViewMode LibraryViewMode { get; set; } = LibraryViewMode.Grid;

    #endregion

    #region Library Settings

    /// <summary>
    /// Whether to auto-scan for new games on startup.
    /// </summary>
    public bool AutoScanOnStartup { get; set; } = false;

    /// <summary>
    /// Default source for metadata scraping.
    /// </summary>
    [MaxLength(50)]
    public string DefaultScrapingSource { get; set; } = "Bangumi";

    #endregion

    #region Timestamps

    /// <summary>
    /// When settings were last modified.
    /// </summary>
    public DateTime LastModified { get; set; } = DateTime.UtcNow;

    #endregion
}

/// <summary>
/// Behavior options when launching a game.
/// </summary>
public enum LaunchBehavior
{
    /// <summary>
    /// No action - keep window as is.
    /// </summary>
    NoAction = 0,

    /// <summary>
    /// Minimize the app window.
    /// </summary>
    Minimize = 1,

    /// <summary>
    /// Minimize to system tray.
    /// </summary>
    MinimizeToTray = 2,

    /// <summary>
    /// Exit the application entirely.
    /// </summary>
    ExitApp = 3
}

/// <summary>
/// Behavior options when game process exits.
/// </summary>
public enum ExitBehavior
{
    /// <summary>
    /// Maximize/restore app window to desktop.
    /// </summary>
    MaximizeToDesktop = 0,

    /// <summary>
    /// Keep app in system tray (if minimized to tray).
    /// </summary>
    StayInTray = 1,

    /// <summary>
    /// Minimize the app window.
    /// </summary>
    Minimize = 2
}

/// <summary>
/// Boss key modifier flags.
/// </summary>
[Flags]
public enum BossKeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8
}

/// <summary>
/// Screenshot format options.
/// </summary>
public enum ScreenshotFormat
{
    /// <summary>
    /// PNG format (lossless, larger file size).
    /// </summary>
    Png = 0,

    /// <summary>
    /// JPG format (lossy, smaller file size).
    /// </summary>
    Jpg = 1
}

/// <summary>
/// Application theme options.
/// </summary>
public enum AppTheme
{
    /// <summary>
    /// Follow system theme.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Always use dark theme.
    /// </summary>
    Dark = 1,

    /// <summary>
    /// Always use light theme.
    /// </summary>
    Light = 2
}

/// <summary>
/// Application language options.
/// </summary>
public enum AppLanguage
{
    /// <summary>
    /// Chinese (Simplified).
    /// </summary>
    ChineseSimplified = 0,

    /// <summary>
    /// English.
    /// </summary>
    English = 1,

    /// <summary>
    /// Japanese.
    /// </summary>
    Japanese = 2
}

/// <summary>
/// Library view mode options.
/// </summary>
public enum LibraryViewMode
{
    /// <summary>
    /// Grid view with cover images.
    /// </summary>
    Grid = 0,

    /// <summary>
    /// Table view with details.
    /// </summary>
    Table = 1
}