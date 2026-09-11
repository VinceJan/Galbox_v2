using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Galbox.Data.Entities;
using Microsoft.Extensions.Logging;

namespace Galbox.App.ViewModels;

/// <summary>
/// SettingsViewModel partial class for scraping source settings.
/// Contains all scraping source related functionality.
/// </summary>
public partial class SettingsViewModel
{
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
}