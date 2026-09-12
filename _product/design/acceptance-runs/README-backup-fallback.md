================================================================================
备份来源回退（AlternativePaths）：A80 先失败后通过 + A100 回归检查
================================================================================
记录时间：2026-09-12
分支    ：fix/save-backup-from-alternative-paths（基于 release/1.0.0，含离屏修复）
缺陷    ：存档管理页点「创建备份」→「创建备份失败。未检测到存档文件。」
          而同一份检测结果里明明有 12 个存档。

--------------------------------------------------------------------------------
1. 原始命令
--------------------------------------------------------------------------------
dotnet build Galbox.sln -c Debug
tests\Galbox.Acceptance\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Galbox.Acceptance.exe --timeout 600

--------------------------------------------------------------------------------
2. 先失败（只合并了 A80 检查代码，产品代码尚未修）
--------------------------------------------------------------------------------
文件：17-backup-fallback-a80-before-fix-42of44-A80-FAIL.txt

 A80  FAIL       6063 ms  Full chain on the real game: ...
 PASSED : 42   FAILED : 2   TOTAL : 44   EXIT CODE: 1

 A80 的失败点（原始输出）：
   === A80.6 back the save folder up ===
     CreateBackupAsync    : null (no archive was produced)
     DetectSaveLocation   : Success=True, PrimarySavePath=(null), AlternativePaths=1, SaveFiles=12
     What the user sees   : the Save Manager page reports "创建备份失败。未检测到存档文件。"
                            although A80.3 just found 12 save files in D:\GAME\Dreamin'_Her\game\saves.
   CHAIN BROKEN AT A80.6: CreateBackupAsync produced no archive. It requires
     SaveLocationResult.PrimarySavePath, and the detector reports the real save folder only under
     AlternativePaths (PrimarySavePath=(null), AlternativePaths=[D:\GAME\Dreamin'_Her\game\saves])
   steps completed before the break: A80.1 -> A80.2 -> A80.3 -> A80.4 -> A80.5

 第二处失败 A18 是**本次运行自身造成的**，不是产品缺陷：跑之前/跑的过程中我在改
 src/Galbox.App 的源码并重新构建，A18 的断言是「二进制必须比 src 下最新源文件新」，
 于是报 "binary built 03:09:04Z but sources changed 03:09:48Z"。修完后重跑的两次都没有这个问题。

--------------------------------------------------------------------------------
3. 后通过（同一套检查，只改了产品代码）
--------------------------------------------------------------------------------
文件：18-backup-fallback-a80-after-fix-44of44.txt

 A80  PASS       5935 ms  Full chain on the real game: ...
 PASSED : 44   FAILED : 0   TOTAL : 44   EXIT CODE: 0

 A80.6 的原始输出：
     backup row Id        : 8
     file on disk         : exists=True, 1985012 bytes, row SizeBytes=1985012
     OriginalSavePath     : D:\GAME\Dreamin'_Her\game\saves
     archive entries      : 13 [1-1-LT1.save, ..., persistent]
 A80.7：
     Chain completed       : A80.1 -> A80.2 -> A80.3 -> A80.4 -> A80.5 -> A80.6 -> A80.7
     Restore               : 13 files byte-identical after restoring over a mutated/deleted folder

 A80 的断言一条都没有放宽、没有注释、没有写死返回值。

--------------------------------------------------------------------------------
4. 最终全量（43 原有 + A80 + 新增回归 A100 = 45 项）
--------------------------------------------------------------------------------
文件：19-backup-fallback-final-45of45-with-A100.txt

 A50  PASS     252526 ms  （见下方「注意：机器级干扰」）
 A80  PASS       7569 ms
 A100 PASS        108 ms
 PASSED : 45   FAILED : 0   TOTAL : 45   EXIT CODE: 0

 注意：机器级干扰。A50 平时 12~15 秒，这一次跑了 252 秒且仍为 PASS。本仓库已记录的
 已知现象（见 16-known-flakiness-gui-checks.txt）是多条工作线并行时 GUI 类检查会被别的
 进程影响；判断标准是退出码：0xC000027B 才是崩溃，0 是被外部正常关掉。

--------------------------------------------------------------------------------
5. 修法
--------------------------------------------------------------------------------
只改「备份服务与检测器不一致」这一件事，EngineSaveDetector 的 Primary/Alternative
划分一个字节都没动。

- SaveManagementService.ResolveBackupSourceRoot：PrimarySavePath 非空且安全时行为不变；
  为空时才依次考察 AlternativePaths，并且只接受**被证明真的含存档**的目录：
    (1) 目录存在；
    (2) 不是游戏安装目录，也不是包含安装目录的上级目录
        （EngineSaveDetector.IsGameInstallRoot + 本服务自己的 IsPathInside）；
    (3) 检测器自己在 SaveFiles 里记录过至少一个位于该目录下的文件；
    (4) 其中至少一个文件在备份时仍在磁盘上。
  任一条件不满足即跳过并记录原因，绝不盲取列表第一项。
- 实际采用的目录写入 GameSaveBackup.OriginalSavePath（该列同时是恢复目标）。
- ISaveManagementService.LastBackupFailureReason：失败原因准确回传界面。
- 界面：存档管理页与游戏详情页的备份列表都显示「来源文件夹」，成功提示也带上实际来源。

--------------------------------------------------------------------------------
6. 仍然不工作的情况（如实列出）
--------------------------------------------------------------------------------
1. 存档只在安装目录根下 → 检测器拒绝（A13/A100.5 实测），备份仍然失败；现在失败原因是
   检测器的原话（点名那个目录），不再是「未检测到存档文件」。
2. 检测成功但没有任何备用目录「被检测器记录过存档文件」→ 拒绝备份并说明原因。此时用户
   需要手动指定存档目录（产品尚未提供该入口）。
3. 多根目录游戏（例如正统 Ren'Py 用户目录 + <install>\game\saves 同时存在）：
   归档仍会包含所有根的存档（既有行为，本次未改，因为改成只打包一个根会造成备份缺文件的
   回退），而恢复只写回 OriginalSavePath 指向的那一个目录。这是既有缝，需要产品决定。
4. 本机 Ren'Py 的正统用户存档目录（%APPDATA%\RenPy\<config.save_directory>）检测器不识别
   （它只找 %APPDATA%\<游戏名>*）——这是已记录的产品决策/已知问题，本次刻意不动。
================================================================================
