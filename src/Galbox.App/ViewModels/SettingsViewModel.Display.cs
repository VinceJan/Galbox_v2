using Galbox.Data.Entities;

namespace Galbox.App.ViewModels;

/// <summary>
/// SettingsViewModel partial class for display and appearance settings.
/// Contains all display/theme/language related functionality.
/// </summary>
public partial class SettingsViewModel
{
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
            return string.Join(" + ", parts);
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
            ? "警告：选择此选项时，应用将在启动游戏时完全关闭。游戏退出后需要手动重新启动应用。"
            : null;
    }
}