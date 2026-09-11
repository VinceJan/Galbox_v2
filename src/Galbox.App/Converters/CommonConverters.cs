using Galbox.App.ViewModels;
using Galbox.Data.Entities;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace Galbox.App.Converters;

/// <summary>
/// Interface for multi-value converters (WinUI3 doesn't provide built-in IMultiValueConverter).
/// </summary>
public interface IMultiValueConverter
{
    object Convert(object[] values, Type targetType, object parameter, string language);
    object[] ConvertBack(object value, Type[] targetTypes, object parameter, string language);
}

/// <summary>
/// Converts a boolean value to its inverse (true -> false, false -> true).
/// </summary>
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is bool boolValue)
        {
            return !boolValue;
        }
        return value;
    }
}

/// <summary>
/// Converts a boolean value to Visibility (true -> Visible, false -> Collapsed).
/// </summary>
public class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is Visibility visibility)
        {
            return visibility == Visibility.Visible;
        }
        return false;
    }
}

/// <summary>
/// Converts a boolean value to Visibility (true -> Collapsed, false -> Visible).
/// Inverse of BooleanToVisibilityConverter.
/// </summary>
public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is Visibility visibility)
        {
            return visibility != Visibility.Visible;
        }
        return true;
    }
}

/// <summary>
/// Converts null to Visibility (null -> Collapsed, non-null -> Visible).
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value == null ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts null to Visibility (null -> Visible, non-null -> Collapsed).
/// </summary>
public class InverseNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value == null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts an empty string to Visibility (empty/null -> Collapsed, non-empty -> Visible).
/// </summary>
public class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string str)
        {
            return string.IsNullOrWhiteSpace(str) ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a collection count to Visibility (count > 0 -> Visible, count == 0 -> Collapsed).
/// </summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int count)
        {
            return count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a collection count to Visibility (count == 0 -> Visible, count > 0 -> Collapsed).
/// Inverse of CountToVisibilityConverter - useful for showing empty states.
/// </summary>
public class InverseCountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int count)
        {
            return count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a DateTime to a formatted string for display.
/// </summary>
public class DateTimeFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DateTime dateTime)
        {
            var format = parameter as string ?? "yyyy-MM-dd";
            return dateTime.ToString(format);
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a file path to a URI for image display.
/// Handles both local file paths and URLs.
/// </summary>
public class PathToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return DependencyProperty.UnsetValue;
            }

            // Handle URLs
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(path);
            }

            // Handle local file paths
            if (System.IO.File.Exists(path))
            {
                return new Uri(path);
            }
        }
        return DependencyProperty.UnsetValue;
    }

    public object? ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a size in bytes to a human-readable format (KB, MB, GB).
/// </summary>
public class ByteSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is long bytes)
        {
            const long GB = 1024 * 1024 * 1024;
            const long MB = 1024 * 1024;
            const long KB = 1024;

            if (bytes >= GB)
            {
                return $"{bytes / GB:F2} GB";
            }
            if (bytes >= MB)
            {
                return $"{bytes / MB:F1} MB";
            }
            if (bytes >= KB)
            {
                return $"{bytes / KB:F0} KB";
            }
            return $"{bytes} B";
        }
        return "0 B";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a duration in seconds to a formatted time string.
/// </summary>
public class DurationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is long seconds)
        {
            var hours = seconds / 3600;
            var minutes = (seconds % 3600) / 60;
            var secs = seconds % 60;

            if (hours > 0)
            {
                return $"{hours}:{minutes:D2}:{secs:D2}";
            }
            return $"{minutes}:{secs:D2}";
        }
        return "0:00";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a rating (0-10) to a display format.
/// </summary>
public class RatingConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double rating)
        {
            return $"{rating:F1}/10";
        }
        return "无评分";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Multi-value converter that returns Visibility based on multiple conditions.
/// </summary>
public class MultiBooleanToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, string language)
    {
        // All values must be true for Visible
        foreach (var value in values)
        {
            if (value is bool boolValue && !boolValue)
            {
                return Visibility.Collapsed;
            }
        }
        return Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts an integer count to a "show more" visibility (count > threshold -> Visible).
/// </summary>
public class CountThresholdToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int count)
        {
            var threshold = 3; // Default threshold
            if (parameter is string thresholdStr && int.TryParse(thresholdStr, out var parsedThreshold))
            {
                threshold = parsedThreshold;
            }
            return count > threshold ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a boolean value to an icon glyph (true -> checkmark, false -> circle).
/// Used for showing default executable indicator.
/// </summary>
public class BooleanToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool b && b ? "\uE73E" : "\uE739";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts an empty string to boolean (empty/null -> false, non-empty -> true).
/// Used for InfoBar.IsOpen binding.
/// </summary>
public class EmptyStringToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string str)
        {
            return !string.IsNullOrWhiteSpace(str);
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Shows an element only when the bound game's installation is unusable (folder or executable
/// missing). Accepts a <see cref="GameInfo"/> or a boolean.
/// </summary>
/// <remarks>
/// The decision itself lives in <see cref="Galbox.App.Services.GameInstallationStatus"/>, so the
/// badge, the detail page, the launch guard and the acceptance check all agree on what "missing"
/// means.
/// </remarks>
public class MissingInstallationToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isMissing = value switch
        {
            GameInfo game => Galbox.App.Services.GameInstallationStatus.Evaluate(game).IsMissing,
            bool flag => flag,
            _ => false
        };

        // An inverted parameter makes this converter usable for the "healthy" case too.
        if (parameter is string text && text.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            isMissing = !isMissing;
        }

        return isMissing ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Produces the short badge text ("文件夹丢失" / "可执行文件丢失") for a game whose installation
/// cannot be found.
/// </summary>
public class MissingInstallationBadgeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is GameInfo game)
        {
            return Galbox.App.Services.GameInstallationStatus.Evaluate(game).StatusText;
        }

        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Produces the tooltip shown on a game whose installation cannot be found: which path is gone and
/// what the user can do about it.
/// </summary>
public class MissingInstallationTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is GameInfo game)
        {
            var state = Galbox.App.Services.GameInstallationStatus.Evaluate(game);
            return state.IsMissing
                ? $"{state.StatusText}\n{state.DetailText}\n{state.RepairHint}"
                : state.DetailText;
        }

        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts GameStatusFilter enum to ComboBox SelectedIndex.
/// </summary>
public class StatusFilterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is GameStatusFilter filter)
        {
            return (int)filter;
        }
        return 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is int index)
        {
            return (GameStatusFilter)index;
        }
        return GameStatusFilter.All;
    }
}

/// <summary>
/// Converts SortOption enum to ComboBox SelectedIndex.
/// </summary>
public class SortOptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is SortOption sort)
        {
            return (int)sort;
        }
        return 0; // AddedTimeDesc default
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is int index)
        {
            return (SortOption)index;
        }
        return SortOption.AddedTimeDesc;
    }
}

/// <summary>
/// Converts GameInfo to its status display text.
/// </summary>
public class GameStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is GameInfo game)
        {
            return LibraryViewModel.GetGameStatus(game);
        }
        return "未知";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts PatchTypeFilter enum to ComboBox SelectedIndex.
/// </summary>
public class PatchTypeFilterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is PatchTypeFilter filter)
        {
            return (int)filter;
        }
        return 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is int index)
        {
            return (PatchTypeFilter)index;
        }
        return PatchTypeFilter.All;
    }
}

/// <summary>
/// Converts PatchStatus to Visibility based on status value.
/// Parameter specifies the expected status to show visibility.
/// </summary>
public class PatchStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is PatchStatus status)
        {
            var expectedStatus = parameter as string;

            if (expectedStatus == "Available")
            {
                return status == PatchStatus.Available ? Visibility.Visible : Visibility.Collapsed;
            }
            if (expectedStatus == "Downloaded")
            {
                return status == PatchStatus.Downloaded || status == PatchStatus.Available
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            if (expectedStatus == "Installed")
            {
                return status == PatchStatus.Installed ? Visibility.Visible : Visibility.Collapsed;
            }
            if (expectedStatus == "Downloading")
            {
                return status == PatchStatus.Downloading || status == PatchStatus.Installing
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts PatchType string to appropriate color brush for badge.
/// </summary>
public class PatchTypeToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        // Return color brush based on patch type
        var app = Microsoft.UI.Xaml.Application.Current;
        if (app != null)
        {
            var resources = app.Resources;

            if (value is string patchType)
            {
                return patchType switch
                {
                    "Translation" => resources["TranslationPatchColor"] as Microsoft.UI.Xaml.Media.SolidColorBrush
                        ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green),
                    "Fix" => resources["FixPatchColor"] as Microsoft.UI.Xaml.Media.SolidColorBrush
                        ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Blue),
                    "Adult" => resources["AdultPatchColor"] as Microsoft.UI.Xaml.Media.SolidColorBrush
                        ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Pink),
                    "Other" => resources["OtherPatchColor"] as Microsoft.UI.Xaml.Media.SolidColorBrush
                        ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                    _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray)
                };
            }
        }
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts LaunchBehavior enum to ComboBox SelectedIndex.
/// </summary>
public class LaunchBehaviorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is Galbox.Data.Entities.LaunchBehavior behavior)
        {
            return (int)behavior;
        }
        return 1; // Default: Minimize
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is int index)
        {
            return (Galbox.Data.Entities.LaunchBehavior)index;
        }
        return Galbox.Data.Entities.LaunchBehavior.Minimize;
    }
}

/// <summary>
/// Converts ExitBehavior enum to ComboBox SelectedIndex.
/// </summary>
public class ExitBehaviorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is Galbox.Data.Entities.ExitBehavior behavior)
        {
            return (int)behavior;
        }
        return 0; // Default: MaximizeToDesktop
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is int index)
        {
            return (Galbox.Data.Entities.ExitBehavior)index;
        }
        return Galbox.Data.Entities.ExitBehavior.MaximizeToDesktop;
    }
}

/// <summary>
/// Converts AppTheme enum to ComboBox SelectedIndex.
/// </summary>
public class AppThemeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is Galbox.Data.Entities.AppTheme theme)
        {
            return (int)theme;
        }
        return 0; // Default: System Default
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is int index)
        {
            return (Galbox.Data.Entities.AppTheme)index;
        }
        return Galbox.Data.Entities.AppTheme.Default;
    }
}

/// <summary>
/// Converts AppLanguage enum to ComboBox SelectedIndex.
/// </summary>
public class AppLanguageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is Galbox.Data.Entities.AppLanguage lang)
        {
            return (int)lang;
        }
        return 0; // Default: Chinese Simplified
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is int index)
        {
            return (Galbox.Data.Entities.AppLanguage)index;
        }
        return Galbox.Data.Entities.AppLanguage.ChineseSimplified;
    }
}

/// <summary>
/// Converts LibraryViewMode enum to ComboBox SelectedIndex.
/// </summary>
public class LibraryViewModeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is Galbox.Data.Entities.LibraryViewMode mode)
        {
            return (int)mode;
        }
        return 0; // Default: Grid
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is int index)
        {
            return (Galbox.Data.Entities.LibraryViewMode)index;
        }
        return Galbox.Data.Entities.LibraryViewMode.Grid;
    }
}

/// <summary>
/// Converts ScreenshotFormat enum to ComboBox SelectedIndex.
/// </summary>
public class ScreenshotFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is Galbox.Data.Entities.ScreenshotFormat format)
        {
            return (int)format;
        }
        return 0; // Default: PNG
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is int index)
        {
            return (Galbox.Data.Entities.ScreenshotFormat)index;
        }
        return Galbox.Data.Entities.ScreenshotFormat.Png;
    }
}

/// <summary>
/// Converts BossKeyModifiers flags to boolean values for checkboxes.
/// Parameter: the modifier name (Alt, Ctrl, Shift, Win).
/// </summary>
public class BossKeyModifierConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is Galbox.Data.Entities.BossKeyModifiers modifiers && parameter is string modifierName)
        {
            var targetModifier = modifierName switch
            {
                "Alt" => Galbox.Data.Entities.BossKeyModifiers.Alt,
                "Ctrl" => Galbox.Data.Entities.BossKeyModifiers.Ctrl,
                "Shift" => Galbox.Data.Entities.BossKeyModifiers.Shift,
                "Win" => Galbox.Data.Entities.BossKeyModifiers.Win,
                _ => Galbox.Data.Entities.BossKeyModifiers.None
            };

            return modifiers.HasFlag(targetModifier);
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        // This converter doesn't support two-way binding directly
        // Use individual boolean properties instead
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts ScreenshotFormat to visibility (shows JPG quality slider only when format is JPG).
/// </summary>
public class SettingsScreenshotFormatToJpgVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is Galbox.Data.Entities.ScreenshotFormat format)
        {
            return format == Galbox.Data.Entities.ScreenshotFormat.Jpg
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts version string to formatted version text.
/// </summary>
public class SettingsVersionTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string version)
        {
            return $"版本 {version}";
        }
        return "版本 1.0.0";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts DefaultScrapingSource string to ComboBox SelectedIndex.
/// Maps: "Bangumi" -> 0, "VNDB" -> 1, "ymgal" -> 2, "cngal" -> 3
/// </summary>
public class DefaultScrapingSourceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string source)
        {
            return source switch
            {
                "Bangumi" => 0,
                "VNDB" => 1,
                "ymgal" => 2,
                "cngal" => 3,
                _ => 0 // Default to Bangumi
            };
        }
        return 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is int index)
        {
            return index switch
            {
                0 => "Bangumi",
                1 => "VNDB",
                2 => "ymgal",
                3 => "cngal",
                _ => "Bangumi"
            };
        }
        return "Bangumi";
    }
}

/// <summary>
/// Shows the Chinese name of a severity. Accepts either an <see cref="Galbox.App.Services.ErrorSeverity"/>
/// (a freshly detected finding) or its stored name (a row loaded from the ErrorRecords table).
/// </summary>
/// <remarks>
/// The mapping itself lives in <see cref="Galbox.App.Models.DiagnosisText"/> so that the model, this
/// converter and the acceptance check all read the same table.
/// </remarks>
public class ErrorSeverityDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            Galbox.App.Services.ErrorSeverity severity => Galbox.App.Models.DiagnosisText.SeverityLabel(severity),
            string stored => Galbox.App.Models.DiagnosisText.StoredSeverityLabel(stored),
            _ => string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Shows the Chinese name of a diagnosis category. Accepts either an
/// <see cref="Galbox.App.Services.ErrorCategory"/> or its stored name.
/// </summary>
public class ErrorCategoryDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            Galbox.App.Services.ErrorCategory category => Galbox.App.Models.DiagnosisText.CategoryLabel(category),
            string stored => Galbox.App.Models.DiagnosisText.StoredCategoryLabel(stored),
            _ => string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Shows the Chinese name of a solution level. Accepts either a
/// <see cref="Galbox.App.Services.SolutionType"/> or its stored name.
/// </summary>
public class ErrorSolutionTypeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            Galbox.App.Services.SolutionType solutionType => Galbox.App.Models.DiagnosisText.SolutionTypeLabel(solutionType),
            string stored => Galbox.App.Models.DiagnosisText.StoredSolutionTypeLabel(stored),
            _ => string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}