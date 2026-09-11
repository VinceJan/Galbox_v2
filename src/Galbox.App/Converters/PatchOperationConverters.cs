using Galbox.Core.Patches;
using Microsoft.UI.Xaml.Data;

namespace Galbox.App.Converters;

/// <summary>
/// Renders the engine's per-file install outcome in Chinese.
///
/// The engine's own strings (<c>Ok</c>, <c>Skipped</c>, ...) are deliberately kept out of the list the
/// user reads: "installation succeeded" and "the file was skipped because you already had it" are
/// different promises, and the status column is where the user can tell them apart.
/// </summary>
public sealed class PatchOperationStatusTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        PatchFileOperationStatus.Ok => "成功",
        PatchFileOperationStatus.ReadBackMismatch => "回读不符",
        PatchFileOperationStatus.Skipped => "已跳过",
        PatchFileOperationStatus.Failed => "失败",
        _ => value?.ToString() ?? string.Empty
    };

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Renders the engine's planned per-file action in Chinese.</summary>
public sealed class PatchOperationActionTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        PatchPreviewAction.Create => "新增",
        PatchPreviewAction.Overwrite => "覆盖",
        PatchPreviewAction.Conflict => "冲突后覆盖",
        PatchPreviewAction.Unchanged => "无需改动",
        PatchPreviewAction.Rejected => "已拒绝",
        _ => value?.ToString() ?? string.Empty
    };

    /// <inheritdoc />
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
