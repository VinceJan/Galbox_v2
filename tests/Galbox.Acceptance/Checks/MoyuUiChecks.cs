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
/// card's status fields and handlers.</description></item>
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
            + "contains \"在线补丁源：未实现\", and binds the status fields, the query command and the "
            + "handlers of every control the card offers";

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

        // The live proof of the same thing is checked further down, once the ViewModel exists:
        // MoyuUiHarness.CreateViewModel prefers the constructor that accepts the moyu services and
        // records whether it was used.

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

        var live = harness.CreateViewModel();
        var vm = new MoyuUiProbe(live);
        vm.Set("SelectedGame", MoyuUiHarness.MakeGame("v4"));

        details.Add(string.Empty);
        details.Add($"Constructed with the moyu dependencies : {harness.LastProbe?.BuiltWithMoyuDependencies}");

        if (harness.LastProbe?.BuiltWithMoyuDependencies != true)
        {
            problems.Add("the page's ViewModel was constructed WITHOUT the moyu services, so the page's own "
                         + "construction path does not reach the online source");
        }

        details.Add(string.Empty);
        details.Add("Online-source surface on the live ViewModel:");
        var surface = new[]
        {
            "FindMoyuPatchesCommand", "MoyuState", "MoyuStatusCodeText", "MoyuStatusMessage",
            "MoyuAdvisoryMessage", "MoyuErrorDetail", "MoyuAnchorUsed", "MoyuPatches", "MoyuResources",
            "IsMoyuQuerying", "SelectMoyuPatchCommand", "OpenMoyuPatchPageCommand",
            "AdoptMoyuDownloadCommand", "CancelMoyuDownloadCommand", "SaveMoyuKeyCommand", "ClearMoyuKeyCommand"
        };

        foreach (var member in surface)
        {
            var present = vm.Has(member);
            details.Add($"   [{(present ? "OK" : "MISSING")}] {member}");
            if (!present)
            {
                problems.Add($"the ViewModel exposes no {member}, so that part of the online source has no door");
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var query = await vm.CallAsync("FindMoyuPatchesAsync").ConfigureAwait(false);
        stopwatch.Stop();

        details.Add(string.Empty);
        details.Add($"Query elapsed          : {stopwatch.ElapsedMilliseconds} ms (returned {query ?? "(null)"})");
        details.Add($"Requests on the wire   : {harness.Transport.Everything.Count}");
        foreach (var (method, uri) in harness.Transport.Everything)
        {
            details.Add($"   {method} {uri.PathAndQuery}  ->  allowed={MoyuComplianceGuard.IsAllowedApiUri(uri)}");
        }

        details.Add($"MoyuState              : {vm.StateName("MoyuState") ?? "(null)"}");
        details.Add($"MoyuStatusCodeText     : {vm.GetString("MoyuStatusCodeText") ?? "(null)"}");
        details.Add($"MoyuStatusMessage      : {vm.GetString("MoyuStatusMessage") ?? "(null)"}");
        details.Add($"MoyuPatches.Count      : {vm.Count("MoyuPatches")}");
        details.Add($"MoyuResources.Count    : {vm.Count("MoyuResources")}");
        details.Add($"Probe missing members  : {vm.MissingSummary()}");

        if (harness.Transport.Everything.Count != 1)
        {
            problems.Add($"a query through the ViewModel produced {harness.Transport.Everything.Count} request(s), expected exactly 1");
        }

        if (vm.StateName("MoyuState") != "Found")
        {
            problems.Add($"the ViewModel reported MoyuState={vm.StateName("MoyuState") ?? "(missing)"} for a one-row answer, expected Found");
        }

        if (vm.Count("MoyuPatches") != 1)
        {
            problems.Add($"the ViewModel holds {vm.Count("MoyuPatches")} patch row(s), expected 1");
        }

        if (vm.Count("MoyuResources") != 1)
        {
            problems.Add($"the ViewModel lists {vm.Count("MoyuResources")} resource row(s) for the found patch, expected 1");
        }

        var resourceRow = vm.Items("MoyuResources").FirstOrDefault();
        if (resourceRow is not null)
        {
            var rowProbe = new MoyuUiProbe(resourceRow);
            details.Add($"First resource row     : {rowProbe.GetString("DisplayName")} | {rowProbe.GetString("SizeText")}");

            if (rowProbe.Get("Resource") is MoyuResource resource)
            {
                var source = resource.ToPatchSourceInfo();
                details.Add($"  engine source info   : kind={source.Kind}, patch={source.PatchId}, "
                            + $"resource={source.ResourceId}, page={source.WebUrl}");

                if (source.Kind != "moyu-moe-v2" || source.PatchId != "86" || source.ResourceId != "6262")
                {
                    problems.Add($"the resource does not carry usable engine source metadata: {source.Kind}/{source.PatchId}/{source.ResourceId}");
                }
            }
            else
            {
                problems.Add("the resource row does not expose the underlying MoyuResource, so no source metadata can be recorded");
            }
        }

        // --- 3. The XAML ---------------------------------------------------------------------
        var repoRoot = RepoLocator.FindRepoRoot();
        var pagePath = repoRoot is null
            ? string.Empty
            : Path.Combine(RepoLocator.AppProject(repoRoot), "Views", "PatchCenterPage.xaml");

        details.Add(string.Empty);
        details.Add($"Patch centre page      : {(pagePath.Length == 0 ? "(repository root not found)" : pagePath)}");

        if (pagePath.Length == 0 || !File.Exists(pagePath))
        {
            problems.Add("the patch centre page could not be located for the XAML assertions");
        }
        else
        {
            var xaml = File.ReadAllText(pagePath);

            foreach (var text in new[] { "在线补丁源：未实现", "OnlinePatchSourceNotice" })
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

            // Every control the card offers must have a handler: a Button with no Command and no Click
            // is exactly the "点了没反应的按钮" defect class of this project's spec.
            var mustBeWired = new[]
            {
                "ViewModel.SaveMoyuKeyCommand",
                "ViewModel.ClearMoyuKeyCommand",
                "ViewModel.CancelMoyuDownloadCommand",
                "OnMoyuKeyPasswordChanged"
            };

            foreach (var handler in mustBeWired)
            {
                var wired = xaml.Contains(handler, StringComparison.Ordinal);
                details.Add($"   [{(wired ? "wired" : "NOT WIRED")}] {handler}");
                if (!wired)
                {
                    problems.Add($"the page never references {handler}, so that control would do nothing");
                }
            }
        }

        // The download-model paragraph is the honest description of the browser hop: it has to say
        // that the site gives no direct link, otherwise the user is told to expect something the
        // service deliberately does not provide.
        var explanation = vm.GetString("MoyuDownloadModelExplanation");
        details.Add(string.Empty);
        details.Add($"Download model text    : {explanation ?? "(missing)"}");

        if (explanation is null || !explanation.Contains("直链", StringComparison.Ordinal)
            || !explanation.Contains("下载", StringComparison.Ordinal))
        {
            problems.Add("the download-model explanation does not state that no direct link exists");
        }

        if (vm.Missing.Count > 0)
        {
            problems.Add($"{vm.Missing.Count} member(s) of the online-source surface are missing: {vm.MissingSummary()}");
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
            + "the real ViewModel sent exactly 1 request and produced 1 patch row with 1 resource carrying "
            + "moyu-moe-v2 source metadata; the page no longer claims the source is unimplemented, binds "
            + "8 status fields and wires 4 handlers")
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
            + "nmk_ key and how to get one, shows zero result rows, carries no error detail, and sends "
            + "ZERO HTTP requests";

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

        var vm = new MoyuUiProbe(harness.CreateViewModel());
        vm.Set("SelectedGame", MoyuUiHarness.MakeGame("v4"));

        details.Add($"State before any query : {vm.StateName("MoyuState") ?? "(missing)"} / {vm.GetString("MoyuStatusCodeText") ?? "(missing)"}");
        details.Add($"  message              : {vm.GetString("MoyuStatusMessage") ?? "(missing)"}");
        details.Add($"Key summary (shown)    : {vm.GetString("MoyuKeySummary") ?? "(missing)"}");
        details.Add($"IsMoyuKeyConfigured    : {vm.GetBool("IsMoyuKeyConfigured")?.ToString() ?? "(missing)"}");

        await vm.CallAsync("FindMoyuPatchesAsync").ConfigureAwait(false);

        details.Add(string.Empty);
        details.Add($"State after the query  : {vm.StateName("MoyuState") ?? "(missing)"}");
        details.Add($"Status code            : {vm.GetString("MoyuStatusCodeText") ?? "(missing)"}");
        details.Add($"Status message         : {vm.GetString("MoyuStatusMessage") ?? "(missing)"}");
        details.Add($"Advisory               : {vm.GetString("MoyuAdvisoryMessage") ?? "(missing)"}");
        details.Add($"Error detail           : {vm.GetString("MoyuErrorDetail") ?? "(none — correctly absent: a missing key is not a query failure)"}");
        details.Add($"MoyuPatches.Count      : {vm.Count("MoyuPatches")}");
        details.Add($"Requests on the wire   : {harness.Transport.Everything.Count}");
        details.Add($"Probe missing members  : {vm.MissingSummary()}");

        var state = vm.StateName("MoyuState");
        if (state is null)
        {
            problems.Add("the ViewModel exposes no MoyuState, so a key-less query cannot be reported at all");
        }
        else if (state == "Empty")
        {
            problems.Add("a missing key was reported as \"no results\" (Empty) — the exact defect this block exists for");
        }
        else if (state != "MissingKey")
        {
            problems.Add($"MoyuState is {state}, expected MissingKey");
        }

        if (vm.Count("MoyuPatches") != 0)
        {
            problems.Add($"the key-less path produced {vm.Count("MoyuPatches")} row(s); a key-less client must invent nothing");
        }

        var headline = vm.GetString("MoyuStatusMessage") ?? string.Empty;
        if (!headline.Contains("密钥", StringComparison.Ordinal))
        {
            problems.Add($"the headline does not say a key is missing: \"{headline}\"");
        }

        if (!headline.Contains("无法查询", StringComparison.Ordinal))
        {
            problems.Add($"the headline does not say the query could not be made: \"{headline}\"");
        }

        var code = vm.GetString("MoyuStatusCodeText") ?? string.Empty;
        if (code != "MOYU_NOT_CONFIGURED")
        {
            problems.Add($"the status code is \"{code}\", expected MOYU_NOT_CONFIGURED — a distinct code is what keeps the four outcomes apart");
        }

        if (string.IsNullOrWhiteSpace(vm.GetString("MoyuAdvisoryMessage")))
        {
            problems.Add("no advisory text is shown for the missing-key state");
        }

        if (vm.Get("MoyuErrorDetail") is string detail && detail.Length > 0)
        {
            problems.Add($"the missing-key state carries an error detail (\"{detail}\"), which makes it look like a query failure");
        }

        // What it is, where to get it: both must be on the page, not just in a log line.
        var keyText = (vm.GetString("MoyuKeySummary") ?? string.Empty) + "\n" + (vm.GetString("MoyuKeyHowTo") ?? string.Empty);
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

        if (vm.Missing.Count > 0)
        {
            problems.Add($"{vm.Missing.Count} member(s) missing: {vm.MissingSummary()}");
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
            "MoyuState=MissingKey / MOYU_NOT_CONFIGURED with \"未配置 nmk_ 密钥，无法查询\", 0 result rows, "
            + "no error detail, the panel names nmk_ and developer.nextmoe.dev, and 0 HTTP requests were sent")
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
/// looked up, and it must do so <b>without sending a request</b>. The client here is configured with a
/// key, so a request would happily be answered — the only reason it is not sent is that the ViewModel
/// decides the anchor before it looks at the key or builds a URI.
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
            "for a game with no vndb id (including one whose only identifier is a Bangumi subject id), the "
            + "ViewModel reports MoyuState=NoAnchor naming the missing vndb identifier, sends ZERO HTTP "
            + "requests, and shows no rows";

        var details = new List<string>();
        var problems = new List<string>();

        var harness = MoyuUiHarness.Build(
            context,
            apiKey: "nmk_live_A112probe_000000000000000000",
            responder: _ => MoyuStubHandler.Json(
                """{"object":"list","items":[{"object":"patch","id":"86","vndb_id":"v4","catalog_work_id":"86","type":["manual"],"language":["zh-Hans"],"platform":["windows"],"resource_count":1,"web_url":"https://www.moyu.moe/patch/86/introduction"}],"next_cursor":null,"total":1,"missing":[]}"""));

        var cases = new (string Label, GameInfo Game)[]
        {
            ("Bangumi 来源的作品（source=bangumi，没有 VndbId）", MoyuUiHarness.MakeGame(vndbId: null, sourceType: "bangumi", sourceId: "1234")),
            ("从未刮削过的作品（三个 id 都没有）", MoyuUiHarness.MakeGame(vndbId: null)),
            ("VndbId 里放的其实是 Bangumi 值", MoyuUiHarness.MakeGame(vndbId: "bangumi:1234"))
        };

        var missingMembers = new List<string>();

        foreach (var (label, game) in cases)
        {
            var vm = new MoyuUiProbe(harness.CreateViewModel());
            vm.Set("SelectedGame", game);

            var before = harness.Transport.Everything.Count;
            await vm.CallAsync("FindMoyuPatchesAsync").ConfigureAwait(false);
            var after = harness.Transport.Everything.Count;

            details.Add($"--- {label} ---");
            details.Add($"  VndbId / SourceType / SourceId : {game.VndbId ?? "(null)"} / {game.SourceType ?? "(null)"} / {game.SourceId ?? "(null)"}");
            details.Add($"  MoyuState                      : {vm.StateName("MoyuState") ?? "(missing)"}");
            details.Add($"  Status code                    : {vm.GetString("MoyuStatusCodeText") ?? "(missing)"}");
            details.Add($"  Status message                 : {vm.GetString("MoyuStatusMessage") ?? "(missing)"}");
            details.Add($"  Advisory                       : {vm.GetString("MoyuAdvisoryMessage") ?? "(missing)"}");
            details.Add($"  MoyuPatches.Count              : {vm.Count("MoyuPatches")}");
            details.Add($"  Requests sent by this query    : {after - before}");
            details.Add(string.Empty);

            if (after != before)
            {
                problems.Add($"{label}: {after - before} request(s) were sent for a game that cannot be looked up");
            }

            var state = vm.StateName("MoyuState");
            if (state is null)
            {
                problems.Add($"{label}: the ViewModel exposes no MoyuState");
            }
            else if (state != "NoAnchor")
            {
                problems.Add($"{label}: MoyuState is {state}, expected NoAnchor");
            }

            if (vm.Count("MoyuPatches") > 0)
            {
                problems.Add($"{label}: {vm.Count("MoyuPatches")} row(s) were produced without an anchor");
            }

            var headline = vm.GetString("MoyuStatusMessage") ?? string.Empty;
            if (!headline.Contains("无法查询", StringComparison.Ordinal))
            {
                problems.Add($"{label}: the headline does not say the query cannot be made: \"{headline}\"");
            }

            if (!headline.Contains("vndb", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{label}: the headline does not name the missing vndb identifier: \"{headline}\"");
            }

            if (vm.GetString("MoyuStatusCodeText") != "MOYU_NO_ANCHOR")
            {
                problems.Add($"{label}: the status code is \"{vm.GetString("MoyuStatusCodeText") ?? "(missing)"}\", expected MOYU_NO_ANCHOR");
            }

            if (string.IsNullOrWhiteSpace(vm.GetString("MoyuAdvisoryMessage")))
            {
                problems.Add($"{label}: no advisory text is shown");
            }

            missingMembers.AddRange(vm.Missing);
        }

        var totalRequests = harness.Transport.Everything.Count;
        details.Add($"Total requests sent across all three no-anchor cases: {totalRequests}");

        if (totalRequests != 0)
        {
            problems.Add($"{totalRequests} request(s) were sent in total for games with no anchor; the answer is knowable without the network");
        }

        if (missingMembers.Count > 0)
        {
            problems.Add($"{missingMembers.Distinct().Count()} member(s) missing: {string.Join("; ", missingMembers.Distinct())}");
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
            "3/3 anchor-less games reported NoAnchor / MOYU_NO_ANCHOR with \"无法查询：该作品没有 vndb 标识\", "
            + "0 rows, and 0 HTTP requests were sent even though the client was configured with a key")
            .With(details.ToArray());
    }
}

/// <summary>
/// A113 - "no results" and "the query failed" are two different sentences.
///
/// <para>
/// This is the second half of the honesty rule the project keeps re-learning: an empty successful
/// answer is information ("moyu has nothing for this game"), while a failed query is the absence of
/// an answer. The check drives both through the same ViewModel and asserts that the state, the code,
/// the headline and the error surface all differ - and, in particular, that only the failure carries
/// the service's own reason, and that only the empty case is allowed to say "no results".
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
            "an answered-but-empty lookup reports MoyuState=Empty / MOYU_NO_RESULTS with a \"没有找到补丁\" "
            + "headline and no error detail; a transport failure reports MoyuState=Failed / MOYU_QUERY_FAILED "
            + "with the service's reason attached; the state names, codes and headlines all differ";

        var details = new List<string>();
        var problems = new List<string>();

        // --- A. Answered, zero rows ----------------------------------------------------------
        var emptyHarness = MoyuUiHarness.Build(
            context,
            apiKey: "nmk_live_A113probe_empty_000000000000",
            responder: _ => MoyuStubHandler.Json(
                """{"object":"list","items":[],"next_cursor":null,"total":0,"missing":["vndb:v4"]}"""));

        var emptyVm = new MoyuUiProbe(emptyHarness.CreateViewModel());
        emptyVm.Set("SelectedGame", MoyuUiHarness.MakeGame("v4"));
        await emptyVm.CallAsync("FindMoyuPatchesAsync").ConfigureAwait(false);

        details.Add("--- A. 查询得到了回答，但结果为空 ---");
        details.Add($"  MoyuState        : {emptyVm.StateName("MoyuState") ?? "(missing)"}");
        details.Add($"  Status code      : {emptyVm.GetString("MoyuStatusCodeText") ?? "(missing)"}");
        details.Add($"  Status message   : {emptyVm.GetString("MoyuStatusMessage") ?? "(missing)"}");
        details.Add($"  Advisory         : {emptyVm.GetString("MoyuAdvisoryMessage") ?? "(missing)"}");
        details.Add($"  Error detail     : {emptyVm.GetString("MoyuErrorDetail") ?? "(none)"}");
        details.Add($"  Rows             : {emptyVm.Count("MoyuPatches")}");
        details.Add($"  Requests sent    : {emptyHarness.Transport.Everything.Count}");
        details.Add(string.Empty);

        if (emptyHarness.Transport.Everything.Count != 1)
        {
            problems.Add($"the empty case sent {emptyHarness.Transport.Everything.Count} request(s), expected 1 (the question really was asked)");
        }

        if (emptyVm.StateName("MoyuState") != "Empty")
        {
            problems.Add($"an answered-but-empty lookup reported {emptyVm.StateName("MoyuState") ?? "(missing)"}, expected Empty");
        }

        if (emptyVm.GetString("MoyuStatusCodeText") != "MOYU_NO_RESULTS")
        {
            problems.Add($"the empty case's code is \"{emptyVm.GetString("MoyuStatusCodeText") ?? "(missing)"}\", expected MOYU_NO_RESULTS");
        }

        var emptyDetail = emptyVm.GetString("MoyuErrorDetail");
        if (emptyDetail is not null && emptyDetail.Length > 0)
        {
            problems.Add($"the empty case carries an error detail (\"{emptyDetail}\"), so it reads like a failure");
        }

        if (emptyVm.GetString("MoyuStatusMessage")?.Contains("没有找到", StringComparison.Ordinal) != true)
        {
            problems.Add($"the empty headline does not say nothing was found: \"{emptyVm.GetString("MoyuStatusMessage")}\"");
        }

        // --- B. The transport never answers ---------------------------------------------------
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

        failedHarness.Options.MaxRetries = 0;              // one attempt; the retry policy is A67's business
        failedHarness.Options.EnableConditionalRequests = false;

        var failedVm = new MoyuUiProbe(failedHarness.CreateViewModel());
        failedVm.Set("SelectedGame", MoyuUiHarness.MakeGame("v4"));

        var stopwatch = Stopwatch.StartNew();
        await failedVm.CallAsync("FindMoyuPatchesAsync").ConfigureAwait(false);
        stopwatch.Stop();

        details.Add("--- B. 传输层没有给出回答 ---");
        details.Add($"  MoyuState        : {failedVm.StateName("MoyuState") ?? "(missing)"}");
        details.Add($"  Status code      : {failedVm.GetString("MoyuStatusCodeText") ?? "(missing)"}");
        details.Add($"  Status message   : {failedVm.GetString("MoyuStatusMessage") ?? "(missing)"}");
        details.Add($"  Advisory         : {failedVm.GetString("MoyuAdvisoryMessage") ?? "(missing)"}");
        details.Add($"  Error detail     : {failedVm.GetString("MoyuErrorDetail") ?? "(none)"}");
        details.Add($"  Rows             : {failedVm.Count("MoyuPatches")}");
        details.Add($"  Requests sent    : {failedHarness.Transport.Everything.Count}");
        details.Add($"  Elapsed          : {stopwatch.ElapsedMilliseconds} ms");
        details.Add(string.Empty);

        if (failedVm.StateName("MoyuState") != "Failed")
        {
            problems.Add($"a 503 produced {failedVm.StateName("MoyuState") ?? "(missing)"}, expected Failed");
        }

        if (failedVm.GetString("MoyuStatusCodeText") != "MOYU_QUERY_FAILED")
        {
            problems.Add($"the failure's code is \"{failedVm.GetString("MoyuStatusCodeText") ?? "(missing)"}\", expected MOYU_QUERY_FAILED");
        }

        var errorDetail = failedVm.GetString("MoyuErrorDetail") ?? string.Empty;
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
            problems.Add($"the failure detail drops the upstream code, which is what a support request would need: \"{errorDetail}\"");
        }

        if (failedVm.GetString("MoyuStatusMessage")?.Contains("查询失败", StringComparison.Ordinal) != true)
        {
            problems.Add($"the failure headline does not say the query failed: \"{failedVm.GetString("MoyuStatusMessage")}\"");
        }

        if (failedVm.Count("MoyuPatches") > 0)
        {
            problems.Add($"the failure produced {failedVm.Count("MoyuPatches")} row(s); a failure must not invent rows");
        }

        // --- C. The two must not be confusable ------------------------------------------------
        details.Add("--- C. 两者的可分性 ---");
        var pairs = new (string Field, string Empty, string Failed)[]
        {
            ("MoyuState", emptyVm.StateName("MoyuState") ?? "(missing)", failedVm.StateName("MoyuState") ?? "(missing)"),
            ("StatusCodeText", emptyVm.GetString("MoyuStatusCodeText") ?? "(missing)", failedVm.GetString("MoyuStatusCodeText") ?? "(missing)"),
            ("StatusMessage", emptyVm.GetString("MoyuStatusMessage") ?? "(missing)", failedVm.GetString("MoyuStatusMessage") ?? "(missing)")
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

        details.Add($"   ErrorDetail      empty={(emptyDetail is null || emptyDetail.Length == 0 ? "(absent)" : "present")}  "
                    + $"failed={(errorDetail.Length == 0 ? "(absent)" : "present")}");

        if ((emptyDetail is not null && emptyDetail.Length > 0) || errorDetail.Length == 0)
        {
            problems.Add("the error surface does not separate the two cases (present for empty, or absent for failed)");
        }

        var missing = emptyVm.Missing.Concat(failedVm.Missing).Distinct().ToList();
        if (missing.Count > 0)
        {
            problems.Add($"{missing.Count} member(s) missing: {string.Join("; ", missing)}");
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
            "Empty / MOYU_NO_RESULTS / \"查询成功，但没有找到补丁\" with no error detail; "
            + "Failed / MOYU_QUERY_FAILED / \"查询失败：这次没有拿到结果\" carrying HTTP 503 + UPSTREAM_TIMEOUT; "
            + "state name, code and headline all differ")
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
            + "never under /api; EnsureApiUri still refuses the forbidden surface (including a traversal "
            + "attempt); and any moyu request recorded on the application's own pipelines stays inside "
            + "the allow-list";

        var details = new List<string>();
        var problems = new List<string>();

        context.Traffic.Clear();

        var harness = MoyuUiHarness.Build(
            context,
            apiKey: "nmk_live_A114probe_000000000000000000",
            responder: _ => MoyuStubHandler.Json(
                """{"object":"list","items":[{"object":"patch","id":"86","vndb_id":"v4","catalog_work_id":"86","type":["manual"],"language":["zh-Hans"],"platform":["windows"],"resource_count":1,"web_url":"https://www.moyu.moe/patch/86/introduction"}],"next_cursor":null,"total":1,"missing":[]}"""));

        var vm = new MoyuUiProbe(harness.CreateViewModel());
        vm.Set("SelectedGame", MoyuUiHarness.MakeGame("v4"));

        await vm.CallAsync("FindMoyuPatchesAsync").ConfigureAwait(false);

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
            "/v2/moyu/patches/../../api/v1/search"
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

        var legitimate = MoyuComplianceGuard.EnsureApiUri(MoyuOptions.DefaultBaseAddress, "/v2/moyu/patches?refs=vndb%3Av4");
        details.Add($"   [allowed] /v2/moyu/patches?refs=vndb%3Av4 -> {legitimate}");

        if (!MoyuComplianceGuard.IsAllowedApiUri(legitimate))
        {
            problems.Add("the guard refuses the one legitimate path, so the feature could not work at all");
        }

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

            if (MoyuComplianceGuard.IsForbiddenPath(uri?.AbsolutePath))
            {
                problems.Add($"a recorded moyu request targeted the Disallowed surface: {exchange.Method} {exchange.Url}");
            }
        }

        var uiExchanges = context.Traffic.For("MoyuUiProbe");
        details.Add($"   (of which produced by the patch centre's client: {uiExchanges.Count})");

        if (recorded.Length == 0)
        {
            problems.Add("the UI's request never reached the recorder, so the compliance assertion has no evidence");
        }

        if (vm.Missing.Count > 0)
        {
            problems.Add($"{vm.Missing.Count} member(s) missing: {vm.MissingSummary()}");
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
            + $"{MoyuComplianceGuard.ApiHost}; {mustRefuse.Length}/{mustRefuse.Length} forbidden paths refused by "
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
/// It also asserts the opposite direction for each: a row whose page URL is missing produces a stated
/// refusal and a disabled button (never a silent no-op), and a patch with no live resource is
/// explained rather than left blank.
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
            + "state; selecting a patch with no live resource replaces the resource list with a reason";

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

            var vm = new MoyuUiProbe(harness.CreateViewModel());
            vm.Set("SelectedGame", MoyuUiHarness.MakeGame("v4"));

            await vm.CallAsync("FindMoyuPatchesAsync").ConfigureAwait(false);

            var patchRows = vm.Items("MoyuPatches");
            var resourceRows = vm.Items("MoyuResources");

            details.Add($"Found patch rows      : {vm.Count("MoyuPatches")}");
            details.Add($"Found resource rows   : {vm.Count("MoyuResources")}");

            if (patchRows.Count == 0 || resourceRows.Count == 0)
            {
                return CheckResult.Fail(Id, Title, expected,
                    $"the query produced {patchRows.Count} patch row(s) and {resourceRows.Count} resource row(s), "
                    + "so the two hops could not be exercised")
                    .With(details.ToArray());
            }

            var patchRow = new MoyuUiProbe(patchRows[0]);
            var resourceRow = new MoyuUiProbe(resourceRows[0]);

            details.Add($"Patch row             : {patchRow.GetString("Name") ?? "(missing)"} | hasUrl={patchRow.GetBool("HasWebUrl")?.ToString() ?? "(missing)"}");
            details.Add($"Resource row          : {resourceRow.GetString("DisplayName") ?? "(missing)"} | {resourceRow.GetString("SizeText") ?? "(missing)"} | {resourceRow.GetString("StorageText") ?? "(missing)"}");

            // --- 1. The browser hop, through the row's own command ---------------------------
            details.Add(string.Empty);
            details.Add("--- 1. 打开补丁页（真实 MoyuBrowserLauncher，dry run）---");

            if (!patchRow.Has("OpenPageCommand"))
            {
                problems.Add("the patch row exposes no OpenPageCommand, so the button has nothing to run");
            }
            else
            {
                var open = patchRow.Get("OpenPageCommand") as System.Windows.Input.ICommand;
                details.Add($"Row OpenPageCommand   : {(open is null ? "(null)" : open.GetType().Name)}, CanExecute={(open?.CanExecute(null))?.ToString() ?? "(n/a)"}");

                if (open is null)
                {
                    problems.Add("the patch row's OpenPageCommand is null");
                }
                else
                {
                    if (!open.CanExecute(null))
                    {
                        problems.Add("the open-page button is disabled for a row that has a page URL");
                    }

                    open.Execute(null);

                    var launchMessage = vm.GetString("MoyuLaunchMessage");
                    details.Add($"MoyuLaunchMessage     : {launchMessage ?? "(none)"}");
                    details.Add($"Processes started     : {startedProcesses.Count}");

                    if (launchMessage is null || !launchMessage.Contains("浏览器", StringComparison.Ordinal))
                    {
                        problems.Add("pressing 打开补丁页 left no message about the browser hop");
                    }

                    if (startedProcesses.Count != 0)
                    {
                        problems.Add($"the dry-run browser hop started {startedProcesses.Count} process(es): {string.Join(", ", startedProcesses)}");
                    }
                }
            }

            // --- 2. Adopting a download that never happens ------------------------------------
            details.Add(string.Empty);
            details.Add("--- 2. 接管下载：用户始终没有下载 ---");
            details.Add($"Watched folder        : {watchFolder}");
            details.Add($"Timeout for this run  : 1 s (the shipping default is 10 min)");

            vm.Set("MoyuDownloadTimeout", TimeSpan.FromSeconds(1));

            var adoptStopwatch = Stopwatch.StartNew();
            var adopt = vm.CallAsync("AdoptMoyuDownloadAsync", resourceRows[0]);
            details.Add($"State while watching  : {vm.StateName("MoyuDownloadState") ?? "(missing)"}, "
                        + $"IsMoyuWatchingDownload={vm.GetBool("IsMoyuWatchingDownload")?.ToString() ?? "(missing)"}");

            await adopt.ConfigureAwait(false);
            adoptStopwatch.Stop();

            var downloadMessage = vm.GetString("MoyuDownloadMessage");
            details.Add($"Adopt elapsed         : {adoptStopwatch.ElapsedMilliseconds} ms");
            details.Add($"MoyuDownloadState     : {vm.StateName("MoyuDownloadState") ?? "(missing)"}");
            details.Add($"MoyuDownloadMessage   : {downloadMessage ?? "(none)"}");
            details.Add($"IsMoyuWatchingDownload: {vm.GetBool("IsMoyuWatchingDownload")?.ToString() ?? "(missing)"}");
            details.Add($"Watch progress text   : {vm.GetString("MoyuWatchProgress") ?? "(cleared)"}");

            if (vm.GetBool("IsMoyuWatchingDownload") == true)
            {
                problems.Add("the watching state was never cleared after the watch ended");
            }

            if (vm.StateName("MoyuDownloadState") != "NotAdopted")
            {
                problems.Add($"a watch that saw nothing ended as {vm.StateName("MoyuDownloadState") ?? "(missing)"}, expected NotAdopted");
            }

            if (string.IsNullOrWhiteSpace(downloadMessage))
            {
                problems.Add("the timed-out watch left no message, so the user would see nothing happen");
            }
            else if (!downloadMessage.Contains("下载文件夹", StringComparison.Ordinal))
            {
                problems.Add($"the timeout message does not name the folder that was watched: \"{downloadMessage}\"");
            }

            if (vm.Get("MoyuWatchProgress") is string leftover && leftover.Length > 0)
            {
                problems.Add("the watch progress line was left behind after the watch ended");
            }

            if (adoptStopwatch.ElapsedMilliseconds > 25000)
            {
                problems.Add($"the 1 s watch took {adoptStopwatch.ElapsedMilliseconds} ms; the timeout was not honoured");
            }

            // --- 3. A row with no page URL must not claim success -----------------------------
            details.Add(string.Empty);
            details.Add("--- 3. 没有页面地址的行：必须是明说的拒绝，不是静默无操作 ---");

            var urlLess = MakeUrlLessPatchRow(vm);
            if (urlLess is null)
            {
                problems.Add("the patch row type could not be constructed for the URL-less case");
            }
            else
            {
                var row = new MoyuUiProbe(urlLess);
                var hasUrl = row.GetBool("HasWebUrl");
                var open = row.Get("OpenPageCommand") as System.Windows.Input.ICommand;

                details.Add($"url-less row HasWebUrl: {hasUrl?.ToString() ?? "(missing)"}");
                details.Add($"url-less CanExecute   : {(open?.CanExecute(null))?.ToString() ?? "(n/a)"}");

                if (hasUrl != false)
                {
                    problems.Add($"a patch with no web_url reports HasWebUrl={hasUrl?.ToString() ?? "(missing)"}");
                }

                if (open is null)
                {
                    problems.Add("the URL-less row has no OpenPageCommand at all");
                }
                else if (open.CanExecute(null))
                {
                    problems.Add("the open-page button is enabled for a row with no page URL");
                }

                // --- 4. A patch with no live resources says why -------------------------------
                details.Add(string.Empty);
                details.Add("--- 4. 资源列表为空时给出原因，而不是留空 ---");

                vm.Call("SelectMoyuPatch", urlLess);

                details.Add($"MoyuResources.Count   : {vm.Count("MoyuResources")}");
                details.Add($"HasMoyuResources      : {vm.GetBool("HasMoyuResources")?.ToString() ?? "(missing)"}");
                details.Add($"Empty reason          : {vm.GetString("MoyuResourcesEmptyReason") ?? "(none)"}");
                details.Add($"State after selecting : {vm.StateName("MoyuState") ?? "(missing)"} (the query result must not change)");

                if (vm.GetBool("HasMoyuResources") == true)
                {
                    problems.Add("selecting a resource-less patch still reports resources present");
                }

                if (string.IsNullOrWhiteSpace(vm.GetString("MoyuResourcesEmptyReason")))
                {
                    problems.Add("a resource-less patch left no explanation on the page");
                }

                if (vm.StateName("MoyuState") != "Found")
                {
                    problems.Add($"browsing a row changed the query state to {vm.StateName("MoyuState") ?? "(missing)"}; it must not");
                }
            }

            if (vm.Missing.Count > 0)
            {
                problems.Add($"{vm.Missing.Count} member(s) missing: {vm.MissingSummary()}");
            }
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
            + "timed out against an empty folder, cleared the watching state and named the folder it watched; "
            + "an URL-less row is disabled; a resource-less patch is explained rather than blank")
            .With(details.ToArray());
    }

    /// <summary>
    /// Builds a patch row for a patch page that carries no <c>web_url</c>, so the "no page to open"
    /// branch is exercised. Constructed through the row type's own constructor by name, because the
    /// harness must also compile against the revision where that type does not exist yet.
    /// </summary>
    private static object? MakeUrlLessPatchRow(MoyuUiProbe viewModel)
    {
        var rowTypeName = "Galbox.App.ViewModels.PatchMoyuRow";
        var rowType = typeof(PatchCenterViewModel).Assembly.GetType(rowTypeName);
        if (rowType is null)
        {
            return null;
        }

        var patch = new MoyuPatch
        {
            Id = "9999",
            VndbId = "v4",
            ResourceCount = 0,
            WebUrl = null
        };

        // The row takes (MoyuPatch, PatchCenterViewModel). The owner is the live ViewModel instance
        // the probe wraps, so the row's commands act on the same object the check is driving.
        var constructor = rowType.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 2);
        if (constructor is null)
        {
            return null;
        }

        return constructor.Invoke(new[] { (object?)patch, viewModel.Instance });
    }
}

/// <summary>
/// A116 - writing the key from the page.
///
/// <para>
/// The feature is unusable without a key and the key previously had no door at all: nothing in the
/// product's UI wrote to <see cref="IMoyuKeyStore"/>, so the only way to configure one was to place
/// the file by hand. This check drives the page's own save/clear commands against a throw-away store
/// and asserts the three things that matter — an obviously wrong value is refused out loud instead of
/// being stored, a real one round-trips into the client and unblocks the query, and the secret never
/// appears in any text the page displays.
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
            + "a valid key is persisted through IMoyuKeyStore, makes the client IsConfigured, unblocks a "
            + "query that was previously reported as missing a key, is never echoed back, and is removed "
            + "again by ClearMoyuKey";

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

            var vm = new MoyuUiProbe(harness.CreateViewModel());
            vm.Set("SelectedGame", MoyuUiHarness.MakeGame("v4"));

            details.Add($"Store                 : {store}");
            details.Add($"Client IsConfigured   : {harness.Api.IsConfigured}");

            if (harness.Api.IsConfigured)
            {
                return CheckResult.Fail(Id, Title, expected, "the harness client started configured")
                    .With(details.ToArray());
            }

            // --- 1. Before: the query is blocked, and says so -------------------------------
            await vm.CallAsync("FindMoyuPatchesAsync").ConfigureAwait(false);
            details.Add($"State before saving   : {vm.StateName("MoyuState") ?? "(missing)"} / {vm.GetString("MoyuStatusMessage") ?? "(missing)"}");

            if (vm.StateName("MoyuState") != "MissingKey")
            {
                problems.Add($"a key-less query reported {vm.StateName("MoyuState") ?? "(missing)"}, expected MissingKey");
            }

            // --- 2. An obviously wrong value -------------------------------------------------
            vm.Set("MoyuKeyInput", "this-is-not-a-key");
            vm.Call("SaveMoyuKey");

            details.Add(string.Empty);
            details.Add($"Rejected input message: {vm.GetString("MoyuKeySaveMessage") ?? "(none)"}");
            details.Add($"Store configured now  : {store.IsConfigured} (file exists: {File.Exists(storePath)})");

            if (store.IsConfigured || File.Exists(storePath))
            {
                problems.Add("a value that is not an nmk_ key was written to the store");
            }

            var rejectMessage = vm.GetString("MoyuKeySaveMessage") ?? string.Empty;
            if (!rejectMessage.Contains("没有保存", StringComparison.Ordinal))
            {
                problems.Add($"the refused value produced no clear refusal: \"{rejectMessage}\"");
            }

            // --- 3. A valid key ---------------------------------------------------------------
            vm.Set("MoyuKeyInput", ProbeKey);
            vm.Call("SaveMoyuKey");

            details.Add(string.Empty);
            details.Add($"Save message          : {vm.GetString("MoyuKeySaveMessage") ?? "(none)"}");
            details.Add($"Store configured now  : {store.IsConfigured}");
            details.Add($"Client IsConfigured   : {harness.Api.IsConfigured}");
            details.Add($"Key summary           : {vm.GetString("MoyuKeySummary") ?? "(none)"}");
            details.Add($"Input box cleared     : {string.IsNullOrEmpty(vm.GetString("MoyuKeyInput"))}");

            if (!store.IsConfigured)
            {
                problems.Add("a valid nmk_ key was not persisted");
            }

            if (!harness.Api.IsConfigured)
            {
                problems.Add("the client still reports unconfigured after the key was saved");
            }

            if (!string.IsNullOrEmpty(vm.GetString("MoyuKeyInput")))
            {
                problems.Add("the key was left in the input property after it was saved");
            }

            if (vm.StateName("MoyuState") == "MissingKey")
            {
                problems.Add("the card still claims a key is missing after one was saved");
            }

            // Nothing the page displays may echo the secret back.
            var surfaces = new[]
            {
                "MoyuKeySaveMessage", "MoyuKeySummary", "MoyuKeyHowTo", "MoyuStatusMessage",
                "MoyuAdvisoryMessage", "MoyuErrorDetail", "MoyuLaunchMessage", "MoyuDownloadMessage"
            };

            var leaked = surfaces
                .Select(name => (Name: name, Value: vm.GetString(name)))
                .Where(pair => pair.Value is not null && pair.Value.Contains(ProbeKey, StringComparison.Ordinal))
                .ToArray();

            details.Add($"Key present in page text: {(leaked.Length == 0 ? "no" : "YES — " + string.Join(", ", leaked.Select(l => l.Name)))}");

            if (leaked.Length > 0)
            {
                problems.Add($"the API key appears in page text: {string.Join(", ", leaked.Select(l => l.Name))}");
            }

            if (vm.GetString("MoyuKeySaveMessage")?.Contains("指纹", StringComparison.Ordinal) != true)
            {
                problems.Add("the save confirmation does not show a fingerprint, so the user cannot tell which key is stored");
            }

            // --- 4. The query now runs --------------------------------------------------------
            var beforeQuery = harness.Transport.Everything.Count;
            await vm.CallAsync("FindMoyuPatchesAsync").ConfigureAwait(false);
            var afterQuery = harness.Transport.Everything.Count;

            details.Add(string.Empty);
            details.Add($"State after saving    : {vm.StateName("MoyuState") ?? "(missing)"} / {vm.GetString("MoyuStatusMessage") ?? "(missing)"}");
            details.Add($"Requests sent         : {afterQuery - beforeQuery} (total this harness: {afterQuery})");

            if (vm.StateName("MoyuState") != "Empty")
            {
                problems.Add($"after configuring a key the query reported {vm.StateName("MoyuState") ?? "(missing)"}, expected Empty (the stub answers with no rows)");
            }

            if (afterQuery - beforeQuery != 1)
            {
                problems.Add($"the query after configuration sent {afterQuery - beforeQuery} request(s), expected 1");
            }

            // --- 5. Clearing ------------------------------------------------------------------
            vm.Call("ClearMoyuKey");

            details.Add(string.Empty);
            details.Add($"Clear message         : {vm.GetString("MoyuKeySaveMessage") ?? "(none)"}");
            details.Add($"Store configured now  : {store.IsConfigured}");
            details.Add($"Client IsConfigured   : {harness.Api.IsConfigured}");
            details.Add($"State after clearing  : {vm.StateName("MoyuState") ?? "(missing)"} / {vm.GetString("MoyuStatusMessage") ?? "(missing)"}");

            if (store.IsConfigured)
            {
                problems.Add("Clear did not remove the stored key");
            }

            if (vm.StateName("MoyuState") != "MissingKey")
            {
                problems.Add($"after clearing the key the card reports {vm.StateName("MoyuState") ?? "(missing)"}, expected MissingKey");
            }

            if (vm.Missing.Count > 0)
            {
                problems.Add($"{vm.Missing.Count} member(s) missing: {vm.MissingSummary()}");
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
            + "appeared only as a fingerprint, and Clear removed it and restored the MissingKey state")
            .With(details.ToArray());
    }
}
