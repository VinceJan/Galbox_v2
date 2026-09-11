namespace Galbox.Core.Patches;

/// <summary>
/// Weak "is there something newer" hint. moyu publishes no structured version number, so the only honest
/// signal is "the provider says the resource changed after we installed it".
/// </summary>
public enum PatchUpdateHint
{
    /// <summary>No provider data; nothing can be said.</summary>
    Unknown = 0,

    /// <summary>The provider reported no timestamp newer than the install.</summary>
    NoNewerResourceKnown = 1,

    /// <summary>The provider's resource timestamp is newer than the local record. Worth showing, not a version verdict.</summary>
    MaybeUpdateAvailable = 2
}

/// <summary>Outcome of the timestamp comparison, with both timestamps surfaced so the user can judge.</summary>
public sealed class PatchUpdateAssessment
{
    /// <summary>The hint.</summary>
    public required PatchUpdateHint Hint { get; init; }

    /// <summary>Provider side <c>resource_updated_at</c>, when known.</summary>
    public DateTimeOffset? ProviderResourceUpdatedAt { get; init; }

    /// <summary>Timestamp the provider had when the patch was installed (recorded in the manifest).</summary>
    public DateTimeOffset? RecordedResourceUpdatedAt { get; init; }

    /// <summary>When Galbox installed the patch.</summary>
    public DateTimeOffset? InstalledAt { get; init; }

    /// <summary>Whether <see cref="Hint"/> is a fact or an inference.</summary>
    public PatchEvidenceClass Evidence { get; init; } = PatchEvidenceClass.Inference;

    /// <summary>Plain-language explanation, including the two timestamps.</summary>
    public required string Explanation { get; init; }
}

/// <summary>
/// Layer 4 of the status model. Deliberately does <b>not</b> compare versions: the research is explicit that
/// only a timestamp comparison is defensible, and the wording must be "might have an update".
/// </summary>
public static class PatchUpdateAdvisor
{
    /// <summary>
    /// Compares the provider's current resource timestamp with what was recorded at install time.
    /// </summary>
    /// <param name="providerResourceUpdatedAt">Current <c>resource_updated_at</c> from the provider, if any.</param>
    /// <param name="manifest">The installed patch being asked about.</param>
    public static PatchUpdateAssessment Assess(DateTimeOffset? providerResourceUpdatedAt, PatchManifest? manifest)
    {
        if (manifest is null)
        {
            return new PatchUpdateAssessment
            {
                Hint = PatchUpdateHint.Unknown,
                ProviderResourceUpdatedAt = providerResourceUpdatedAt,
                Explanation = "Galbox has no manifest for this patch, so there is nothing to compare against."
            };
        }

        var installedAt = manifest.CommittedAt ?? manifest.CreatedAt;
        DateTimeOffset? recorded = null;
        if (!string.IsNullOrWhiteSpace(manifest.Source?.ResourceUpdatedAt) &&
            DateTimeOffset.TryParse(manifest.Source!.ResourceUpdatedAt, out var parsed))
        {
            recorded = parsed;
        }

        if (providerResourceUpdatedAt is null)
        {
            return new PatchUpdateAssessment
            {
                Hint = PatchUpdateHint.Unknown,
                RecordedResourceUpdatedAt = recorded,
                InstalledAt = installedAt,
                Explanation = "The provider returned no resource timestamp, so Galbox cannot tell whether anything changed."
            };
        }

        var reference = recorded ?? installedAt;
        var newer = providerResourceUpdatedAt.Value > reference;

        return new PatchUpdateAssessment
        {
            Hint = newer ? PatchUpdateHint.MaybeUpdateAvailable : PatchUpdateHint.NoNewerResourceKnown,
            ProviderResourceUpdatedAt = providerResourceUpdatedAt,
            RecordedResourceUpdatedAt = recorded,
            InstalledAt = installedAt,
            Explanation = newer
                ? $"The provider says this resource was last updated {providerResourceUpdatedAt.Value:u}, which is later than the " +
                  $"{(recorded is null ? "installed" : "recorded")} timestamp {reference:u}. It might have an update - check the page before reinstalling. " +
                  "Galbox does not compare version numbers because moyu does not publish structured versions."
                : $"The provider's resource timestamp {providerResourceUpdatedAt.Value:u} is not newer than {reference:u}, so nothing suggests an update. " +
                  "This is a timestamp comparison, not a version check."
        };
    }
}
