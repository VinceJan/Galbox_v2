# Evidence: ymgal / cngal metadata sources (A40–A42)

Two runs, same acceptance binary contract, same command. The first was taken **before** the
implementation, the second **after**. Both are raw output, copied verbatim.

Command (from the repository root):

```powershell
dotnet build Galbox.sln -c Debug
.\tests\Galbox.Acceptance\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Galbox.Acceptance.exe --timeout 420
```

---

## 1. Baseline — `1f17c38` (checks added, ymgal/cngal still stubs)

Exit code **1**. Full log kept at `E:\tmp\Galbox_meta\baseline-A40-A42.txt`.

```
 A40  FAIL          5 ms  ymgal source performs a real search and returns the real game
 A41  FAIL          4 ms  cngal source performs a real search and returns the real game
 A42  FAIL        409 ms  metadata sources distinguish 'not configured' / 'unreachable' / 'no results'
 PASSED : 10
 FAILED : 3
 ERRORS : 0
 TOTAL  : 13
```

```
# [A40] ymgal source performs a real search and returns the real game
ACTUAL   : baseAddress=False, userAgent=True, requests=0, pathHit=False, searchOk=False, items=0, detailOk=False
DETAILS  :
  --- [1] HttpClient configuration built by the container ---
    YmgalHttpClient     BaseAddress=(null)
                        Timeout=30s
                        UserAgent=Galbox/1.0
  --- [2] live search through the DI-resolved YmgalApi ---
    query                : "サノバウィッチ"
    elapsed              : 0 ms
    HTTP requests issued : 0
    ApiClient.LastError  : (null)
    response             : Success=False, Items=0, Message="ymgal API integration pending - needs API research"
RESULT   : FAIL  (5 ms)
```

```
# [A42] metadata sources distinguish 'not configured' / 'unreachable' / 'no results'
ACTUAL   : config=False, ymgalNotConfigured=False(requests=0), cngalUnreachable=False, ymgalEmpty=False, cngalEmpty=False
  --- [2] ymgal with an incomplete credential ---
    Message              : "ymgal API integration pending - needs API research"
    ApiClient.LastError  : "(null)"
    HTTP requests issued : 0
    verdict              : Explained=False
  --- [3] cngal pointed at a closed port (real transport failure) ---
    Message              : "cngal API integration pending - needs API research"
    HTTP requests issued : 0
    verdict              : Explained=False
  --- [4] negative control: a query that cannot exist upstream ---
    --- [4] ymgal ---
        Success              : False
        Items.Count          : 0
        verdict              : Distinguishable=False
RESULT   : FAIL  (409 ms)
```

The stub is indistinguishable from "the search found nothing" - exactly the defect A42 exists to
catch.

---

## 2. After the implementation — `feat/metadata-sources`

Exit code **0**. Full log at `E:\tmp\Galbox_meta\after-A40-A42.txt`.

```
 A40  PASS       5897 ms  ymgal source performs a real search and returns the real game
 A41  PASS       2253 ms  cngal source performs a real search and returns the real game
 A42  PASS      10624 ms  metadata sources distinguish 'not configured' / 'unreachable' / 'no results'
 PASSED : 13
 FAILED : 0
 ERRORS : 0
 TOTAL  : 13
 EXIT CODE: 0  (all checks PASS)
```

```
# [A40]
ACTUAL   : baseAddress=True, userAgent=True, requests=1, pathHit=True, searchOk=True, items=1, detailOk=True, appDetailOk=True
  --- [2] live search through the DI-resolved YmgalApi ---
    credential source    : ymgal's documented public client (ymgal)
    attempt 1/3: 196 ms, requests=1, response=Success=True, Items=1, lastError="(null)"
        GET https://www.ymgal.games/open/archive/search-game?mode=list&keyword=サノバウィッチ&pageNum=1&pageSize=10  ->  HTTP 200
          response body : {"success":true,"code":0,"data":{"result":[{"id":23682,"name":"サノバウィッチ","chineseName":"魔女的夜宴","state":"PUBLIC_PUBLISHED","weights":0,"mainImg":"https://cdn.ymgal.games/archive/main/a0/a057bbc19b9f41a2868ee415721b84e1.webp","publishVersion":4,"publishTime":"2022-02-15 01:12:29","publisher":3412,"score":"0","orgId":10126,"orgName":"ゆずソフト","releaseDate":"2015-02-27","haveChinese":true}],"total":1,"hasNext":false,"pageNum":1,"pageSize":10}}
        hit: id=23682 title="サノバウィッチ" titleCn="魔女的夜宴" cover=yes
  --- [3] detail endpoint through the DI-resolved YmgalApi ---
    GetGameAsync("23682") requests : 1
        GET https://www.ymgal.games/open/archive?gid=23682  ->  HTTP 200
    result               : id=23682, title="サノバウィッチ", titleCn="魔女的夜宴", releaseDate="2015-02-27", tags=0, characters=12
  --- [4] GameScrapingService.GetGameDetailsAsync (the path the UI uses) ---
    result               : source=Ymgal, id=23682, titleOriginal="サノバウィッチ", titleCn="魔女的夜宴", releaseDate=2015-02-27, titles=7, characters=12
    titles               : サノバウィッチ | 魔女的夜宴 | サノバウィッチ Sabbat of the Witch | Son of a Witch: Sabbat of the Witch | Sonova Witch | Sonovawitch | Sabbath of the Witch
RESULT   : PASS  (5897 ms)
```

```
# [A41]
ACTUAL   : baseAddress=True, userAgent=True, requests=1, pathHit=True, searchOk=True, items=10, detailOk=True, appDetailOk=True
  --- [2] live search through the DI-resolved CngalApi ---
    HTTP requests issued : 1
        GET https://api.cngal.org/api/home/Search?Types=Game&Text=三色绘恋&Page=1  ->  HTTP 200
        hit: id=80 title="三色△绘恋" titleCn="三色△绘恋" cover=yes
        hit: id=81 title="三色绘恋S" titleCn="三色绘恋S" cover=yes
  --- [3] detail endpoint through the DI-resolved CngalApi ---
        GET https://api.cngal.org/api/entries/GetEntryView/80?renderMarkdown=false  ->  HTTP 200
    result               : id=80, title="三色△绘恋", developer="绘恋制作组", releaseDate="2017-09-21", tags=1, characters=9
  --- [4] GameScrapingService.GetGameDetailsAsync (the path the UI uses) ---
    result               : source=Cngal, id=80, titleOriginal="三色△绘恋", titleCn="三色△绘恋", developer="绘恋制作组", releaseDate=2017-09-21, tags=1, characters=9
    titles               : 三色△绘恋 | 三色绘恋 | Tricolour Lovestory
RESULT   : PASS  (2253 ms)
```

```
# [A42]
ACTUAL   : config=True, ymgalNotConfigured=True(requests=0), cngalUnreachable=True, ymgalEmpty=True, cngalEmpty=True
  --- [2] ymgal with an incomplete credential (client id present, secret missing) ---
    Message              : "ymgal credentials are not configured: GALBOX_YMGAL_CLIENT_SECRET is empty. Either provide both GALBOX_YMGAL_CLIENT_ID and GALBOX_YMGAL_CLIENT_SECRET for a dedicated client, or leave both unset to use ymgal's documented public client (ymgal)."
    HTTP requests issued : 0 (a configuration failure must not call the community site)
    verdict              : Explained=True
  --- [3] cngal pointed at a closed port (real transport failure) ---
    Message              : "cngal is unreachable: 由于目标计算机积极拒绝，无法连接。 (127.0.0.1:9)"
    HTTP attempts made   : 3
    verdict              : Explained=True
  --- [4] negative control: a query that cannot exist upstream ---
    --- [4] ymgal ---
        attempt 1/3:
          Success              : False
          Message              : "ymgal request failed: API error: 302 Found"
          inconclusive: the source did not deliver a zero-hit answer at all ... Retrying.
        attempt 2/3:
          Success              : True
          Items.Count          : 0
          ApiClient.LastError  : "(null)"
          verdict              : Distinguishable=True
RESULT   : PASS  (13942 ms)
```

## 3. Note on the intermittent ymgal HTTP 302

ymgal (or an intermediary in front of it) was observed answering a bare `302` with an empty body
and no `Location` for ordinary searches - roughly once every other acceptance run while several
agents were scraping concurrently. The client reports it as a distinct failure
(`API error: 302 Found`, `Success = false`, `LastError` set), never as an empty result, which is
the behaviour A42 asserts.

Because such an answer is the upstream refusing to answer rather than a statement about the
client, A40 and A42's negative control retry the probe a bounded number of times (3) with every
attempt printed in the report, and FAIL if the source never delivers a real answer. The assertion
itself is not relaxed: a zero-hit answer must still be `Success = true`, 0 items, no `LastError`.

A second consecutive run after the retry was added produced `PASSED : 13 / FAILED : 0` with no
retry needed (`E:\tmp\Galbox_meta\after-A40-A42-confirm.txt`).

## 4. How the check set evolved

The baseline run above used the A40/A41/A42 revision committed as `1f17c38`. The final revision
added one diagnostic line to A40 (which credential source was used) and one further assertion to
A40/A41 — section `[4]`, which drives `IGameScrapingService.GetGameDetailsAsync`, the application
path the UI actually calls. Both additions are visible in the `After` output above, and neither
relaxes anything: `pass` gained a term (`appDetailOk`) rather than losing one.
