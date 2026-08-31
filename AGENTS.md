# FeedPeek — WinUI 3 项目速览

## 项目概述

FeedPeek 是一个 Windows 桌面通用更新查看器（WinUI 3 + Windows App SDK），通过 RSS 与网站插件提供更新、历史记录、稍后再看、作者和直播内容。应用驻留系统托盘，点击弹出 Flyout 面板。

**技术栈**：C# / WinUI 3 / DesktopFlyouts.WinUI 1.4.0 / CommunityToolkit 8.2 / .NET 9

## 架构总览

项目已从旧 `MainWindow.xaml.cs` 单体窗口迁移到 DesktopFlyouts `SystemTrayIcon` + `DesktopFlyout` + 页面化架构。Flyout 内容关闭后重建，三页纵向视频列表使用虚拟化 `ListView`，关注页直播横向区域使用 `ItemsRepeater`。

```
App.xaml.cs                    ← 应用入口，初始化托盘、数据监控
├── TrayFlyoutService          ← DesktopFlyouts SystemTrayIcon + DesktopFlyout/DesktopMenuFlyout
│   └── MainPanelControl       ← Flyout 内容（420px 宽面板）
│       ├── SelectorBar        ← 关注/历史/稍后再看 三页切换
│       ├── Frame              ← 承载三个 Page（CacheSize=0, 切页释放旧页）
│       ├── ProgressBar        ← 顶部刷新进度条
│       └── ItemsControl       ← 底部 StatusNotification InfoBar 列表
├── BackgroundNotificationMonitor ← 后台轮询推送通知
└── SettingsWindow             ← 独立设置窗口（常规/通知/关于 三个页面）
```

### 页面结构

```
Pages/
├── FollowingPage       ← 直播 ItemsRepeater + 最新视频 ListView
├── HistoryPage         ← 历史记录 ListView + 空状态
├── ViewLaterPage       ← 稍后再看 ListView + 移除按钮 + 空状态
├── GeneralSettingsPage ← 通用设置（语言、开机自启等）
├── SourcesSettingsPage ← RSS 来源、网站插件、登录与来源配置
├── NotificationSettingsPage ← 通知设置
└── AboutSettingsPage   ← 关于页面

Controls/
└── MainPanelControl ← Flyout 外壳：SelectorBar + Frame + ProgressBar + 通知区

Models/
├── FeedItemRow / FeedLiveItemRow ← 通用条目与直播 UI 行
├── FeedSourceRow / PluginSettingsRow ← 来源与插件设置行
├── NotificationCreatorSubscription ← 通用作者通知规则
├── MainPanelSection    ← enum { Following, History, ViewLater }
└── StatusNotification  ← InfoBar 消息
```

### 服务层

```
Services/
├── FeedPeekPanelSession ← **核心**：组合 Core、RSS 与动态插件 Provider，服务三个页面
├── FeedPeekPluginRuntime ← manifest、凭据、插件调用与动态 Provider 注册
├── PluginInstallationService ← 用户插件安装、哈希、启停、升级与卸载
├── FeedPeekNotificationSession / NotificationService ← 通用更新与直播通知
├── AppSettings          ← ApplicationData.LocalSettings 持久化
├── CookieStore          ← 旧登录状态的一次性兼容迁移
└── TrayFlyoutService    ← DesktopFlyouts 托盘图标、主面板和右键菜单
```

## 关键设计决策

1. **纵向更新列表使用 ListView** — 关注、历史、稍后再看三页由通用 `FeedItemRow` 驱动虚拟化 `ListView`，卡片模板直接定义在页面资源中。
2. **直播横向区域使用 ItemsRepeater** — 关注页直播 UP 主区域使用官方 `ItemsRepeater + StackLayout(Orientation=Horizontal)`，外层仍由横向 `ScrollViewer` 承载。
3. **页面不缓存 UI 树** — `Frame.CacheSize=0`，三个 Page 不使用 `NavigationCacheMode="Required"`。切页前主动 Dispose 当前页，避免已访问页面的 UI 内存叠加。
4. **数据由 Core 持久化** — 页面 UI 可销毁，`FeedPeekPanelSession` 从 SQLite 和动态 Provider 恢复数据；不再保留旧窗口快照或站点专属 UI 模型。
5. **插件能力驱动行为** — 来源、作者、直播、稍后再看和通知动作按 provider/source/item 标识及 manifest capability 分发，不按内置网站 ID 判断。
6. **插件与来源分离** — FeedPeek 本体不随包发布任何视频网站插件。哔哩哔哩与其他网站一样，必须由用户从文件夹手动安装、登录并点击“添加来源”；安装、启用或登录都不得自动创建 Source/Subscription。所有网站插件均可停用和卸载。
7. **切页后延迟资源回收** — 页面切换后延迟、低优先级执行 GC + working set trim，用于提前触发 WinUI/图片资源释放后的工作集回落。
8. **Flyout 内容按需创建** — `MainPanelControl` 在托盘左键打开时创建，关闭后 Dispose 面板与会话并低优先级修剪 working set；下次从持久层重建。
9. **右键菜单不提供“打开”** — 左键托盘图标负责打开/关闭主 Flyout；右键菜单只保留“设置”和“退出”。不要重新加入右键“打开”，之前尝试在 MenuFlyout 命令中主动 `ShowAt(...)` 会造成状态重入和卡死风险。
10. **无旧宿主窗口** — DesktopFlyouts 使用独立 XAML island 窗口；应用在 `OnLaunched` 中将 `DispatcherShutdownMode` 设为 `OnExplicitShutdown`，关闭最后一个设置/登录窗口后仍由托盘驻留，只有显式退出才停止 UI 消息循环。不要把设置移到 `App` 构造函数，`Application.Start` 会在随后覆盖它。不再创建透明锚点或隐藏 `TrayHostWindow`。

## 构建

```bash
dotnet build src/FeedPeek.App/FeedPeek.App.csproj -p:Platform=x64
```

Debug 配置：框架依赖（`WinUISDKReferences=true`）；Release：自包含 MSIX 包。

## Subagent 自动委派

1. 对能够拆成独立、边界清晰子任务的工作，主代理应主动委派给 subagent，不需要等待用户再次明确要求。
2. 优先委派代码检索、影响面梳理、文档核对、测试/日志分析和独立审查等读多写少的任务；可并行的子任务应并行执行。
3. 简单的一步任务、无法独立推进的任务，以及可能与主代理或其他 subagent 修改同一文件的写密集型任务，不要为了使用 subagent 而强行委派。
4. 主代理负责划定每个子任务的范围、汇总结果、解决冲突并完成最终验证；不要把整项任务原样转交给 subagent。
5. 项目级 `.codex/config.toml` 已将默认 subagent 配置为 `gpt-5.6-luna`、`high` 推理强度；除非用户明确指定其他模型或任务确有不同需求，否则不要在派生时覆盖它。

### Codex 环境中的 NuGet 还原

本机普通终端或 Visual Studio 能访问 NuGet，但 Codex 受限沙箱内的 Windows Schannel 可能报以下错误：

- `SEC_E_NO_CREDENTIALS (0x8009030e)` / “安全包中没有可用的凭据”
- `NU1301`，内层错误为 “The SSL connection could not be established”

这表示沙箱进程无法取得 TLS credential handle，不是 NuGet 用户名/密码错误，也不是项目包源配置错误。不要改用非 HTTPS 包源，也不要设置或保留 `disableTLSCertificateValidation="true"` 绕过证书校验。

正确做法是在沙箱外运行同一条还原命令；Codex 工具调用应使用 `sandbox_permissions: require_escalated`：

```powershell
dotnet restore src/FeedPeek.App/FeedPeek.App.csproj -p:Platform=x64
```

下载期间偶发 `unexpected EOF or 0 bytes` 通常是代理或 CDN 瞬时断流，先让 NuGet 自己重试。若持续发生，再降低并发：

```powershell
dotnet restore src/FeedPeek.App/FeedPeek.App.csproj -p:Platform=x64 --disable-parallel
```

还原成功后，编译时避免再次触发网络请求：

```powershell
dotnet build src/FeedPeek.App/FeedPeek.App.csproj -p:Platform=x64 --no-restore
```

项目根目录的 `NuGet.Config` 是本项目包源配置的唯一依据，当前通过 `<clear />` 后只启用官方 `https://api.nuget.org/v3/index.json`。若沙箱内失败而沙箱外访问该地址返回 HTTP 200，应直接按上述方式在沙箱外还原，不要继续修改项目配置。

## Windows App SDK 升级准入

不要只根据“能编译”决定是否升级。候选版本必须是 Stable 通道，并按以下顺序评测：

1. **依赖审计** — 用 `dotnet list src/FeedPeek.App/FeedPeek.App.csproj package --outdated` 确认候选版本；检查直接和传递依赖是否出现降级、冲突、弃用或安全警告，并确认 DesktopFlyouts 版本支持该 Windows App SDK。
2. **构建与打包矩阵** — 验证 Debug x64 框架依赖构建和 Release x64 自包含 MSIX。项目当前为 x64-only，不再构建或发布 ARM64。还原成功后构建统一使用 `--no-restore`。
3. **启动与生命周期** — 安装/注册并真实启动 MSIX，验证二次启动的单实例重定向、开机启动、通知激活、设置窗口和 WebView2 登录链路，不能只检查进程存在。
4. **托盘关键路径** — 验证托盘左键 Flyout、右键仅“设置/退出”、Explorer 重启后的图标恢复、切换显示器/DPI 后图标清晰，以及连续开关 Flyout 不出现重入或卡死。
5. **WinUI 关键路径** — 验证三页切换、`ListView`/`ItemsRepeater` 虚拟化与滚动加载、稍后再看移除、关注/取消关注、直播卡片操作；刷新或加载中关闭 Flyout 时，取消请求不能产生错误 InfoBar。
6. **升级门槛** — 构建/打包零错误，关键路径无功能回归，事件日志无新的应用崩溃。性能数字不作为当前托盘迁移门槛；若出现肉眼可见卡顿、泄漏或稳定性问题再单独处理。

评测时应单独建分支，只修改 Windows App SDK 版本，先验证默认行为；不要一开始同时启用新的 XAML 可选行为或 `XamlChangeId`，这些应作为第二阶段独立基准测试。

## 当前迁移状态

### FeedPeek RSS / 插件迁移

| 阶段 | 状态 |
|------|------|
| Phase 0（行为基线） | ✅ |
| Phase 1（通用项目骨架） | ✅ |
| Phase 2（通用核心与 RSS） | ✅ 已人工验收主路径 |
| Phase 3（插件合同与 Jint Runtime） | ✅ 18 项必要验证通过，Release x64 构建通过 |
| Phase 4（站点插件迁移） | 🧪 4.0–4.9 自动验证通过；4.3–4.4 已人工验收，4.5–4.9 待人工验收 |
| Phase 5（品牌、独立包身份与数据迁移） | 🚧 5.0–5.3 已完成；5.4 待完成 |

Phase 3 生产项目为 `src/FeedPeek.PluginContracts` 与 `src/FeedPeek.PluginRuntime`。Runtime 锁定 Jint 4.16.1；同一插件 realm 串行调用，通过 JSON `IPluginTransport` 保持 transport-neutral。插件只能使用显式 Host Bridge，不开放 CLR、文件系统、进程、注册表、COM 或 WinUI。mock JS fixture 和必要验证复用 `tests/FeedPeek.Core.Tests`，不再新增测试项目。

Phase 5 采用独立应用发布路径，不把 FeedPeek 作为旧 BiliRadar identity 的原位升级。开发包使用 `JordanQD.FeedPeek.Preview`，正式包通过 `UseProductionPackageIdentity=true` 选择独立 production identity；Microsoft Store 发布时必须使用 Partner Center 分配的精确 identity。用户可见品牌已统一为 FeedPeek，LocalSettings 已增加版本化幂等迁移。启动项目现为 `src/FeedPeek.App/FeedPeek.App.csproj`，程序集、可执行文件和默认命名空间均已改为 FeedPeek；正式 identity 和人工新装验收仍待完成，详见 `docs/design/rss-plugin-phase5-plan.md`。

Phase 4 详细检查点见 `docs/design/rss-plugin-phase4-plan.md`。4.1–4.8 已完成通用 Host、仓库维护的网站 JS 插件、三页数据、登录、作者/直播、通知和用户插件管理。4.9 已删除 legacy provider、旧模型、旧 adapter、旧快照和孤立卡片；视频网站插件不再随应用发布，停用插件不会继续刷新旧订阅，通知动作按 manifest capability 显示，插件升级/卸载使用可回滚目录切换。仅保留旧 Cookie 与通知规则的一次性兼容迁移。

### DesktopFlyouts 迁移

| 阶段 | 状态 |
|------|------|
| Phase 0 (分支与构建基线) | ✅ |
| Phase 1 (26100 TFM、依赖和 x64 打包准入) | ✅ |
| Phase 2 (SystemTrayIcon + DesktopFlyout + DesktopMenuFlyout) | ✅ 已人工验收 |
| Phase 3 (旧路径清理与 WinUIEx 依赖移除) | ✅ |
| Phase 4 (状态机加固与交付收口) | ✅ |
| Phase 5 (Shell 环境适配核查) | ✅ |
| Phase 6 (移除 WinUIEx 和旧宿主) | 🧪 自动验证通过，待人工生命周期/交互验收 |

### 旧 WinUIEx 架构迁移（历史）

| 阶段 | 状态 |
|------|------|
| Phase 1 (WinUIEx 原型) | ✅ |
| Phase 2a-2f (页面提取) | ✅ |
| Phase 2g (Flyout 集成) | ✅ 已手动验证 |
| Phase 3 (右键菜单迁移) | ✅ 新路径原生 MenuFlyout，仅保留设置/退出 |
| Phase 4 (清理旧代码) | ✅ 删除 MainWindow、TrayIconService、SystemTray/，#if 双路径已移除 |
| Phase 5 (内存测试) | ✅ 已接入关闭后重建面板、切页 Dispose 和延迟 working set trim；仍建议记录最终数值 |
| Phase 6 (ListView / ItemsRepeater 迁移) | ✅ 三个纵向列表已改 ListView，直播横向区已改 ItemsRepeater |

DesktopFlyouts 迁移详见 `docs/design/desktopflyouts-migration.md`；旧架构记录见 `docs/design/tray-flyout-migration.md`。

## WinUI 控件选型约束

1. **优先复用，不手搓替代品** — 只要 WinUI、Windows App SDK、WinUI Gallery、Windows Community Toolkit 或微软官方推荐库已有适合的控件/模式，就不要先实现自定义控件、模板或交互替代品。
2. **先检索官方来源** — 新增或重做 UI 前，先检索本机 `C:\Users\Q\Data\Projects\WinUI-Gallery`、`C:\Users\Q\Data\Projects\Windows` 以及可用的官方示例索引；记录找到的候选控件、来源和适用限制。
3. **采用前先通知用户** — 找到合适的官方或官方推荐控件后，先向用户说明候选、收益、依赖和迁移影响，由用户决定是否采用。未经用户决定，不要擅自引入新控件库或用自制控件替代候选。
4. **确需自定义时说明证据** — 只有确认现有控件无法满足需求后才考虑自定义，并在动手前说明缺口、键盘/无障碍/主题/高对比度/虚拟化成本及建议实现边界。

## 后续迁移约束

1. **右键菜单范围** — 右键菜单只做设置和退出。左键托盘图标是唯一主面板入口，不要重新加入右键“打开”；之前尝试在 `MenuFlyout` 命令中主动打开主 Flyout，出现过状态重入和卡死。
2. **不恢复旧宿主** — DesktopFlyouts 自己创建 XAML island 窗口，应用通过 `DispatcherShutdownMode.OnExplicitShutdown` 维持无窗口时的托盘生命周期；不要重新添加 `TrayHostWindow`、透明锚点、原生 `Flyout.ShowAt(...)` 或 WinUIEx 托盘路径。
3. **Session 生命周期** — Flyout 关闭后 Dispose `MainPanelControl` 和 `FeedPeekPanelSession`；下次打开从 SQLite 重建。切页只 Dispose 当前 Page，会话在当前 Flyout 内保留。
4. **页面缓存约束** — 当前以内存优先，`Frame.CacheSize=0`。不要重新启用 `NavigationCacheMode="Required"`，否则三页 UI 稳定内存会再次叠加。
5. **取消请求链路** — 页面刷新和加载更多继续通过 `CancellationToken` 传到 `FeedPeekPanelSession`、Core 轮询和插件 Runtime。Flyout 关闭触发取消时，不应显示错误 InfoBar。

## 建议下一步

1. 在 Phase 6 最终构建上验证无隐藏宿主时进程可持续驻留。
2. 验证托盘左键主面板、右键仅“设置/退出”、关闭设置/登录窗口后继续驻留，以及右键“退出”能完整结束进程。

## 本地化

资源文件在 `Strings/zh-CN/Resources.resw` 和 `zh-HK/Resources.resw`。通过 `LocalizationHelper.GetString(key)` 获取。

## Git 分支

当前迁移分支 `codex/desktopflyouts-migration`，主分支 `main`。
