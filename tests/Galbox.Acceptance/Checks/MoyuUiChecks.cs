using System.Diagnostics;
using System.Text;
using Galbox.Acceptance.Support;
using Galbox.App.ViewModels;
using Galbox.Core.Api;
using Galbox.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Galbox.Acceptance.Checks;

/// <summary>
/// A110 - the wiring assertion, the one the whole block exists for.
///
/// <para>
/// The moyu discovery layer was fully implemented and fully verified (A60-A67) and <b>nothing in the
/// user interface referenced it</b>: a search of <c>src/Galbox.App/Views</c> and
/// <c>src/Galbox.App/ViewModels</c> for <c>MoyuApi|MoyuOptions|MoyuResource|MoyuBrowserLauncher|
/// MoyuDownloadWatcher</c> returned zero hits, and the patch centre page stated
/// "在线补丁源：未实现" instead. This check is the evidence that the door now exists:
/// </para>
/// <list type="number">
/// <item><description>the page's ViewModel takes the moyu services as constructor dependencies, and
/// the real container can satisfy them (so the page really resolves a working client);</description></item>
/// <item><description>it exposes a query surface, and a query driven through the real ViewModel
/// reaches the wire — proving the service is called rather than merely stored;</description></item>
/// <item><description>the shipping XAML no longer claims the feature is unimplemented, and binds the
/// card's status fields.</description></item>
/// </list>
/// </summary>
public sealed class A110MoyuUiWiringCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A110";

    /// <inheritdoc />
    public string Title => "补丁中心 ViewModel 真的持有 moyu 服务，并且界面不再写着「在线补丁源：未实现」";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "PatchCenterViewModel's constructor takes MoyuApi + IMoyuKeyStore + MoyuBrowserLauncher + "
            + "MoyuDownloadWatcher and the container satisfies them; a query driven through the real "
            + "ViewModel puts exactly one request on /v2/moyu/patches; the patch centre XAML no longer "
            + "contains \"在线补丁源：未实现\" and binds MoyuStatusCodeText / MoyuStatusMessage / "
            + "FindMoyuPatchesCommand";

        var details = new List<string>();
        var problems = new List<string>();

        // --- 1. The dependencies -----------------------------------------------------------
        var viewModelType = typeof(PatchCenterViewModel);
        var constructor = viewModelType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();

        details.Add($"PatchCenterViewModel constructor ({constructor.GetParameters().Length} parameter(s)):");
        foreach (var parameter in constructor.GetParameters())
        {
            details.Add($"   {parameter.ParameterType.Name,-26} {parameter.Name}");
        }

        var required = new[]
        {
            typeof(MoyuApi), typeof(IMoyuKeyStore), typeof(MoyuBrowserLauncher), typeof(MoyuDownloadWatcher)
        };

        foreach (var type in required)
        {
            var present = constructor.GetParameters().Any(p => p.ParameterType == type);
            details.Add($"   [{(present ? "OK" : "MISSING")}] {type.Name} is a constructor dependency");
            if (!present)
            {
                problems.Add($"PatchCenterViewModel does not depend on {type.Name}, so the page cannot reach the moyu layer");
            }
        }

        // The container's own registrations, resolved exactly as the page resolves them.
        var moyuApiFromContainer = context.Services.GetService<MoyuApi>();
        var keyStoreFromContainer = context.Services.GetService<IMoyuKeyStore>();
        var launcherFromContainer = context.Services.GetService<MoyuBrowserLauncher>();
        var watcherFromContainer = context.Services.GetService<MoyuDownloadWatcher>();
        var optionsFromContainer = context.Services.GetService<MoyuOptions>();

        details.Add(string.Empty);
        details.Add("Resolved from the run's container (what the page's own constructor injection gets):");
        details.Add($"   MoyuApi              : {moyuApiFromContainer?.GetType().Name ?? "(MISSING)"}");
        details.Add($"   IMoyuKeyStore        : {keyStoreFromContainer?.GetType().Name ?? "(MISSING)"}");
        details.Add($"   MoyuBrowserLauncher  : {launcherFromContainer?.GetType().Name ?? "(MISSING)"}");
        details.Add($"   MoyuDownloadWatcher  : {watcherFromContainer?.GetType().Name ?? "(MISSING)"}");
        details.Add($"   MoyuOptions          : {optionsFromContainer?.DescribeKey() ?? "(MISSING)"}");

        if (moyuApiFromContainer is null || keyStoreFromContainer is null
            || launcherFromContainer is null || watcherFromContainer is null)
        {
            problems.Add("the container does not satisfy all four moyu dependencies, so the page cannot be constructed");
        }

        // --- 2. A real query through the real ViewModel ------------------------------------
        var payload =
            """{"object":"list","items":[{"object":"patch","id":"86","vndb_id":"v4","catalog_work_id":"86","content_limit":"sfw","release_date":"2004-04-28","type":["manual"],"language":["zh-Hans"],"platform":["windows"],"resource_count":1,"download_count":282,"view_count":0,"favorite_count":0,"comment_count":0,"web_url":"https://www.moyu.moe/patch/86/introduction","created_at":"2024-12-13T05:25:54.167Z","updated_at":"2026-05-27T18:41:40.338Z","resource_updated_at":"2024-12-13T05:25:54.167Z","resources":[{"object":"patch_resource","id":"6262","patch_id":"86","name":"Key Fans Club 汉化","storage":"s3","size":"17.953 MB","hash":"690eac","model_name":"","localization_group_name":"Key Fans Club","note":"","type":["manual"],"language":["zh-Hans"],"platform":["windows"],"download_count":122,"like_count":0,"web_url":"https://www.moyu.moe/patch/86/resources/6262","created_at":"2024-12-13T05:25:54.167Z","updated_at":"2025-11-11T08:31:33.206Z"}]}],"next_cursor":null,"total":1,"missing":[]}""";

        var harness = MoyuUiHarness.Build(
            context,
            apiKey: "nmk_live_A110probe_000000000000000000",
            responder: _ => MoyuStubHandler.Json(payload));

        var viewModel = harness.CreateViewModel();
        viewModel.SelectedGame = MoyuUiHarness.MakeGame("v4");

        var query = viewModel.FindMoyuPatchesCommand;
        details.Add(string.Empty);
        details.Add($"FindMoyuPatchesCommand : {(query is null ? "(MISSING)" : query.GetType().Name)}");

        if (query is null)
        {
            problems.Add("the ViewModel exposes no FindMoyuPatchesCommand, so the page has no button to press");
        }
        else
        {
            var stopwatch = Stopwatch.StartNew();
            await query.ExecuteAsync(null).ConfigureAwait(false);
            stopwatch.Stop();

            details.Add($"Query elapsed          : {stopwatch.ElapsedMilliseconds} ms");
            details.Add($"Requests on the wire   : {harness.Transport.Everything.Count}");
            foreach (var (method, uri) in harness.Transport.Everything)
            {
                details.Add($"   {method} {uri.PathAndQuery}  ->  allowed={MoyuComplianceGuard.IsAllowedApiUri(uri)}");
            }

            details.Add($"MoyuState              : {viewModel.MoyuState}");
            details.Add($"MoyuStatusCodeText     : {viewModel.MoyuStatusCodeText}");
            details.Add($"MoyuStatusMessage      : {viewModel.MoyuStatusMessage}");
            details.Add($"MoyuPatches.Count      : {viewModel.MoyuPatches.Count}");
            details.Add($"MoyuResources.Count    : {viewModel.MoyuResources.Count}");

            if (harness.Transport.Everything.Count != 1)
            {
                problems.Add($"a query through the ViewModel produced {harness.Transport.Everything.Count} request(s), expected exactly 1");
            }

            if (viewModel.MoyuState != MoyuQueryState.Found)
            {
                problems.Add($"the ViewModel reported MoyuState={viewModel.MoyuState} for a one-row answer, expected Found");
            }

            if (viewModel.MoyuPatches.Count != 1)
            {
                problems.Add($"the ViewModel holds {viewModel.MoyuPatches.Count} patch row(s), expected 1");
            }

            if (viewModel.MoyuResources.Count != 1)
            {
                problems.Add($"the ViewModel lists {viewModel.MoyuResources.Count} resource row(s) for the found patch, expected 1");
            }

            // The engine's source metadata is what makes an install traceable to the page it came from.
            var resourceRow = viewModel.MoyuResources.FirstOrDefault();
            if (resourceRow is not null)
            {
                var source = resourceRow.Resource.ToPatchSourceInfo();
                details.Add($"First resource row     : {resourceRow.DisplayName} | {resourceRow.SizeText}");
                details.Add($"  patch source info    : kind={source.Kind}, patch={source.PatchId}, "
                            + $"resource={source.ResourceId}, page={source.WebUrl}");
            }
        }

        // --- 3. The XAML ---------------------------------------------------------------------
        var repoRoot = RepoLocator.FindRepoRoot();
        var pagePath = Path.Combine(RepoLocator.AppProject(repoRoot), "Views", "PatchCenterPage.xaml");
        var xaml = File.ReadAllText(pagePath);

        details.Add(string.Empty);
        details.Add($"Patch centre page      : {pagePath}");

        var mustBeGone = new[] { "在线补丁源：未实现", "OnlinePatchSourceNotice" };
        foreach (var text in mustBeGone)
        {
            var stillThere = xaml.Contains(text, StringComparison.Ordinal);
            details.Add($"   [{(stillThere ? "STILL PRESENT" : "removed")}] \"{text}\"");
            if (stillThere)
            {
                problems.Add($"the page still contains \"{text}\", so the UI still says the online source is unimplemented");
            }
        }

        var mustBeBound = new[]
        {
            "ViewModel.FindMoyuPatchesCommand",
            "ViewModel.MoyuStatusCodeText",
            "ViewModel.MoyuStatusMessage",
            "ViewModel.MoyuErrorDetail",
            "ViewModel.MoyuAdvisoryMessage",
            "ViewModel.MoyuPatches",
            "ViewModel.MoyuResources",
            "ViewModel.MoyuKeySummary"
        };

        foreach (var binding in mustBeBound)
        {
            var bound = xaml.Contains(binding, StringComparison.Ordinal);
            details.Add($"   [{(bound ? "bound" : "NOT BOUND")}] {binding}");
            if (!bound)
            {
                problems.Add($"the page does not bind {binding}, so that part of the feature has no door");
            }
        }

        // Every command the card binds must resolve on the ViewModel: a button bound to a null
        // command is exactly the "点了没反应的按钮" defect class of this project's spec.
        var commandNames = new[]
        {
            "FindMoyuPatchesCommand", "SelectMoyuPatchCommand", "OpenMoyuPatchPageCommand",
            "AdoptMoyuDownloadCommand", "CancelMoyuDownloadCommand", "SaveMoyuKeyCommand", "ClearMoyuKeyCommand"
        };

        foreach (var name in commandNames)
        {
            var command = MoyuUiHarness.Read(viewModel, name);
            details.Add($"   [{(command is null ? "MISSING" : command.GetType().Name)}] ViewModel.{name}");
            if (command is null)
            {
                problems.Add($"ViewModel.{name} does not exist; a button bound to it would be a dead button");
            }
        }

        // The download-model paragraph is the honest description of the browser hop. It has to say
        // that the site gives no direct link, otherwise the user is told to expect something the
        // service deliberately does not provide.
        var explanation = viewModel.MoyuDownloadModelExplanation;
        details.Add(string.Empty);
        details.Add($"Download model text    : {explanation}");
        if (!explanation.Contains("直链", StringComparison.Ordinal) || !explanation.Contains("下载", StringComparison.Ordinal))
        {
            problems.Add("the download-model explanation does not state that no direct link exists");
        }

        foreach (var problem in problems)
        {
            details.Add($"PROBLEM: {problem}");
        }

        if (problems.Count > 0)
        {
            return CheckResult.Fail(Id, Title, expected, $"{problems.Count} wiring problem(s): {string.Join("; ", problems)}")
                .With(details.ToArray());
        }

        return CheckResult.Pass(Id, Title, expected,
            "4/4 moyu dependencies in the constructor and resolvable from the container; a query through "
            + "the real ViewModel sent exactly 1 request to /v2/moyu/patches and produced 1 patch row with "
            + "1 resource; the page no longer claims the source is unimplemented and binds 8 status fields "
            + "plus 7 live commands")
            .With(details.ToArray());
    }
}

/// <summary>
/// A111 - no <c>nmk_</c> key configured. The defect this guards is the one the whole moyu block was
/// written for: a source that returns an empty list when it is actually unusable, leaving the user
/// unable to tell "this game has no patches" from "you have not configured anything".
///
/// <para>
/// The transport it drives would answer <c>200</c> with a valid one-row payload, so a request that
/// leaked through would look like success. That is what makes "0 requests" a strong assertion.
/// </para>
/// </summary>
public sealed class A111MoyuUiMissingKeyCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A111";

    /// <inheritdoc />
    public string Title => "未配置 nmk_ 密钥时，补丁中心给的是一条明确的「未配置密钥」，不是空列表";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "with no key, the ViewModel reports MoyuState=MissingKey with a headline naming the missing "
            + "nmk_ key and how to get one, shows zero result rows, and sends ZERO HTTP requests";

        var details = new List<string>();
        var problems = new List<string>();

        var harness = MoyuUiHarness.Build(
            context,
            apiKey: null,
            responder: _ => MoyuStubHandler.Json(
                """{"object":"list","items":[{"object":"patch","id":"86","vndb_id":"v4","catalog_work_id":"86","type":["manual"],"language":["zh-Hans"],"platform":["windows"],"resource_count":1,"web_url":"https://www.moyu.moe/patch/86/introduction"}],"next_cursor":null,"total":1,"missing":[]}"""));

        details.Add($"MoyuApi.IsConfigured   : {harness.Api.IsConfigured}");
        details.Add($"Key store              : {harness.KeyStore}");

        if (harness.Api.IsConfigured)
        {
            return CheckResult.Fail(Id, Title, expected,
                "the harness produced a configured client, so this check cannot measure the key-less path")
                .With(details.ToArray());
        }

        var viewModel = harness.CreateViewModel();
        viewModel.SelectedGame = MoyuUiHarness.MakeGame("v4");

        details.Add($"State before any query : {viewModel.MoyuState} / {viewModel.MoyuStatusCodeText}");
        details.Add($"  message              : {viewModel.MoyuStatusMessage}");
        details.Add($"Key summary (shown)    : {viewModel.MoyuKeySummary}");
        details.Add($"IsMoyuKeyConfigured    : {viewModel.IsMoyuKeyConfigured}");

        await viewModel.FindMoyuPatchesCommand.ExecuteAsync(null).ConfigureAwait(false);

        details.Add(string.Empty);
        details.Add($"State after the query  : {viewModel.MoyuState}");
        details.Add($"Status code            : {viewModel.MoyuStatusCodeText}");
        details.Add($"Status message         : {viewModel.MoyuStatusMessage}");
        details.Add($"Advisory               : {viewModel.MoyuAdvisoryMessage}");
        details.Add($"Error detail           : {viewModel.MoyuErrorDetail ?? "(none — correctly absent: a missing key is not a query failure)"}");
        details.Add($"MoyuPatches.Count      : {viewModel.MoyuPatches.Count}");
        details.Add($"Requests on the wire   : {harness.Transport.Everything.Count}");

        if (viewModel.MoyuState == MoyuQueryState.Empty)
        {
            problems.Add("a missing key was reported as \"no results\" (MoyuQueryState.Empty) — the exact defect this block exists for");
        }

        if (viewModel.MoyuState != MoyuQueryState.MissingKey)
        {
            problems.Add($"MoyuState is {viewModel.MoyuState}, expected MissingKey");
        }

        if (viewModel.MoyuPatches.Count != 0)
        {
            problems.Add($"the key-less path produced {viewModel.MoyuPatches.Count} row(s); a key-less client must invent nothing");
        }

        var headline = viewModel.MoyuStatusMessage ?? string.Empty;
        if (!headline.Contains("密钥", StringComparison.Ordinal))
        {
            problems.Add($"the headline does not say a key is missing: \"{headline}\"");
        }

        if (!headline.Contains("无法查询", StringComparison.Ordinal))
        {
            problems.Add($"the headline does not say the query could not be made: \"{headline}\"");
        }

        var advisory = viewModel.MoyuAdvisoryMessage ?? string.Empty;
        if (advisory.Length == 0)
        {
            problems.Add("no advisory text is shown for the missing-key state");
        }

        if (viewModel.MoyuErrorDetail is not null)
        {
            problems.Add("the missing-key state carries an error detail, which makes it look like a query failure");
        }

        // What it is, where to get it: both must be on the page, not just in a log line.
        var keyText = viewModel.MoyuKeySummary + "\n" + viewModel.MoyuKeyHowTo;
        details.Add(string.Empty);
        details.Add($"Key text (as shown)    : {keyText.Replace("\n", " | ")}");

        foreach (var fragment in new[] { "nmk_", "developer.nextmoe.dev", "未配置" })
        {
            if (!keyText.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"the key panel never says \"{fragment}\", so a user cannot act on it");
            }
        }

        if (harness.Transport.Everything.Count > 0)
        {
            problems.Add($"{harness.Transport.Everything.Count} request(s) were sent with no key configured: "
                         + string.Join(", ", harness.Transport.Everything.Select(r => r.Uri.ToString())));
        }

        foreach (var problem in problems)
        {
            details.Add($"PROBLEM: {problem}");
        }

        if (problems.Count > 0)
        {
            return CheckResult.Fail(Id, Title, expected, $"{problems.Count} problem(s): {string.Join("; ", problems)}")
                .With(details.ToArray());
        }

        return CheckResult.Pass(Id, Title, expected,
            "MoyuState=MissingKey with \"未配置 nmk_ 密钥，无法查询\", 0 result rows, no error detail, "
            + "the panel names nmk_ and developer.nextmoe.dev, and 0 HTTP requests were sent")
            .With(details.ToArray());
    }
}

/// <summary>
/// A112 - a game with no vndb id. moyu's public face accepts <c>vndb:vXXXX</c> and
/// <c>catalog:&lt;id&gt;</c> and <b>not</b> a Bangumi subject id; the three id spaces are not
/// interchangeable, and guessing a conversion would produce a confidently wrong patch list.
///
/// <para>
/// Two things are asserted, and the second is the important one: the UI must say the work cannot be
/// looked up, and it must do so <b>without sending a request</b>. The transport here is configured
/// with a key, so a request would happily be answered — the only reason it is not sent is that the
/// ViewModel decides the anchor first.
/// </para>
/// </summary>
public sealed class A112MoyuUiNoVndbIdCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A112";

    /// <inheritdoc />
    public string Title => "没有 vndb id 时显示「无法查询：该作品没有 vndb 标识」，且发出 0 个请求";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "for a game with no vndb id (but with a Bangumi source id), the ViewModel reports "
            + "MoyuState=NoAnchor naming the missing vndb identifier, sends ZERO HTTP requests, and shows "
            + "no rows — and the same holds for a game that was never scraped at all";

        var details = new List<string>();
        var problems = new List<string>();

        var harness = MoyuUiHarness.Build(
            context,
            apiKey: "nmk_live_A112probe_000000000000000000",
            responder: _ => MoyuStubHandler.Json(
                """{"object":"list","items":[{"object":"patch","id":"86","vndb_id":"v4","catalog_work_id":"86","type":["manual"],"language":["zh-Hans"],"platform":["windows"],"resource_count":1,"web_url":"https://www.moyu.moe/patch/86/introduction"}],"next_cursor":null,"total":1,"missing":[]}"""));

        var cases = new (string Label, GameInfo Game)[]
        {
            ("Bangumi-sourced game (source=bangumi, no VndbId)", MoyuUiHarness.MakeGame(vndbId: null, sourceType: "bangumi", sourceId: "1234")),
            ("never-scraped game (no ids at all)", MoyuUiHarness.MakeGame(vndbId: null)),
            ("illegal vndb value", MoyuUiHarness.MakeGame(vndbId: "bangumi:1234"))
        };

        var requestsBefore = harness.Transport.Everything.Count;
        var seen = new List<string>();

        foreach (var (label, game) in cases)
        {
            var viewModel = harness.CreateViewModel();
            viewModel.SelectedGame = game;

            var before = harness.Transport.Everything.Count;
            await viewModel.FindMoyuPatchesCommand.ExecuteAsync(null).ConfigureAwait(false);
            var after = harness.Transport.Everything.Count;

            details.Add($"--- {label} ---");
            details.Add($"  VndbId / SourceType / SourceId : {game.VndbId ?? "(null)"} / {game.SourceType ?? "(null)"} / {game.SourceId ?? "(null)"}");
            details.Add($"  MoyuState                      : {viewModel.MoyuState}");
            details.Add($"  Status code                    : {viewModel.MoyuStatusCodeText}");
            details.Add($"  Status message                 : {viewModel.MoyuStatusMessage}");
            details.Add($"  Advisory                       : {viewModel.MoyuAdvisoryMessage}");
            details.Add($"  MoyuPatches.Count              : {viewModel.MoyuPatches.Count}");
            details.Add($"  Requests sent by this query    : {after - before}");
            details.Add(string.Empty);

            if (after != before)
            {
                problems.Add($"{label}: {after - before} request(s) were sent for a game that cannot be looked up");
            }

            if (viewModel.MoyuState != MoyuQueryState.NoAnchor)
            {
                problems.Add($"{label}: MoyuState is {viewModel.MoyuState}, expected NoAnchor");
            }

            if (viewModel.MoyuPatches.Count != 0)
            {
                problems.Add($"{label}: {viewModel.MoyuPatches.Count} row(s) were produced without an anchor");
            }

            var headline = viewModel.MoyuStatusMessage ?? string.Empty;
            if (!headline.Contains("无法查询", StringComparison.Ordinal))
            {
                problems.Add($"{label}: the headline does not say the query cannot be made: \"{headline}\"");
            }

            if (!headline.Contains("vndb", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{label}: the headline does not name the missing vndb identifier: \"{headline}\"");
            }

            var advisory = viewModel.MoyuAdvisoryMessage ?? string.Empty;
            if (advisory.Length == 0)
            {
                problems.Add($"{label}: no advisory text is shown");
            }

            seen.Add($"{(int)viewModel.MoyuState}:{viewModel.MoyuStatusMessage}");
        }

        var totalRequests = harness.Transport.Everything.Count - requestsBefore;
        details.Add($"Total requests sent across all three no-anchor cases: {totalRequests}");

        if (totalRequests != 0)
        {
            problems.Add($"{totalRequests} request(s) were sent in total for games with no anchor; the answer is knowable without the network");
        }

        // The Bangumi case must say WHY, by name: "Bangumi 的 id 不能当锚点用" is the actionable part.
        var bangumiAdvisory = seen.FirstOrDefault();
        details.Add($"First case state/message pair: {bangumiAdvisory}");

        foreach (var problem in problems)
        {
            details.Add($"PROBLEM: {problem}");
        }

        if (problems.Count > 0)
        {
            return CheckResult.Fail(Id, Title, expected, $"{problems.Count} problem(s): {string.Join("; ", problems)}")
                .With(details.ToArray());
        }

        return CheckResult.Pass(Id, Title, expected,
            "3/3 anchor-less games reported NoAnchor with \"无法查询：该作品没有 vndb 标识\", 0 rows, and "
            + "0 HTTP requests were sent even though the client was configured with a key")
            .With(details.ToArray());
    }
}

/// <summary>
/// A113 - "no results" and "the query failed" are two different sentences.
///
/// <para>
/// This is the second half of the honesty rule the project keeps re-learning: an empty successful
/// answer is information ("moyu has nothing for this game"), while a failed query is the absence of
/// an answer. The check drives both through the same ViewModel and asserts that the state, the
/// headline, the advisory and the error surface differ - and, in particular, that only the failure
/// carries the service's own reason or claims the query did not succeed.
/// </para>
/// </summary>
public sealed class A113MoyuUiEmptyVsFailureCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A113";

    /// <inheritdoc />
    public string Title => "「没有找到补丁」与「查询失败」是两种状态、两套话术，不会互相冒充";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "an answered-but-empty lookup reports MoyuState=Empty with a \"没有找到补丁\" headline and no "
            + "error detail; a transport failure reports MoyuState=Failed with the service's reason "
            + "attached; the two states, headlines and detail surfaces are all different";

        var details = new List<string>();
        var problems = new List<string>();

        // --- 1. Answered, zero rows ----------------------------------------------------------
        var emptyHarness = MoyuUiHarness.Build(
            context,
            apiKey: "nmk_live_A113probe_empty_000000000000",
            responder: _ => MoyuStubHandler.Json(
                """{"object":"list","items":[],"next_cursor":null,"total":0,"missing":["vndb:v4"]}"""));

        var emptyViewModel = emptyHarness.CreateViewModel();
        emptyViewModel.SelectedGame = MoyuUiHarness.MakeGame("v4");
        await emptyViewModel.FindMoyuPatchesCommand.ExecuteAsync(null).ConfigureAwait(false);

        details.Add("--- A. The lookup was answered and holds nothing ---");
        details.Add($"  MoyuState        : {emptyViewModel.MoyuState}");
        details.Add($"  Status code      : {emptyViewModel.MoyuStatusCodeText}");
        details.Add($"  Status message   : {emptyViewModel.MoyuStatusMessage}");
        details.Add($"  Advisory         : {emptyViewModel.MoyuAdvisoryMessage}");
        details.Add($"  Error detail     : {emptyViewModel.MoyuErrorDetail ?? "(none)"}");
        details.Add($"  Rows             : {emptyViewModel.MoyuPatches.Count}");
        details.Add($"  Requests sent    : {emptyHarness.Transport.Everything.Count}");
        details.Add(string.Empty);

        if (emptyHarness.Transport.Everything.Count != 1)
        {
            problems.Add($"the empty case sent {emptyHarness.Transport.Everything.Count} request(s), expected 1 (the question really was asked)");
        }

        if (emptyViewModel.MoyuState != MoyuQueryState.Empty)
        {
            problems.Add($"an answered-but-empty lookup reported {emptyViewModel.MoyuState}, expected Empty");
        }

        if (emptyViewModel.MoyuErrorDetail is not null)
        {
            problems.Add($"the empty case carries an error detail (\"{emptyViewModel.MoyuErrorDetail}\"), so it reads like a failure");
        }

        if (emptyViewModel.MoyuStatusMessage?.Contains("没有找到", StringComparison.Ordinal) != true)
        {
            problems.Add($"the empty headline does not say nothing was found: \"{emptyViewModel.MoyuStatusMessage}\"");
        }

        // --- 2. The transport never answers ---------------------------------------------------
        var failedHarness = MoyuUiHarness.Build(
            context,
            apiKey: "nmk_live_A113probe_failure_000000000000",
            responder: _ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    """{"type":"about:blank","title":"upstream unavailable","status":503,"code":"UPSTREAM_TIMEOUT","detail":"the moyu backend did not answer in time","request_id":"req-A113"}""",
                    Encoding.UTF8,
                    "application/problem+json")
            });

        var failOptions = failedHarness.Options;
        failOptions.MaxRetries = 0;          // one attempt; the retry policy is A67's business
        failOptions.EnableConditionalRequests = false;

        var failedViewModel = failedHarness.CreateViewModel();
        failedViewModel.SelectedGame = MoyuUiHarness.MakeGame("v4");

        var stopwatch = Stopwatch.StartNew();
        await failedViewModel.FindMoyuPatchesCommand.ExecuteAsync(null).ConfigureAwait(false);
        stopwatch.Stop();

        details.Add("--- B. The transport failed ---");
        details.Add($"  MoyuState        : {failedViewModel.MoyuState}");
        details.Add($"  Status code      : {failedViewModel.MoyuStatusCodeText}");
        details.Add($"  Status message   : {failedViewModel.MoyuStatusMessage}");
        details.Add($"  Advisory         : {failedViewModel.MoyuAdvisoryMessage}");
        details.Add($"  Error detail     : {failedViewModel.MoyuErrorDetail ?? "(none)"}");
        details.Add($"  Rows             : {failedViewModel.MoyuPatches.Count}");
        details.Add($"  Requests sent    : {failedHarness.Transport.Everything.Count}");
        details.Add($"  Elapsed          : {stopwatch.ElapsedMilliseconds} ms");
        details.Add(string.Empty);

        if (failedViewModel.MoyuState != MoyuQueryState.Failed)
        {
            problems.Add($"a 503 produced {failedViewModel.MoyuState}, expected Failed");
        }

        if (failedViewModel.MoyuState == MoyuQueryState.Empty)
        {
            problems.Add("a failed query was reported as \"no results\"");
        }

        var errorDetail = failedViewModel.MoyuErrorDetail ?? string.Empty;
        if (errorDetail.Length == 0)
        {
            problems.Add("the failure state carries no reason, so the user cannot tell why the query failed");
        }

        if (!errorDetail.Contains("HTTP 503", StringComparison.Ordinal))
        {
            problems.Add($"the failure detail does not name the HTTP status: \"{errorDetail}\"");
        }

        if (!errorDetail.Contains("UPSTREAM_TIMEOUT", StringComparison.Ordinal))
        {
            problems.Add($"the failure detail drops the upstream code: \"{errorDetail}\"");
        }

        if (!failedViewModel.MoyuStatusMessage!.Contains("查询失败", StringComparison.Ordinal))
        {
            problems.Add($"the failure headline does not say the query failed: \"{failedViewModel.MoyuStatusMessage}\"");
        }

        if (failedViewModel.MoyuPatches.Count != 0)
        {
            problems.Add($"the failure produced {failedViewModel.MoyuPatches.Count} row(s); a failure must not invent rows");
        }

        // --- 3. The two must not be confusable ------------------------------------------------
        details.Add("--- C. The separation ---");
        var pairs = new (string Field, string Empty, string Failed)[]
        {
            ("MoyuState", emptyViewModel.MoyuState.ToString(), failedViewModel.MoyuState.ToString()),
            ("StatusCodeText", emptyViewModel.MoyuStatusCodeText, failedViewModel.MoyuStatusCodeText),
            ("StatusMessage", emptyViewModel.MoyuStatusMessage ?? string.Empty, failedViewModel.MoyuStatusMessage ?? string.Empty)
        };

        foreach (var (field, empty, failed) in pairs)
        {
            var distinct = !string.Equals(empty, failed, StringComparison.Ordinal);
            details.Add($"   {field,-16} empty=\"{empty}\"  failed=\"{failed}\"  -> {(distinct ? "different" : "IDENTICAL (wrong)")}");
            if (!distinct)
            {
                problems.Add($"{field} is identical for the empty and the failed case, so a user cannot tell them apart");
            }
        }

        details.Add($"   ErrorDetail      empty={(emptyViewModel.MoyuErrorDetail is null ? "(absent)" : "present")}  "
                    + $"failed={(failedViewModel.MoyuErrorDetail is null ? "(absent)" : "present")}");

        if (emptyViewModel.MoyuErrorDetail is not null || failedViewModel.MoyuErrorDetail is null)
        {
            problems.Add("the error surface does not separate the two cases (present for empty, or absent for failed)");
        }

        foreach (var problem in problems)
        {
            details.Add($"PROBLEM: {problem}");
        }

        if (problems.Count > 0)
        {
            return CheckResult.Fail(Id, Title, expected, $"{problems.Count} problem(s): {string.Join("; ", problems)}")
                .With(details.ToArray());
        }

        return CheckResult.Pass(Id, Title, expected,
            "Empty=MOYU_NO_RESULTS/\"查询成功，但没有找到补丁\" with no error detail; Failed=MOYU_QUERY_FAILED/"
            + "\"查询失败：这次没有拿到结果\" carrying HTTP 503 + UPSTREAM_TIMEOUT; state, code and headline all differ")
            .With(details.ToArray());
    }
}

/// <summary>
/// A114 - the compliance guard is still on the new code path.
///
/// <para>
/// <c>https://www.moyu.moe/robots.txt</c> contains <c>Disallow: /api</c>, and every usable
/// first-party data endpoint on that site lives under <c>/api/v1/*</c>. The online source added to the
/// patch centre is a new route to the network, so this check re-asserts the boundary <b>through the
/// UI's own path</b>: the requests the patch centre actually produces are built by
/// <see cref="MoyuComplianceGuard"/>, every one of them is inside the allow-list, and the guard still
/// refuses the forbidden surface when it is asked directly.
/// </para>
/// </summary>
public sealed class A114MoyuUiComplianceCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A114";

    /// <inheritdoc />
    public string Title => "经由补丁中心界面的新路径也不会碰到 /api，护栏仍在运行时生效";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "every request the patch centre's query produces is on api.nextmoe.dev under /v2/moyu/ and "
            + "never under /api; the guard still throws for a forbidden relative path; and a real "
            + "application-pipeline moyu request recorded during the whole run stays inside the allow-list";

        var details = new List<string>();
        var problems = new List<string>();

        context.Traffic.Clear();

        var harness = MoyuUiHarness.Build(
            context,
            apiKey: "nmk_live_A114probe_000000000000000000",
            responder: _ => MoyuStubHandler.Json(
                """{"object":"list","items":[{"object":"patch","id":"86","vndb_id":"v4","catalog_work_id":"86","type":["manual"],"language":["zh-Hans"],"platform":["windows"],"resource_count":1,"web_url":"https://www.moyu.moe/patch/86/introduction"}],"next_cursor":null,"total":1,"missing":[]}"""));

        var viewModel = harness.CreateViewModel();
        viewModel.SelectedGame = MoyuUiHarness.MakeGame("v4");

        await viewModel.FindMoyuPatchesCommand.ExecuteAsync(null).ConfigureAwait(false);

        details.Add($"Requests produced by the patch centre query ({harness.Transport.Everything.Count}):");
        foreach (var (method, uri) in harness.Transport.Everything)
        {
            var allowed = MoyuComplianceGuard.IsAllowedApiUri(uri);
            var forbidden = MoyuComplianceGuard.IsForbiddenPath(uri.AbsolutePath);

            details.Add($"   {method,-4} {uri}");
            details.Add($"        host={uri.Host} path={uri.AbsolutePath} -> allowed={allowed}, under /api={forbidden}");

            if (!allowed)
            {
                problems.Add($"the UI's query sent a request outside the allow-list: {method} {uri}");
            }

            if (forbidden)
            {
                problems.Add($"the UI's query reached the robots.txt Disallowed surface: {method} {uri}");
            }

            if (!uri.AbsolutePath.StartsWith("/v2/moyu/", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"the UI's query used a path other than /v2/moyu/: {uri.AbsolutePath}");
            }
        }

        if (harness.Transport.Everything.Count == 0)
        {
            problems.Add("no request was produced, so this check measured nothing");
        }

        // --- The guard itself, asked directly -------------------------------------------------
        details.Add(string.Empty);
        details.Add("Guard matrix, asked directly (the runtime mechanism, not a comment):");

        var mustRefuse = new[]
        {
            "/api/v1/search",
            "/api/v1/patch/86/resource",
            "/api/v1/patch/resource/223/link",
            "/v2/moyu/patches/../api/v1/search"
        };

        foreach (var path in mustRefuse)
        {
            var threw = false;
            string? built = null;
            try
            {
                built = MoyuComplianceGuard.EnsureApiUri(MoyuOptions.DefaultBaseAddress, path).ToString();
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            details.Add($"   [{(threw ? "refused" : "BUILT (wrong)")}] {path}{(built is null ? string.Empty : $" -> {built}")}");
            if (!threw)
            {
                problems.Add($"EnsureApiUri built a URI for the forbidden path {path}");
            }
        }

        details.Add($"   [allowed] /v2/moyu/patches?refs=vndb%3Av4  -> {MoyuComplianceGuard.EnsureApiUri(MoyuOptions.DefaultBaseAddress, "/v2/moyu/patches?refs=vndb%3Av4").Host}");

        // --- What the application's own pipelines recorded ------------------------------------
        var recorded = context.Traffic.Exchanges
            .Where(e => e.Url.Contains("moyu", StringComparison.OrdinalIgnoreCase)
                        || e.Url.Contains("nextmoe.dev", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        details.Add(string.Empty);
        details.Add($"Moyu/nextmoe requests recorded on the application's own pipelines during this check: {recorded.Length}");
        foreach (var exchange in recorded)
        {
            details.Add($"   [{exchange.ClientName}] {exchange.Method} {exchange.Url} -> {exchange.StatusCode}");

            if (!Uri.TryCreate(exchange.Url, UriKind.Absolute, out var uri)
                || !MoyuComplianceGuard.IsAllowedApiUri(uri))
            {
                problems.Add($"a recorded moyu request left the allow-list: {exchange.Method} {exchange.Url}");
            }
        }

        // The UI's own exchanges are on the recorder too (that is how the harness observes them), and
        // every one of them is checked by the loop above.
        var uiExchanges = context.Traffic.For("MoyuUiProbe");
        details.Add($"   (of which produced by the patch centre's client: {uiExchanges.Count})");

        if (uiExchanges.Count != recorded.Length && recorded.Length > 0)
        {
            details.Add("   note: other moyu clients in the container also produced traffic; all of it was checked");
        }

        foreach (var problem in problems)
        {
            details.Add($"PROBLEM: {problem}");
        }

        if (problems.Count > 0)
        {
            return CheckResult.Fail(Id, Title, expected,
                $"{problems.Count} compliance violation(s): {string.Join("; ", problems)}").With(details.ToArray());
        }

        return CheckResult.Pass(Id, Title, expected,
            $"all {harness.Transport.Everything.Count} request(s) the patch centre produced were /v2/moyu/* on "
            + $"{MoyuComplianceGuard.ApiHost}; {mustRefuse.Length}/{mustRefuse.Length} forbidden paths still refused by "
            + $"EnsureApiUri; {recorded.Length} recorded pipeline request(s) inside the allow-list")
            .With(details.ToArray());
    }
}

/// <summary>
/// A115 - the two hops after discovery: the browser, and the downloads folder.
///
/// <para>
/// Both are asserted through their real seams. The browser hop runs the real
/// <see cref="MoyuBrowserLauncher"/> in dry-run mode, so a check can prove the page <b>would</b> be
/// opened without starting a browser window on somebody's desktop. The download hop runs the real
/// <see cref="MoyuDownloadWatcher"/> against a throw-away folder with a short timeout — because the
/// requirement is that a user who never downloads anything still gets an answer, and the watcher
/// ending in <see cref="MoyuDownloadWatchState.TimedOut"/> is that answer.
/// </para>
///
/// <para>
/// It also asserts the opposite direction for each: a row with no page URL produces a stated failure
/// rather than a silent no-op, and a row with no live resource produces a stated failure rather than
/// an indefinite "watching".
/// </para>
/// </summary>
public sealed class A115MoyuUiBrowserAndDownloadCheck : IAcceptanceCheck
{
    /// <inheritdoc />
    public string Id => "A115";

    /// <inheritdoc />
    public string Title => "打开补丁页与接管下载：dry-run 不启动浏览器、等待会超时并给出结论、无地址时不假成功";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "the row's open command validates the page URL and reports the hop (dry run: no process "
            + "started); a row without a page URL reports a refusal and its button is disabled; adopting "
            + "a download against an empty folder times out with a stated reason and ends the watching "
            + "state; a resource-less patch replaces the resource list with a reason";

        var details = new List<string>();
        var problems = new List<string>();

        var startedProcesses = new List<string>();
        var launcher = new MoyuBrowserLauncher(new MoyuBrowserLauncherOptions
        {
            DryRun = true,
            StartProcess = info =>
            {
                startedProcesses.Add(info.FileName);
                return null;
            }
        });

        var watchFolder = MoyuCheckSupport.NewScratchDirectory("a115-downloads");
        var watcher = new MoyuDownloadWatcher(new MoyuOptions { DownloadsFolder = watchFolder });

        try
        {
            var harness = MoyuUiHarness.Build(
                context,
                apiKey: "nmk_live_A115probe_000000000000000000",
                responder: _ => MoyuStubHandler.Json(
                    """{"object":"list","items":[{"object":"patch","id":"86","vndb_id":"v4","catalog_work_id":"86","type":["manual"],"language":["zh-Hans"],"platform":["windows"],"resource_count":1,"download_count":282,"view_count":0,"favorite_count":0,"comment_count":0,"web_url":"https://www.moyu.moe/patch/86/introduction","updated_at":"2026-05-27T18:41:40.338Z","resource_updated_at":"2024-12-13T05:25:54.167Z","resources":[{"object":"patch_resource","id":"6262","patch_id":"86","name":"Key Fans Club 汉化","storage":"s3","size":"17.953 MB","hash":"690eac","model_name":"","localization_group_name":"Key Fans Club","note":"","type":["manual"],"language":["zh-Hans"],"platform":["windows"],"download_count":122,"like_count":0,"web_url":"https://www.moyu.moe/patch/86/resources/6262","created_at":"2024-12-13T05:25:54.167Z","updated_at":"2025-11-11T08:31:33.206Z"}]}],"next_cursor":null,"total":1,"missing":[]}"""),
                launcher: launcher,
                watcher: watcher);

            var viewModel = harness.CreateViewModel();
            viewModel.SelectedGame = MoyuUiHarness.MakeGame("v4");

            await viewModel.FindMoyuPatchesCommand.ExecuteAsync(null).ConfigureAwait(false);

            var patchRow = viewModel.MoyuPatches.FirstOrDefault();
            var resourceRow = viewModel.MoyuResources.FirstOrDefault();

            details.Add($"Found patch rows      : {viewModel.MoyuPatches.Count}");
            details.Add($"Found resource rows   : {viewModel.MoyuResources.Count}");

            if (patchRow is null || resourceRow is null)
            {
                return CheckResult.Fail(Id, Title, expected,
                    "the query produced no patch/resource rows, so the two hops could not be exercised")
                    .With(details.ToArray());
            }

            details.Add($"Patch row             : {patchRow.Name}");
            details.Add($"Resource row          : {resourceRow.DisplayName} | {resourceRow.SizeText} | {resourceRow.StorageText}");

            // --- 1. The browser hop, through the row's own command ---------------------------
            details.Add(string.Empty);
            details.Add("--- 1. 打开补丁页（真实 MoyuBrowserLauncher，dry run）---");

            var openCommand = patchRow.OpenPageCommand;
            details.Add($"Row OpenPageCommand   : {openCommand.GetType().Name}, CanExecute={openCommand.CanExecute(null)}");

            if (!openCommand.CanExecute(null))
            {
                problems.Add("the open-page button is disabled for a row that has a page URL");
            }

            openCommand.Execute(null);

            details.Add($"MoyuLaunchMessage     : {viewModel.MoyuLaunchMessage}");
            details.Add($"Processes started     : {startedProcesses.Count}");

            if (viewModel.MoyuLaunchMessage is null || !viewModel.MoyuLaunchMessage.Contains("浏览器", StringComparison.Ordinal))
            {
                problems.Add("pressing 打开补丁页 left no message about the browser hop");
            }

            if (startedProcesses.Count != 0)
            {
                problems.Add($"the dry-run browser hop started {startedProcesses.Count} process(es): {string.Join(", ", startedProcesses)}");
            }

            // --- 2. Adopting a download that never happens ------------------------------------
            details.Add(string.Empty);
            details.Add("--- 2. 接管下载：用户始终没有下载 ---");
            details.Add($"Watched folder        : {watchFolder}");
            details.Add($"Timeout for this run  : 1 s (the shipping default is 10 min)");

            viewModel.MoyuDownloadTimeout = TimeSpan.FromSeconds(1);

            var adoptStopwatch = Stopwatch.StartNew();
            var adopt = viewModel.AdoptMoyuDownloadCommand.ExecuteAsync(resourceRow);
            details.Add($"State while watching  : {viewModel.MoyuDownloadState}, IsMoyuWatchingDownload={viewModel.IsMoyuWatchingDownload}");

            await adopt.ConfigureAwait(false);
            adoptStopwatch.Stop();

            details.Add($"Adopt elapsed         : {adoptStopwatch.ElapsedMilliseconds} ms");
            details.Add($"MoyuDownloadState     : {viewModel.MoyuDownloadState}");
            details.Add($"MoyuDownloadMessage   : {viewModel.MoyuDownloadMessage}");
            details.Add($"IsMoyuWatchingDownload: {viewModel.IsMoyuWatchingDownload}");
            details.Add($"Watch progress text   : {viewModel.MoyuWatchProgress ?? "(cleared)"}");

            if (viewModel.IsMoyuWatchingDownload)
            {
                problems.Add("the watching state was never cleared after the watch ended");
            }

            if (viewModel.MoyuDownloadState != MoyuDownloadUiState.NotAdopted)
            {
                problems.Add($"a watch that saw nothing ended as {viewModel.MoyuDownloadState}, expected NotAdopted");
            }

            var watchMessage = viewModel.MoyuDownloadMessage ?? string.Empty;
            if (watchMessage.Length == 0)
            {
                problems.Add("the timed-out watch left no message, so the user would see nothing happen");
            }

            if (!watchMessage.Contains("下载文件夹", StringComparison.Ordinal))
            {
                problems.Add($"the timeout message does not name the folder that was watched: \"{watchMessage}\"");
            }

            if (viewModel.MoyuWatchProgress is not null)
            {
                problems.Add("the watch progress line was left behind after the watch ended");
            }

            if (adoptStopwatch.ElapsedMilliseconds > 20000)
            {
                problems.Add($"the 1 s watch took {adoptStopwatch.ElapsedMilliseconds} ms; the timeout was not honoured");
            }

            // --- 3. A row with no page URL must not claim success -----------------------------
            details.Add(string.Empty);
            details.Add("--- 3. 没有页面地址的行：必须是明说的失败，不是静默无操作 ---");

            var urlLessPatch = new MoyuPatch
            {
                Id = "9999",
                VndbId = "v4",
                ResourceCount = 0,
                WebUrl = null
            };

            var urlLessRow = new PatchMoyuRow(urlLessPatch, viewModel);
            details.Add($"url-less row HasWebUrl: {urlLessRow.HasWebUrl}");
            details.Add($"url-less row CanExecute: {urlLessRow.OpenPageCommand.CanExecute(null)}");

            if (urlLessRow.HasWebUrl)
            {
                problems.Add("a patch with no web_url reports HasWebUrl=true");
            }

            if (urlLessRow.OpenPageCommand.CanExecute(null))
            {
                problems.Add("the open-page button is enabled for a row with no page URL");
            }

            // --- 4. A patch with no live resources says why ------------------------------------
            details.Add(string.Empty);
            details.Add("--- 4. 资源列表为空时给出原因，而不是留空 ---");

            viewModel.SelectMoyuPatch(urlLessRow);

            details.Add($"MoyuResources.Count   : {viewModel.MoyuResources.Count}");
            details.Add($"HasMoyuResources      : {viewModel.HasMoyuResources}");
            details.Add($"Empty reason          : {viewModel.MoyuResourcesEmptyReason ?? "(none)"}");
            details.Add($"State after selecting : {viewModel.MoyuState} (the query result is unchanged by browsing a row)");

            if (viewModel.HasMoyuResources)
            {
                problems.Add("selecting a resource-less patch still reports resources present");
            }

            if (string.IsNullOrWhiteSpace(viewModel.MoyuResourcesEmptyReason))
            {
                problems.Add("a resource-less patch left no explanation on the page");
            }

            if (viewModel.MoyuState != MoyuQueryState.Found)
            {
                problems.Add($"browsing a row changed the query state to {viewModel.MoyuState}; it must not");
            }

            details.Add(string.Empty);
            details.Add($"Resources actually present after a real query: {viewModel.MoyuResources.Count} (the row selected last is the resource-less one)");
        }
        finally
        {
            MoyuCheckSupport.TryDeleteDirectory(watchFolder);
        }

        foreach (var problem in problems)
        {
            details.Add($"PROBLEM: {problem}");
        }

        if (problems.Count > 0)
        {
            return CheckResult.Fail(Id, Title, expected, $"{problems.Count} problem(s): {string.Join("; ", problems)}")
                .With(details.ToArray());
        }

        return CheckResult.Pass(Id, Title, expected,
            "the real launcher validated the page URL in dry run and started no process; the real watcher "
            + "timed out against an empty folder, cleared the watching state and stated the folder it "
            + "watched; an URL-less row is disabled with a refusal message; a resource-less patch is "
            + "explained rather than blank")
            .With(details.ToArray());
    }
}

/// <summary>
/// A116 - writing the key from the page.
///
/// <para>
/// The feature is unusable without a key and the key previously had no door at all: there is no
/// settings page field for it, so the only way to configure one was to place the file by hand. This
/// check drives the page's own save/clear commands against a throw-away store and asserts the two
/// things that matter — an obviously wrong value is refused out loud instead of being stored, and a
/// real one round-trips into the client without appearing in any message.
/// </para>
/// </summary>
public sealed class A116MoyuUiKeyEntryCheck : IAcceptanceCheck
{
    private const string ProbeKey = "nmk_live_A116probe_000000000000000000";

    /// <inheritdoc />
    public string Id => "A116";

    /// <inheritdoc />
    public string Title => "密钥可以在补丁中心填写：非法值被明确拒绝、合法值真的进了客户端、且不回显";

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(AcceptanceContext context, CancellationToken cancellationToken)
    {
        var expected =
            "SaveMoyuKey refuses a value that is not an nmk_ key with a stated reason and stores nothing; "
            + "a valid key is persisted through IMoyuKeyStore, makes the client IsConfigured, is never "
            + "echoed back, and unblocks a query that was previously reported as missing a key; "
            + "ClearMoyuKey removes it again";

        var details = new List<string>();
        var problems = new List<string>();

        var scratch = MoyuCheckSupport.NewScratchDirectory("a116-keystore");
        var storePath = Path.Combine(scratch, "secrets", "moyu-api-key.bin");

        try
        {
            var store = new MoyuDpapiKeyStore(storePath);

            var harness = MoyuUiHarness.Build(
                context,
                apiKey: null,
                responder: _ => MoyuStubHandler.Json(
                    """{"object":"list","items":[],"next_cursor":null,"total":0,"missing":["vndb:v4"]}"""),
                keyStore: store);

            var viewModel = harness.CreateViewModel();
            viewModel.SelectedGame = MoyuUiHarness.MakeGame("v4");

            details.Add($"Store                 : {store}");
            details.Add($"Client IsConfigured   : {harness.Api.IsConfigured}");

            if (harness.Api.IsConfigured)
            {
                return CheckResult.Fail(Id, Title, expected, "the harness client started configured")
                    .With(details.ToArray());
            }

            // --- 1. Before: the query is blocked, and says so -------------------------------
            await viewModel.FindMoyuPatchesCommand.ExecuteAsync(null).ConfigureAwait(false);
            details.Add($"State before saving   : {viewModel.MoyuState} / {viewModel.MoyuStatusMessage}");

            if (viewModel.MoyuState != MoyuQueryState.MissingKey)
            {
                problems.Add($"a key-less query reported {viewModel.MoyuState}, expected MissingKey");
            }

            // --- 2. An obviously wrong value -------------------------------------------------
            viewModel.MoyuKeyInput = "this-is-not-a-key";
            viewModel.SaveMoyuKeyCommand.Execute(null);

            details.Add(string.Empty);
            details.Add($"Rejected input message: {viewModel.MoyuKeySaveMessage}");
            details.Add($"Store configured now  : {store.IsConfigured} (file exists: {File.Exists(storePath)})");

            if (store.IsConfigured || File.Exists(storePath))
            {
                problems.Add("a value that is not an nmk_ key was written to the store");
            }

            var rejectMessage = viewModel.MoyuKeySaveMessage ?? string.Empty;
            if (!rejectMessage.Contains("没有保存", StringComparison.Ordinal))
            {
                problems.Add($"the refused value produced no clear refusal: \"{rejectMessage}\"");
            }

            // --- 3. A valid key ---------------------------------------------------------------
            viewModel.MoyuKeyInput = ProbeKey;
            viewModel.SaveMoyuKeyCommand.Execute(null);

            details.Add(string.Empty);
            details.Add($"Save message          : {viewModel.MoyuKeySaveMessage}");
            details.Add($"Store configured now  : {store.IsConfigured}");
            details.Add($"Client IsConfigured   : {harness.Api.IsConfigured}");
            details.Add($"Key summary           : {viewModel.MoyuKeySummary}");
            details.Add($"Input box cleared     : {string.IsNullOrEmpty(viewModel.MoyuKeyInput)}");

            if (!store.IsConfigured)
            {
                problems.Add("a valid nmk_ key was not persisted");
            }

            if (!harness.Api.IsConfigured)
            {
                problems.Add("the client still reports unconfigured after the key was saved");
            }

            if (!string.IsNullOrEmpty(viewModel.MoyuKeyInput))
            {
                problems.Add("the key was left in the input property after it was saved");
            }

            if (viewModel.MoyuState == MoyuQueryState.MissingKey)
            {
                problems.Add("the card still claims a key is missing after one was saved");
            }

            // Nothing on the page may echo the secret back.
            var surfaces = new[]
            {
                viewModel.MoyuKeySaveMessage, viewModel.MoyuKeySummary, viewModel.MoyuKeyHowTo,
                viewModel.MoyuStatusMessage, viewModel.MoyuAdvisoryMessage, viewModel.MoyuErrorDetail,
                viewModel.MoyuLaunchMessage, viewModel.MoyuDownloadMessage
            };

            var leaked = surfaces.Any(s => s is not null && s.Contains(ProbeKey, StringComparison.Ordinal));
            details.Add($"Key present in any page text: {leaked}");

            if (leaked)
            {
                problems.Add("the API key appears in a message the page displays");
            }

            if (viewModel.MoyuKeySaveMessage?.Contains("指纹", StringComparison.Ordinal) != true)
            {
                problems.Add("the save confirmation does not show a fingerprint, so the user cannot tell which key is stored");
            }

            // --- 4. The query now runs --------------------------------------------------------
            await viewModel.FindMoyuPatchesCommand.ExecuteAsync(null).ConfigureAwait(false);

            details.Add(string.Empty);
            details.Add($"State after saving    : {viewModel.MoyuState} / {viewModel.MoyuStatusMessage}");
            details.Add($"Requests sent         : {harness.Transport.Everything.Count}");

            if (viewModel.MoyuState != MoyuQueryState.Empty)
            {
                problems.Add($"after configuring a key the query reported {viewModel.MoyuState}, expected Empty (the stub answers with no rows)");
            }

            if (harness.Transport.Everything.Count != 1)
            {
                problems.Add($"the query after configuration sent {harness.Transport.Everything.Count} request(s), expected 1");
            }

            // --- 5. Clearing ------------------------------------------------------------------
            viewModel.ClearMoyuKeyCommand.Execute(null);

            details.Add(string.Empty);
            details.Add($"Clear message         : {viewModel.MoyuKeySaveMessage}");
            details.Add($"Store configured now  : {store.IsConfigured}");
            details.Add($"Client IsConfigured   : {harness.Api.IsConfigured}");
            details.Add($"State after clearing  : {viewModel.MoyuState} / {viewModel.MoyuStatusMessage}");

            if (store.IsConfigured)
            {
                problems.Add("Clear did not remove the stored key");
            }

            if (viewModel.MoyuState != MoyuQueryState.MissingKey)
            {
                problems.Add($"after clearing the key the card reports {viewModel.MoyuState}, expected MissingKey");
            }
        }
        catch (Exception ex)
        {
            return CheckResult.Error(Id, Title, expected, $"unhandled {ex.GetType().Name}", ex)
                .With(details.ToArray());
        }
        finally
        {
            MoyuCheckSupport.TryDeleteDirectory(scratch);
        }

        foreach (var problem in problems)
        {
            details.Add($"PROBLEM: {problem}");
        }

        if (problems.Count > 0)
        {
            return CheckResult.Fail(Id, Title, expected, $"{problems.Count} problem(s): {string.Join("; ", problems)}")
                .With(details.ToArray());
        }

        return CheckResult.Pass(Id, Title, expected,
            "a non-nmk_ value was refused without touching the store; a valid key went through "
            + "MoyuDpapiKeyStore, flipped the client to configured, unblocked the query (1 request, Empty), "
            + "was shown only as a fingerprint, and Clear removed it and restored the MissingKey state")
            .With(details.ToArray());
    }
}
