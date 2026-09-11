using System.Text.Json.Serialization;

namespace Galbox.Core.Api;

/// <summary>
/// DTOs for the official NextMoe "moyu face", <c>/v2/moyu/*</c>.
///
/// <para>
/// The shapes and — importantly — the <b>absences</b> follow
/// <c>https://developer.nextmoe.dev/specs/moyu-openapi.yaml</c> (version 1.0.0,
/// <c>info.x-stability: stable</c>), which is the contract the research report §5.2 quotes.
/// </para>
///
/// <para>
/// <b>There is no download link, share code or password in any of these types, and none may be
/// added.</b> The spec states the omission is deliberate: revealing a link on moyu is a separate,
/// rate-limited, per-resource request whose purpose is that links cannot be harvested in bulk.
/// Every row carries <see cref="MoyuPatch.WebUrl"/> / <see cref="MoyuResource.WebUrl"/> instead —
/// send a reader there. That is the whole interaction model of this integration, and it is why the
/// download half of the feature is a browser hop plus a downloads-folder watcher.
/// </para>
///
/// <para>
/// Every id is a <b>string</b> in the contract, and <c>patch_id</c> / <c>vndb_id</c> /
/// <c>catalog_work_id</c> are three different id spaces for the same game. They are kept as strings
/// here so no accidental numeric comparison can suggest otherwise.
/// </para>
/// </summary>
public sealed class MoyuPatchListDto
{
    /// <summary>Always <c>"list"</c>.</summary>
    [JsonPropertyName("object")]
    public string? Object { get; set; }

    /// <summary>The rows.</summary>
    [JsonPropertyName("items")]
    public List<MoyuPatchDto>? Items { get; set; }

    /// <summary>Pass as <c>cursor</c> for the next page; null on the last page.</summary>
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    /// <summary>Null unless <c>include_total=true</c> was sent.</summary>
    [JsonPropertyName("total")]
    public int? Total { get; set; }

    /// <summary>
    /// Only present on the <c>ids</c>/<c>refs</c> lane: the anchors that matched nothing, in the
    /// spelling that was sent. Normal information, not an error.
    /// </summary>
    [JsonPropertyName("missing")]
    public List<string>? Missing { get; set; }
}

/// <summary>
/// One game page on moyu — the thing resources hang off, not the game itself.
/// </summary>
public sealed class MoyuPatchDto
{
    /// <summary>Always <c>"patch"</c>.</summary>
    [JsonPropertyName("object")]
    public string? Object { get; set; }

    /// <summary>The moyu patch id. Not a catalog work id and not a VNDB number.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// moyu's own dedupe key. Usually a VNDB id, but a page created before its game reached VNDB
    /// carries a placeholder instead — so this is a lookup key, not a guarantee about the game.
    /// </summary>
    [JsonPropertyName("vndb_id")]
    public string? VndbId { get; set; }

    /// <summary>The NextMoe catalog work this page is about, or null on a placeholder page.</summary>
    [JsonPropertyName("catalog_work_id")]
    public string? CatalogWorkId { get; set; }

    /// <summary>Catalog's display verdict: <c>sfw</c>, <c>nsfw</c>, or null when not mirrored yet.</summary>
    [JsonPropertyName("content_limit")]
    public string? ContentLimit { get; set; }

    /// <summary>Release date, <c>YYYY-MM-DD</c>.</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    /// <summary>What kinds of patch the page's resources are.</summary>
    [JsonPropertyName("type")]
    public List<string>? Type { get; set; }

    /// <summary>Declared languages.</summary>
    [JsonPropertyName("language")]
    public List<string>? Language { get; set; }

    /// <summary>Declared platforms.</summary>
    [JsonPropertyName("platform")]
    public List<string>? Platform { get; set; }

    /// <summary>How many resources hang off this page.</summary>
    [JsonPropertyName("resource_count")]
    public int ResourceCount { get; set; }

    /// <summary>Site download counter.</summary>
    [JsonPropertyName("download_count")]
    public int DownloadCount { get; set; }

    /// <summary>Site view counter.</summary>
    [JsonPropertyName("view_count")]
    public int ViewCount { get; set; }

    /// <summary>Site favourite counter.</summary>
    [JsonPropertyName("favorite_count")]
    public int FavoriteCount { get; set; }

    /// <summary>Site comment counter.</summary>
    [JsonPropertyName("comment_count")]
    public int CommentCount { get; set; }

    /// <summary>
    /// The page on moyu. <b>This is where a reader goes to download.</b> It is the only download
    /// route this face offers, by design.
    /// </summary>
    [JsonPropertyName("web_url")]
    public string? WebUrl { get; set; }

    /// <summary>When the page was created.</summary>
    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    /// <summary>When the page was last edited.</summary>
    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }

    /// <summary>
    /// When a resource on the page was last added or changed. This is the only honest basis for a
    /// "there might be an update" hint.
    /// </summary>
    [JsonPropertyName("resource_updated_at")]
    public string? ResourceUpdatedAt { get; set; }

    /// <summary>Only with <c>include=publisher</c>.</summary>
    [JsonPropertyName("publisher")]
    public MoyuUserDto? Publisher { get; set; }

    /// <summary>Only with <c>include=resources</c>.</summary>
    [JsonPropertyName("resources")]
    public List<MoyuResourceDto>? Resources { get; set; }
}

/// <summary>A page of resources.</summary>
public sealed class MoyuResourceListDto
{
    /// <summary>Always <c>"list"</c>.</summary>
    [JsonPropertyName("object")]
    public string? Object { get; set; }

    /// <summary>The rows.</summary>
    [JsonPropertyName("items")]
    public List<MoyuResourceDto>? Items { get; set; }

    /// <summary>Cursor for the next page, or null on the last page.</summary>
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }

    /// <summary>Null unless <c>include_total=true</c> was sent.</summary>
    [JsonPropertyName("total")]
    public int? Total { get; set; }
}

/// <summary>
/// One downloadable item on moyu.
///
/// <para>
/// Carries no link, code or password <b>by design</b>; <see cref="WebUrl"/> is the way to it.
/// </para>
/// </summary>
public sealed class MoyuResourceDto
{
    /// <summary>Always <c>"patch_resource"</c>.</summary>
    [JsonPropertyName("object")]
    public string? Object { get; set; }

    /// <summary>The resource id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>The patch page it belongs to.</summary>
    [JsonPropertyName("patch_id")]
    public string? PatchId { get; set; }

    /// <summary>
    /// Display name. <b>It can be empty</b> — the research report observed exactly that on a real
    /// row (search result id 223), so a caller must fall back to something readable rather than
    /// showing a blank line.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// <c>s3</c> (moyu's own object store) or <c>user</c> (a link the publisher hosts elsewhere).
    /// A <c>user</c> row means the browser hop may land on a file-host page that asks for an
    /// extraction code, so the "download then adopt" flow cannot be assumed to be one click.
    /// </summary>
    [JsonPropertyName("storage")]
    public string? Storage { get; set; }

    /// <summary>
    /// A human-readable size using <b>binary</b> units, e.g. <c>"0.571 MB"</c>. Parse with
    /// <see cref="MoyuSize"/>, which was calibrated against a byte-range probe.
    /// </summary>
    [JsonPropertyName("size")]
    public string? Size { get; set; }

    /// <summary>
    /// BLAKE3 of the file, empty on rows uploaded before it was recorded. The only stable identity
    /// of the bytes themselves, and therefore the only sound way to match a downloaded file by
    /// content. Populated-ness is not guaranteed.
    /// </summary>
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    /// <summary>
    /// For an AI-translated patch, the model as the publisher typed it. Free text, not a
    /// vocabulary — never switch on this value.
    /// </summary>
    [JsonPropertyName("model_name")]
    public string? ModelName { get; set; }

    /// <summary>The localisation group's display name.</summary>
    [JsonPropertyName("localization_group_name")]
    public string? LocalizationGroupName { get; set; }

    /// <summary>Markdown source, with image tokens already resolved to absolute URLs.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>Declared patch kinds.</summary>
    [JsonPropertyName("type")]
    public List<string>? Type { get; set; }

    /// <summary>Declared languages.</summary>
    [JsonPropertyName("language")]
    public List<string>? Language { get; set; }

    /// <summary>Declared platforms.</summary>
    [JsonPropertyName("platform")]
    public List<string>? Platform { get; set; }

    /// <summary>Site download counter.</summary>
    [JsonPropertyName("download_count")]
    public int DownloadCount { get; set; }

    /// <summary>Site like counter.</summary>
    [JsonPropertyName("like_count")]
    public int LikeCount { get; set; }

    /// <summary>The page a reader must open to obtain the file.</summary>
    [JsonPropertyName("web_url")]
    public string? WebUrl { get; set; }

    /// <summary>When the resource was published.</summary>
    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    /// <summary>When the resource changed.</summary>
    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }

    /// <summary>Only with <c>include=publisher</c>.</summary>
    [JsonPropertyName("publisher")]
    public MoyuUserDto? Publisher { get; set; }
}

/// <summary>
/// Who published a patch or resource. A display copy owned by the NextMoe identity service.
/// </summary>
public sealed class MoyuUserDto
{
    /// <summary>Always <c>"user"</c>.</summary>
    [JsonPropertyName("object")]
    public string? Object { get; set; }

    /// <summary>The publisher's id.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Avatar URL.</summary>
    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }
}

/// <summary>
/// An RFC 9457 <c>application/problem+json</c> document, the failure shape of the public face.
/// </summary>
/// <remarks>
/// <c>401</c> and <c>429</c> are written by the gateway before the service is reached and carry the
/// platform's own <c>{code, message}</c> body instead, so <see cref="MoyuApi"/> reads both shapes.
/// </remarks>
public sealed class MoyuProblemDto
{
    /// <summary>Problem type URI.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Short human-readable summary.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>HTTP status echoed in the body.</summary>
    [JsonPropertyName("status")]
    public int? Status { get; set; }

    /// <summary>Human-readable explanation.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>Request path.</summary>
    [JsonPropertyName("instance")]
    public string? Instance { get; set; }

    /// <summary>Closed-registry error code, e.g. <c>INVALID_CURSOR</c>.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>Quote this in a support request; also sent as <c>X-Request-ID</c>.</summary>
    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    /// <summary>Field-level failures; empty when the failure is not field-level.</summary>
    [JsonPropertyName("errors")]
    public List<MoyuFieldErrorDto>? Errors { get; set; }

    /// <summary>
    /// The platform's plain <c>{code, message}</c> body, which the gateway writes for 401/429.
    /// Present only on those responses.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>One field-level failure inside a problem document.</summary>
public sealed class MoyuFieldErrorDto
{
    /// <summary>The offending query parameter, when the failure names one.</summary>
    [JsonPropertyName("parameter")]
    public string? Parameter { get; set; }

    /// <summary>The offending header, when the failure names one.</summary>
    [JsonPropertyName("header")]
    public string? Header { get; set; }

    /// <summary>Machine-readable reason.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>Human-readable explanation.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}
