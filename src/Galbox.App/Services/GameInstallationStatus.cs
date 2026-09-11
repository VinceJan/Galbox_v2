using System.IO;
using Galbox.Data.Entities;

namespace Galbox.App.Services;

/// <summary>
/// Whether a game in the library can actually be played from where it is.
/// </summary>
public sealed class GameInstallationState
{
    /// <summary>Installation folder recorded in the library.</summary>
    public string InstallPath { get; set; } = string.Empty;

    /// <summary>Main executable recorded in the library.</summary>
    public string MainExecutable { get; set; } = string.Empty;

    /// <summary>Whether the recorded installation folder exists on disk.</summary>
    public bool FolderExists { get; set; }

    /// <summary>Whether the recorded main executable exists on disk.</summary>
    public bool ExecutableExists { get; set; }

    /// <summary>
    /// True when the game cannot be launched from the recorded paths. The library entry is still
    /// valid (metadata, saves, play time), so this is a repairable condition, not a broken record.
    /// </summary>
    public bool IsMissing { get; set; }

    /// <summary>Short badge text: "文件夹丢失", "可执行文件丢失" or "正常".</summary>
    public string StatusText { get; set; } = "正常";

    /// <summary>One-line explanation naming the path that is gone.</summary>
    public string DetailText { get; set; } = string.Empty;

    /// <summary>How the user can fix it.</summary>
    public string RepairHint { get; set; } = string.Empty;
}

/// <summary>
/// Evaluates whether the files a library entry points at still exist.
/// </summary>
/// <remarks>
/// The audit found a library entry (<c>SabbatOfTheWitch</c> -> <c>D:\GAME\SabbatOfTheWitch</c>)
/// whose folder had already been deleted, while the UI showed the game as healthy and the launch
/// button failed only with a generic error. Both the badge shown on the library card and the message
/// shown when a launch is blocked come from here, so the UI and the acceptance check cannot drift
/// apart.
/// </remarks>
public static class GameInstallationStatus
{
    /// <summary>Badge text for a missing installation folder.</summary>
    public const string FolderMissingBadge = "文件夹丢失";

    /// <summary>Badge text for a missing executable in an existing folder.</summary>
    public const string ExecutableMissingBadge = "可执行文件丢失";

    /// <summary>
    /// Evaluates a game and reports which of its paths exist.
    /// </summary>
    public static GameInstallationState Evaluate(GameInfo? game)
    {
        var state = new GameInstallationState();

        if (game == null)
        {
            state.IsMissing = true;
            state.StatusText = FolderMissingBadge;
            state.DetailText = "库中没有这条游戏记录。";
            state.RepairHint = "请刷新游戏库后重试。";
            return state;
        }

        state.InstallPath = game.InstallPath ?? string.Empty;
        state.MainExecutable = game.MainExecutable ?? string.Empty;

        state.FolderExists = !string.IsNullOrWhiteSpace(state.InstallPath) && Directory.Exists(state.InstallPath);
        state.ExecutableExists = !string.IsNullOrWhiteSpace(state.MainExecutable) && File.Exists(state.MainExecutable);

        if (string.IsNullOrWhiteSpace(state.InstallPath))
        {
            state.IsMissing = true;
            state.StatusText = FolderMissingBadge;
            state.DetailText = "这条记录没有保存游戏文件夹路径。";
            state.RepairHint = "请用「添加游戏」重新指向游戏文件夹，然后删除这条旧记录。";
            return state;
        }

        if (!state.FolderExists)
        {
            state.IsMissing = true;
            state.StatusText = FolderMissingBadge;
            state.DetailText = $"游戏文件夹不存在：{state.InstallPath}";
            state.RepairHint =
                "游戏文件没有被 Galbox 删掉——可能是被移动、重命名，或所在磁盘（移动硬盘/U 盘）未接入。"
                + "把文件夹放回原位置即可恢复；若路径已改变，请重新添加游戏，再删除这条记录。";
            return state;
        }

        if (string.IsNullOrWhiteSpace(state.MainExecutable))
        {
            state.IsMissing = true;
            state.StatusText = ExecutableMissingBadge;
            state.DetailText = $"文件夹存在（{state.InstallPath}），但没有记录启动用的可执行文件。";
            state.RepairHint = "请重新添加该游戏文件夹，让 Galbox 重新识别主程序。";
            return state;
        }

        if (!state.ExecutableExists)
        {
            state.IsMissing = true;
            state.StatusText = ExecutableMissingBadge;
            state.DetailText = $"启动程序不存在：{state.MainExecutable}";
            state.RepairHint =
                "游戏文件夹仍在，但主程序文件不见了（可能被杀软隔离或被手动删除）。"
                + "请确认文件存在，或重新添加游戏文件夹以重新识别主程序。";
            return state;
        }

        state.IsMissing = false;
        state.StatusText = "正常";
        state.DetailText = $"游戏文件齐全：{state.InstallPath}";
        state.RepairHint = string.Empty;
        return state;
    }

    /// <summary>
    /// Builds the message shown when a launch has to be refused.
    /// </summary>
    /// <returns>
    /// <c>null</c> when nothing blocks the launch; otherwise a message that names the missing path
    /// and tells the user what to do.
    /// </returns>
    public static string? BuildLaunchBlockMessage(GameInfo? game)
    {
        var state = Evaluate(game);
        if (!state.IsMissing)
        {
            return null;
        }

        return $"无法启动「{game?.DisplayName ?? "该游戏"}」：{state.StatusText}。\n{state.DetailText}\n{state.RepairHint}";
    }
}
