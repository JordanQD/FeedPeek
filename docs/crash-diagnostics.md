# 随机退出诊断

Debug 和 Release 都需要精简、可留存的诊断信息。详细的托盘调试输出仍只在 Debug 编译中启用；日常使用的 Release 依靠运行日志和 Windows 崩溃转储。

## 应用运行日志

日志目录为 `ApplicationData.Current.LocalFolder.Path\FeedPeek\Diagnostics`。打包应用的实际位置取决于安装包身份，通常位于 `%LOCALAPPDATA%\Packages\<包族名>\LocalState` 下；应以当前安装包的本地数据目录为准。

- 每个进程写入 `runtime-<进程 ID>.log`，包含启动、主动退出、未处理异常及面板生命周期等关键记录。
- 单文件约 1 MB 后轮转为一个 previous 文件，保留最近 10 个进程的日志组，避免日志随驻留时间无限增长。
- 不接入插件业务日志，不逐条记录订阅内容或网络请求。异常文本仍可能包含上下文，分享前应检查内容。
- 日志记录不能让损坏进程继续运行；未处理异常保持原来的终止行为。

访问冲突、堆内存损坏等原生故障可能直接终止进程，C# 的未处理异常事件不保证有机会执行。日志没有异常结尾不能说明是正常退出，必须结合 Windows 事件和转储。

## 开启完整崩溃转储

使用同一 Windows 账户打开**管理员权限的 64 位 PowerShell**，在项目根目录运行：

```powershell
.\scripts\Enable-CrashDumps.ps1
```

脚本只设置 `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\FeedPeek.exe`，开启完整转储（`DumpType=2`），最多保留 3 份（`DumpCount=3`）。默认输出目录为执行脚本账户的 `%LOCALAPPDATA%\CrashDumps`，控制台会显示展开后的实际位置。可以通过 `-DumpFolder` 指定当前应用账户可写入的目录。

首次开启前会将原来的 FeedPeek 专用配置备份到 `%LOCALAPPDATA%\FeedPeek\CrashDumpConfiguration`。重复开启保留第一次的备份。脚本不改变 Windows 的全局 LocalDumps 设置，不启动应用、不安装调试器。

配置完成后正常使用应用；下次崩溃时，Windows 在本地保存 `FeedPeek.exe.<进程 ID>.dmp`。完整转储可能较大；最多 3 份的限制是文件数量限制，不是磁盘容量上限。Windows 的自定义崩溃处理器或自动调试设置可能影响 WER 抓取，最终应以实际生成的文件为准。

关闭此次配置并恢复开启前的状态：

```powershell
.\scripts\Disable-CrashDumps.ps1
```

使用开启时的同一账户；如果传过 `-StateDirectory`，关闭时也传相同路径。开启前不存在专用键时，关闭脚本会删除该专用键，应用重新使用 Windows 原有的全局配置；已有转储文件不会删除。

## 再次退出后保留什么

保留崩溃时间、对应 `.dmp`、运行日志，以及**当时实际运行的构建和匹配的 PDB**。不要先覆盖构建再收集符号，源码和依赖版本必须与转储中的构建区分。

完整转储包含更多堆内存，但不保证追溯到此前破坏内存的那次写入。若仍只能定位到延迟检测点，再考虑针对 FeedPeek 开启 PageHeap 或 Application Verifier；它们有额外内存和运行开销，当前脚本不会开启这些选项。

转储可能包含登录凭据和进程中的其他私人数据，应留在本地分析或通过可信渠道分享，不要提交到公开仓库、公开问题单或公开网盘。

微软说明：[收集用户模式转储](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps)。
