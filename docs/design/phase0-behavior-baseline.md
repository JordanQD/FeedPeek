# FeedPeek 迁移 Phase 0：BiliRadar 行为与数据基线

> 基线日期：2026-08-28  
> 迁移分支：`codex/feedpeek-migration`  
> 基线提交：`b545371a60e81e82b5de8818254ad6b753a8357d`  
> 部署模型：packaged MSIX、x64-only、Debug 框架依赖、Release 自包含

本文冻结迁移前 BiliRadar 的可观察行为、B 站数据映射、通知基线和本地数据合同。Phase 1 及以后可以改变内部结构，但在兼容矩阵明确批准前不得无意改变这些行为。

## 1. 用户可观察行为

| 范围 | 当前基线 |
|---|---|
| 托盘生命周期 | `App.OnLaunched` 将 `DispatcherShutdownMode` 设为 `OnExplicitShutdown`；没有旧宿主窗口。关闭设置或登录窗口后进程应继续驻留，只有右键“退出”执行完整清理并结束应用。 |
| 托盘左键 | 打开或关闭唯一主 Flyout。Flyout 内容按需创建，关闭后导出快照、取消请求、Dispose 页面/session，并延迟修剪 working set。 |
| 托盘右键 | 菜单只包含“设置”和“退出”，不包含“打开”。不得通过右键命令主动 `ShowAt(...)`。 |
| 关注页 | 顶部横向直播 `ItemsRepeater`，下方最新视频虚拟化 `ListView`；刷新和滚动加载更多均支持取消。动态接口页大于 12 条时先显示 12 条，其余由内存队列按 12 条继续分页。 |
| 历史页 | 虚拟化 `ListView`，使用 `max + view_at` 游标；只保留 `business=archive` 条目；同 ID 新时间可更新和重排。 |
| 稍后再看 | 虚拟化 `ListView`，使用 `pn + ps=12` 页码；支持移除；`loadedCount + items.Count < totalCount` 决定是否还有下一页。 |
| 页面生命周期 | `Frame.CacheSize=0`；切页前 Dispose 当前页；页面结果回写受取消和 session 生命周期约束。 |
| 登录 | 无 Cookie 时显示登录通知；网页登录成功后保存规范化 Cookie，并可保存 BiliKernel token。关闭登录窗口不应结束托盘进程。 |
| 二次启动 | 单实例键为 `BiliRadar.Main`；重定向后按设置打开设置窗口或自定义网页。 |
| 开机启动 | packaged `StartupTask` ID 为 `BiliRadarStartupTask`。 |

## 2. B 站映射与身份基线

### 2.1 最新动态

- 响应入口：`data.items`；游标为 `data.offset`；继续标志为 `data.has_more`。
- 支持 `modules.module_dynamic.major.archive`、`pgc` 和 `opus`。
- 条目 ID 优先使用 `bvid`，否则使用 `aid.ToString()`。
- `bvid` 可从 `jump_url` 提取；Opus 的 `aid` 可从 `/av{aid}` 提取。
- 协议相对 URL `//...` 规范化为 `https://...`。
- 作者来自 `module_author`；发布时间来自 `pub_ts`；统计优先使用 `module_stat`，再回退到视频 `stat`。
- 缺少有效 ID 或标题的条目被忽略。

### 2.2 历史与稍后再看

- 历史只映射 `history.business` 为空或 `archive` 的条目，ID 同样优先 `bvid`、回退 `oid/aid`。
- 历史游标读取 `cursor.max` 与 `cursor.view_at`；`has_more` 可来自 `data`、`cursor`，或非空结果的新 `nextMax`。
- 稍后再看读取 `data.list` 与 `data.count`，下一页为当前页加一。
- 历史和稍后再看条目均为已读；观看/添加时间分别写入 `PublishedAt`。

### 2.3 去重合同

- 更新、历史和稍后再看各自维护独立 ID 集合。
- ID 比较使用 `StringComparer.OrdinalIgnoreCase`。
- 当前 key 仅为站点条目 ID，不含来源命名空间；迁移后必须扩展为 `ProviderId + SourceId + FeedId + ItemId`，并保留旧 ID 映射。

## 3. 通知基线

### 视频

1. 每次通知服务启动都把视频和直播基线初始化标志重置为 `false`。
2. 第一次刷新只按 `PublishedAt` 倒序保存当前视频 ID，并标记基线完成，不发送历史通知。
3. 后续刷新按大小写不敏感 ID 找出新条目，每轮最多通知 3 条。
4. 已知视频 ID 最多保存 80 个；新旧 ID 合并后去重。
5. 默认模式还会过滤 `PublishedAt > ServiceStartedAt`；自定义作者模式使用最近检查时间与服务启动时间中的较晚者，并按 `CreatorMid` 过滤。

### 直播

1. 当前直播先按 `RoomId` 去重。
2. 首次刷新只保存当前房间号，不通知。
3. 后续只通知当前列表中新出现的房间，每轮最多 3 条。
4. 已知直播列表只保存当前仍在直播的最多 80 个房间，因此下播后再次开播可以再次通知。

当前通知激活仍使用站点专属 `aid`、`url` 和 `watchLater` 动作；这些参数必须在通用通知路由落地前保留兼容测试。

## 4. LocalSettings 与凭据迁移清单

### 4.1 根容器键

| 键 | 当前类型/默认值 | 迁移要求 |
|---|---|---|
| `VideoNotificationsEnabled` | bool / `true` | 迁入通用通知规则。 |
| `LiveNotificationsEnabled` | bool / `true` | 作为站点插件可选直播能力规则。 |
| `NotificationCheckIntervalMinutes` | int / `15`，最小 1 | 迁入统一轮询设置。 |
| `KnownVideoUpdateIds` | 换行分隔字符串 | 迁入站点来源通知基线；增加 provider/source 命名空间。 |
| `KnownLiveRoomIds` | 换行分隔字符串 | 迁入站点直播通知基线。 |
| `VideoNotificationBaselineInitialized` | bool / `false` | schema migration 后幂等保留。 |
| `LiveNotificationBaselineInitialized` | bool / `false` | schema migration 后幂等保留。 |
| `NotificationTargetMode` | int：0/1/2 | 映射 None/AllFollowing/CustomCreators。 |
| `RunningLaunchAction` | int：0/1 | 保留 OpenSettings/OpenCustomWebPage。 |
| `CustomLaunchWebPageUrl` | HTTP(S) 字符串 / B 站首页 | 品牌阶段改中性默认值；已有用户值保留。 |
| `DefaultOpenPage` | int：0/1/2 | 旧 Following/History/ViewLater 映射到插件来源视图或兼容入口。 |
| `SaveMainPanelPosition` | bool / `false` | 主程序通用 UI 设置，直接保留。 |
| `LiveSectionDisplayMode` | int：0/1/2 | 迁为来源视图设置。 |
| `MainPanelHeight` | 600/800/1000，默认 800 | 主程序通用 UI 设置，直接保留。 |
| `CustomNotificationCreators` | JSON 数组，按 `Mid` 去重 | 迁为 `ProviderId + SourceId + creatorKey + kind` 规则。 |
| `AppLanguage` | string / `system` | 直接保留。 |

`CustomNotificationCreators` 元素字段为 `Mid`、`Name`、`AvatarUrl`、`VideoNotificationsEnabled`、`LiveNotificationsEnabled`。

### 4.2 `BiliRadar` 子容器

| 键 | 内容 | 迁移要求 |
|---|---|---|
| `BiliCookie` | 规范化 Cookie Header 明文字符串 | 不得自动复制到通用数据库；迁移必须取得用户确认，写入受控 credential profile，并在成功后清理旧明文。 |
| `BiliKernelToken` | 含 access/refresh token、过期时间和 cookie_info 的 JSON | 视为敏感凭据；不得写入日志、fixture 或普通设置导出。 |

Cookie 规范化规则：分号或换行分隔、去空白、忽略无 `=` 片段、名称大小写不敏感、重复名称后值覆盖前值。网页登录最低需要 `SESSDATA`、`bili_jct`、`DedeUserID`。

当前设置导出 JSON 的 `Login.Cookie` 会包含明文 Cookie。这是已知迁移风险：通用化前必须改为不默认导出凭据，旧导入兼容只能在明确确认后读取。

## 5. 自动化特征测试

测试项目：`tests/FeedPeek.Compatibility.Tests`。它不引用或加载 WinUI 宿主程序集，而是链接宿主实际使用的纯 legacy 映射与规划源码，因此可在普通 `dotnet test` 进程中独立执行。

无隐私 fixture：

- `dynamic-video-page.json`：archive、pgc、opus、忽略非视频动态、offset/has_more。
- `history-page.json`：archive 映射、非 archive 过滤、`max + view_at`。
- `view-later-page.json`：总数、页码、进度和统计字段。

当前 12 项测试覆盖：

- 三类动态 JSON 映射、URL/ID 回退和 API 错误合同。
- 历史游标与业务类型过滤。
- 稍后再看页码和 `has_more` 计算。
- 25 条接口批次切成 `12 + 12 + 1`。
- 条目 ID 大小写不敏感去重。
- Cookie 规范化。
- 视频/直播首次通知基线、后续新条目、每轮 3 条、最多 80 个已知 ID，以及直播“在线 → 下播 → 再开播”时再次通知。

验证命令：

```powershell
dotnet build BiliRadar/BiliRadar.csproj -p:Platform=x64 --no-restore
dotnet build tests/FeedPeek.Compatibility.Tests/FeedPeek.Compatibility.Tests.csproj -p:Platform=x64 --no-restore
dotnet test tests/FeedPeek.Compatibility.Tests/FeedPeek.Compatibility.Tests.csproj -p:Platform=x64 --no-build --no-restore
```

2026-08-28 自动验证结果：

- `dotnet build BiliRadar.slnx -p:Platform=x64 --no-restore -m:1`：成功，0 错误；仅有 NuGet 漏洞索引 `NU1900` 网络警告。
- `dotnet test ... --no-build --no-restore`：12/12 通过。
- 解决方案构建使用 `-m:1`，避免 WinUI XAML 编译器在解决方案并行构建时取消自身子进程；BiliRadar 单项目构建不需要该参数。

2026-08-28 人工验收结果：通过。

- 托盘左键可以打开和关闭主 Flyout。
- 托盘右键菜单仅包含“设置”和“退出”。
- 关闭设置窗口和登录窗口后应用继续驻留。
- 右键“退出”可以完整结束进程。

## 6. Phase 0 退出判断

- [x] 基线提交和迁移分支已记录。
- [x] 三页、菜单、通知、登录和托盘生命周期行为已形成文字合同。
- [x] B 站核心 JSON 映射、分页、去重和通知基线已有独立特征测试。
- [x] fixture 全部为人工构造，不含 Cookie、Token、真实 UID 或个人响应。
- [x] LocalSettings、Cookie、BiliKernel token、StartupTask 和单实例键已记录。
- [x] 在最终改动上真实启动并复核托盘左键、右键、设置/登录窗口关闭后驻留和显式退出。
