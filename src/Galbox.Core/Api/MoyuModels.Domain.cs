using Galbox.Core.Patches;

namespace Galbox.Core.Api;

/// <summary>
/// A game page on moyu: the thing resources hang off.
///
/// <para>
/// This is the mapping from the wire DTO to something the rest of Galbox can use, and it is where
/// the "empty is not zero" rules live — an empty <c>name</c>, an unknown <c>type</c> and an
/// unparseable <c>size</c> all have a defined, honest representation rather than becoming blank.
/// </para>
/// </summary>
public sealed class MoyuPatch
{
    /// <summary>The moyu patch page id.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>moyu's own dedupe key, usually but not always a VNDB id.</summary>
    public string? VndbId { get; init; }

    /// <summary>The NextMoe catalog work, or null on a placeholder page.</summary>
    public string? CatalogWorkId { get; init; }

    /// <summary><c>sfw</c>, <c>nsfw</c>, or null when catalog has not rated it yet.</summary>
    public string? ContentLimit { get; init; }

    /// <summary>Release date as published (<c>YYYY-MM-DD</c>), kept verbatim.</summary>
    public string? ReleaseDate { get; init; }

    /// <summary>Upstream <c>type[]</c> values, verbatim.</summary>
    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();

    /// <summary>Declared languages.</summary>
    public IReadOnlyList<string> Languages { get; init; } = Array.Empty<string>();

    /// <summary>Declared platforms.</summary>
    public IReadOnlyList<string> Platforms { get; init; } = Array.Empty<string>();

    /// <summary>How many resources the page holds.</summary>
    public int ResourceCount { get; init; }

    /// <summary>Site download counter.</summary>
    public int DownloadCount { get; init; }

    /// <summary>Site view counter.</summary>
    public int ViewCount { get; init; }

    /// <summary>
    /// The page on moyu — where a reader goes to download. Null when the row carried none or it
    /// failed the URL guard.
    /// </summary>
    public string? WebUrl { get; init; }

    /// <summary>Page creation timestamp, verbatim RFC 3339.</summary>
    public string? CreatedAt { get; init; }

    /// <summary>Page edit timestamp, verbatim.</summary>
    public string? UpdatedAt { get; init; }

    /// <summary>
    /// When a resource on the page was last added or changed. The only honest basis for a
    /// "there may be an update" hint.
    /// </summary>
    public string? ResourceUpdatedAt { get; init; }

    /// <summary>Publisher, when it was requested.</summary>
    public MoyuUser? Publisher { get; init; }

    /// <summary>Resources, present only when the call asked to include them.</summary>
    public IReadOnlyList<MoyuResource> Resources { get; init; } = Array.Empty<MoyuResource>();

    /// <summary>Chinese display labels for <see cref="Types"/>.</summary>
    public IReadOnlyList<string> TypeLabels => Types.Select(MoyuPatchTypes.Describe).ToList();

    /// <summary>Maps a wire DTO.</summary>
    internal static MoyuPatch FromDto(MoyuPatchDto dto) => new()
    {
        Id = dto.Id ?? string.Empty,
        VndbId = Blank(dto.VndbId),
        CatalogWorkId = Blank(dto.CatalogWorkId),
        ContentLimit = Blank(dto.ContentLimit),
        ReleaseDate = Blank(dto.ReleaseDate),
        Types = dto.Type ?? new List<string>(),
        Languages = dto.Language ?? new List<string>(),
        Platforms = dto.Platform ?? new List<string>(),
        ResourceCount = dto.ResourceCount,
        DownloadCount = dto.DownloadCount,
        ViewCount = dto.ViewCount,
        WebUrl = MoyuComplianceGuard.IsAllowedWebUrl(dto.WebUrl) ? dto.WebUrl : null,
        CreatedAt = Blank(dto.CreatedAt),
        UpdatedAt = Blank(dto.UpdatedAt),
        ResourceUpdatedAt = Blank(dto.ResourceUpdatedAt),
        Publisher = dto.Publisher is null ? null : MoyuUser.FromDto(dto.Publisher),
        Resources = (dto.Resources ?? new List<MoyuResourceDto>()).Select(MoyuResource.FromDto).ToList()
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// One downloadable item on moyu.
///
/// <para>
/// It carries <b>no link, code or password</b>, and none may be added: the contract omits them by
/// design so that links cannot be harvested in bulk. <see cref="WebUrl"/> is the way to the file,
/// and the user's browser is what follows it.
/// </para>
/// </summary>
public sealed class MoyuResource
{
    /// <summary>The resource id, as a string (the contract keeps every id a string).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The patch page it belongs to.</summary>
    public string PatchId { get; init; } = string.Empty;

    /// <summary>
    /// The publisher's display name for this resource, possibly empty. Use <see cref="DisplayName"/>
    /// rather than this to render something.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// <c>s3</c> (moyu's own object store) or <c>user</c> (hosted by the publisher elsewhere).
    /// Recorded verbatim; unknown values are kept rather than mapped away.
    /// </summary>
    public string Storage { get; init; } = string.Empty;

    /// <summary>The upstream size string, verbatim (<c>"17.953 MB"</c>).</summary>
    public string SizeText { get; init; } = string.Empty;

    /// <summary>
    /// <see cref="SizeText"/> in bytes, using the calibrated binary unit; 0 when it could not be
    /// understood. 0 means "unknown", and the download matcher treats it that way.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>BLAKE3 of the file, or null when the row predates the field being recorded.</summary>
    public string? Hash { get; init; }

    /// <summary>Free-text model name for an AI translation, as the publisher typed it.</summary>
    public string? ModelName { get; init; }

    /// <summary>The localisation group's display name.</summary>
    public string LocalizationGroupName { get; init; } = string.Empty;

    /// <summary>Markdown note.</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>Upstream <c>type[]</c> values, verbatim.</summary>
    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();

    /// <summary>Declared languages.</summary>
    public IReadOnlyList<string> Languages { get; init; } = Array.Empty<string>();

    /// <summary>Declared platforms.</summary>
    public IReadOnlyList<string> Platforms { get; init; } = Array.Empty<string>();

    /// <summary>Site download counter.</summary>
    public int DownloadCount { get; init; }

    /// <summary>Site like counter.</summary>
    public int LikeCount { get; init; }

    /// <summary>Where the user must go to obtain the file.</summary>
    public string? WebUrl { get; init; }

    /// <summary>Publication timestamp, verbatim.</summary>
    public string? CreatedAt { get; init; }

    /// <summary>Change timestamp, verbatim.</summary>
    public string? UpdatedAt { get; init; }

    /// <summary>Publisher, when requested.</summary>
    public MoyuUser? Publisher { get; init; }

    /// <summary>
    /// A name that is never blank. The upstream <c>name</c> is frequently empty — the research
    /// report saw exactly that on a real row — so this falls back to the localisation group, then
    /// to the resource id. A blank line in the UI is not an acceptable rendering of "unnamed".
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name))
            {
                return Name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(LocalizationGroupName))
            {
                return $"{LocalizationGroupName.Trim()}（未命名资源）";
            }

            return $"未命名资源 #{Id}";
        }
    }

    /// <summary>Chinese display labels for <see cref="Types"/>.</summary>
    public IReadOnlyList<string> TypeLabels => Types.Select(MoyuPatchTypes.Describe).ToList();

    /// <summary>True when the publisher hosts the file somewhere else, which means a file-host page
    /// and possibly an extraction code rather than a direct download.</summary>
    public bool IsExternallyHosted => string.Equals(Storage, "user", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The identity to use for persistence. <c>patch_id</c> and the resource id are different id
    /// spaces, so the pair is what identifies a row.
    /// </summary>
    public string ExternalId => $"moyu:p{PatchId}:r{Id}";

    /// <summary>
    /// Provider metadata for the local patch engine's <see cref="PatchPreviewRequest.Source"/>, so
    /// a manifest records where the package came from and which page the user was sent to.
    /// </summary>
    public PatchSourceInfo ToPatchSourceInfo() => new()
    {
        Kind = "moyu-moe-v2",
        PatchId = PatchId,
        ResourceId = Id,
        WebUrl = WebUrl,
        ResourceUpdatedAt = UpdatedAt,
        Note = string.IsNullOrWhiteSpace(LocalizationGroupName) ? null : LocalizationGroupName
    };

    /// <summary>Maps a wire DTO.</summary>
    internal static MoyuResource FromDto(MoyuResourceDto dto) => new()
    {
        Id = dto.Id ?? string.Empty,
        PatchId = dto.PatchId ?? string.Empty,
        Name = dto.Name ?? string.Empty,
        Storage = dto.Storage ?? string.Empty,
        SizeText = dto.Size ?? string.Empty,
        SizeBytes = MoyuSize.ParseBytes(dto.Size),
        Hash = string.IsNullOrWhiteSpace(dto.Hash) ? null : dto.Hash,
        ModelName = string.IsNullOrWhiteSpace(dto.ModelName) ? null : dto.ModelName,
        LocalizationGroupName = dto.LocalizationGroupName ?? string.Empty,
        Note = dto.Note ?? string.Empty,
        Types = dto.Type ?? new List<string>(),
        Languages = dto.Language ?? new List<string>(),
        Platforms = dto.Platform ?? new List<string>(),
        DownloadCount = dto.DownloadCount,
        LikeCount = dto.LikeCount,
        WebUrl = MoyuComplianceGuard.IsAllowedWebUrl(dto.WebUrl) ? dto.WebUrl : null,
        CreatedAt = string.IsNullOrWhiteSpace(dto.CreatedAt) ? null : dto.CreatedAt,
        UpdatedAt = string.IsNullOrWhiteSpace(dto.UpdatedAt) ? null : dto.UpdatedAt,
        Publisher = dto.Publisher is null ? null : MoyuUser.FromDto(dto.Publisher)
    };
}

/// <summary>Who published a patch or resource.</summary>
public sealed class MoyuUser
{
    /// <summary>The publisher's id.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Avatar URL.</summary>
    public string? AvatarUrl { get; init; }

    /// <summary>Maps a wire DTO.</summary>
    internal static MoyuUser FromDto(MoyuUserDto dto) => new()
    {
        Id = dto.Id ?? string.Empty,
        Name = dto.Name ?? string.Empty,
        AvatarUrl = string.IsNullOrWhiteSpace(dto.AvatarUrl) ? null : dto.AvatarUrl
    };
}

/// <summary>
/// The upstream twelve-value <c>type[]</c> vocabulary, and its Chinese labels.
///
/// <para>
/// Kept here rather than added to <c>PatchRecord.PatchTypeDisplay</c> on purpose: that property is
/// shared with the patch-centre UI and currently understands four values
/// (<c>Translation/Fix/Adult/Other</c>). Extending a switch used elsewhere, from a service-layer
/// change, would be an unannounced behaviour change for a different workstream.
/// </para>
/// </summary>
public static class MoyuPatchTypes
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["manual"] = "人工翻译补丁",
        ["ai"] = "AI 翻译补丁",
        ["machine_polishing"] = "机翻润色",
        ["machine"] = "机翻补丁",
        ["save"] = "全 CG 存档",
        ["crack"] = "破解补丁",
        ["fix"] = "修正补丁",
        ["mod"] = "魔改补丁",
        ["r18"] = "18+",
        ["decensor"] = "去马赛克补丁",
        ["image"] = "修图补丁",
        ["other"] = "其它"
    };

    /// <summary>
    /// The Chinese label for an upstream value. An unrecognised value is returned as-is rather than
    /// relabelled "其它", because silently reclassifying an unknown value would hide a contract
    /// change.
    /// </summary>
    public static string Describe(string? upstreamValue)
        => string.IsNullOrWhiteSpace(upstreamValue)
            ? "未标注"
            : Labels.TryGetValue(upstreamValue.Trim(), out var label)
                ? label
                : upstreamValue.Trim();

    /// <summary>
    /// True when the resource is a save rather than a patch over the game directory.
    /// </summary>
    /// <remarks>
    /// A <c>save</c> resource must never be routed through the overwrite flow: the local engine
    /// rejects it by declared type (<c>PatchAcceptance.EnsureNotDeclaredSave</c>), and the save-node
    /// workstream owns what should happen instead. This helper exists so the service layer can make
    /// that routing decision without duplicating the vocabulary.
    /// </remarks>
    public static bool IsSaveType(IReadOnlyList<string>? types)
        => types is not null && types.Any(t => string.Equals(t, "save", StringComparison.OrdinalIgnoreCase));

    /// <summary>Every value the upstream vocabulary defines.</summary>
    public static IReadOnlyCollection<string> All => Labels.Keys;
}
