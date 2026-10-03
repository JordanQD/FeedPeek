# FeedPeek 插件架构说明

> 状态：**实现说明**，以 `main` 分支代码为准（不是计划文档）。
> 覆盖代码：`src/FeedPeek.PluginContracts`、`src/FeedPeek.PluginRuntime`、`src/FeedPeek.PluginHost`、`src/FeedPeek.App`。
> 背景与阶段计划见 `docs/design/rss-plugin-migration.md`、`docs/design/rss-plugin-phase4-plan.md`、`docs/design/rss-plugin-phase5-plan.md`。
> 第三方方言（把别的宿主格式的插件接进来）的计划见 `docs/design/liveparse-compat-plan.md`。

## 0. 一句话概括

FeedPeek 本体不内置任何视频网站插件。网站能力通过**用户手动安装的 JS 插件目录 + `plugin.json` 清单**接入：宿主校验清单与包哈希后，用 Jint 在进程内为每个插件建一个隔离 realm，只通过 JSON 信封和显式 Host Bridge 与插件通信；插件返回的通用 DTO 由 `PluginHost` 映射成 Core 模型，再当作一个普通 `IFeedProvider` 注册进会话。

## 1. 分层与依赖方向

```text
FeedPeek.Core            FeedPeek.PluginContracts
        \                /
         FeedPeek.PluginHost
                  |
        FeedPeek.PluginRuntime   <- Jint 4.16.1（版本锁定在 csproj 的 [4.16.1]）
```

| 项目 | 职责 | 不做什么 |
|---|---|---|
| `FeedPeek.Core` | 站点中立的领域模型（`FeedItem` / `FeedPage` / `SourceDefinition` / `FeedSubscription` / `CreatorDescriptor`）、`IFeedProvider`、`FeedProviderRegistry`、SQLite 持久化、轮询 | 不引用 Jint，不认识插件动作或站点字段 |
| `FeedPeek.PluginContracts` | manifest 记录、协议信封、DTO、JSON Schema、错误信封 | 不引用 WinUI / `ApplicationData` / 数据库实体 |
| `FeedPeek.PluginRuntime` | 发现、manifest 校验、包哈希、Jint 执行、Host Bridge 实现、日志脱敏 | 不引用 Core |
| `FeedPeek.PluginHost` | `PluginClient` 强类型调用、DTO 校验、Core 映射、调用上下文授权 | 不直接接触 Jint |
| `FeedPeek.App` | `FeedPeekPluginRuntime` 组合根、`PluginInstallationService`、凭据存储、登录窗口、设置页与主面板接线 | - |

`FeedPeek.Rss` 是内置的 RSS Provider，与插件 Provider 在 Core 眼里同构。

## 2. 插件包与 manifest

### 2.1 目录形状

```text
<插件根>/            例如 %LOCALAPPDATA%/Packages/<PFN>/LocalState/FeedPeek/Plugins/feedpeek.bilibili/
  plugin.json        清单（文件名常量：PluginManifestRules.FileName）
  index.js           入口（manifest.entry）
  <其它文件>          manifest.preloadScripts 与附属资源（例如 fflate、图标）
```

### 2.2 字段（`PluginManifest`，见 `src/FeedPeek.PluginContracts/Manifest/PluginManifest.cs`）

| 字段 | 类型 | 说明 |
|---|---|---|
| `pluginId` | string | 必须匹配 `^[a-z0-9]+(?:[.-][a-z0-9]+)+$`（反向域名风格、小写） |
| `version` | string | 语义化版本 |
| `apiVersion` | int | 当前只接受 `1`（`PluginProtocolVersion.Current`） |
| `displayName` | string | 必填，不超过 100 字符 |
| `entry` | string | 相对路径，必须是 `.js` |
| `preloadScripts` | string[] | 按顺序在 `entry` 之前、于同一 realm 中执行 |
| `capabilities` | string[] | 见 2.3 白名单 |
| `auth` | object? | `PluginAuthManifest` |
| `network` | object | `{ allowedHosts, allowRedirectHosts }` |
| `views` | object[] | `{ id, kind, title }`，kind 取 `feed` / `collection` / `live` |
| `sourceConfiguration` | object? | `{ fields, defaultFeedIds }` |
| `notifications` | object? | `{ autoAddToCustom: bool }`，原生插件省略时默认不自动加入自定义通知列表 |
| `creatorResolution` | object? | `{ inputHosts: string[] }`，作者链接的精确归属域名；声明时必须具有 `creator.resolve`，不授予网络访问 |
| `icon` | string? | 包内相对路径的插件图标，见 2.5 |

新增字段都带默认值，因此旧清单（没有 `runtime` / `runtimeOptions` / `icon`）继续可用。

`notifications.autoAddToCustom` 仅决定添加来源后是否自动创建来源通知规则，不授予通知能力，也不更改用户保存的开关。原生插件默认 `false`；哔哩哔哩显式声明 `false`，由用户手动创建作者通知配置。RSS/Atom 由宿主采用自动加入策略；LiveParse 转换器写入 `true`，旧兼容包没有此字段时也采用宿主直播间策略。实际可用通知类型必须同时满足已启用订阅、Feed 的 `SupportsNotifications=true` 和插件的 `feed.updates` / `feed.live` 能力；按 Feed 的 `kind` 区分更新（`feed` / `rss` / `atom`）和开播（`live`），不依赖固定 Feed ID。

原生作者来源的自定义通知使用宿主 SQLite 的 `CreatorNotificationPolicy`：按 provider/source 保存每类通知的全部已关注作者或自选作者范围和总开关。自选时还需匹配 provider/source/creator 的个人开关；全部时不复制整批作者配置。全局“所有来源更新”使用当前账号的全部关注名单和全局类型开关，自定义策略暂不参与；“不通知”不执行通知查询。新手动配置默认自选，`creator.following` 是新增全部范围的必要能力；仅有 `creator.resolve` 可逐个验证自选作者。

更新通知由 Core `NotificationUpdatePoller` 维护进程内独立首页验证器、已观察身份和分页缺口队列，不读取或覆盖前台 `FeedPollingState.NextCursor`。首次成功首页静默建立锚点；之后按插件真实游标追到之前的首页，默认每轮最多 8 次请求、2000 个新观察身份和 20 秒，按整页提交，因此单页超过条目预算时仍保留完整响应。未完成区间下轮继续，新首页再产生缺口时追加区间；304 仍可继续旧区间。重复游标不前进；连续空映射页可能只是过滤了不支持的内容，保留已前进的游标并暂停本轮，不能视为追赶完成。可靠部分页按完整 provider/source/feed/item 身份暂存，调用方接受观察后确认；账号、来源地址和有效范围变更重新建立静默基线，不继承旧账号分页状态。

更新必须同时满足首次入库时间和可信发布时间晚于本次 watch 基线，且发布时间不晚于观察时刻。缺失日期、Unix Epoch、未来时间及 `Metadata["feedpeek.publishedAtReliable"]="false"` 均静默记录。插件不得将回退成当前时间的显示日期冒充真实发布时间；无法提供真实时间时可用此字符串 Metadata 标记，不需要新增 DTO 字段。B 站更新中的缺失/无效 `pub_ts` 已采用此标记，Opus 摘要读取 `summary.text`；不逐条追加详情请求。

本轮首页查询成功（包括已有首页后的 304）才允许新 watch 初始化基线。首页失败时 `CanInitializeBaseline=false`，已初始化的 watch 仍可处理可靠暂存条目；新 watch 等到下一次成功首页才静默建立基线。首页成功而续页失败不否定首页观察。空映射首页保留最后非空锚点，同一段连续空首页只创建一个追赶区间；尚无非空锚点时不创建无目标的全历史扫描。追赶区间轮转查询，失败、重复游标或两页无新条目的区间本轮延后，继续其他区间，跨轮保留调度位置。请求预算至少为 2（首页加续页），避免配置导致续查永久无法执行。

历史去重、每 watch 已观察身份、分页区间及其游标/条目记录、未确认观察和待发送事件使用 `NotificationDiskMap` 临时 SQLite 磁盘缓存。每个连接的页缓存限制为 256 KiB，关闭连接清理临时库，不修改应用持久 schema，也不跨进程恢复通知。保留完整去重记录和未发送事件，不用 FIFO 淘汰历史身份或溢出时丢弃队列。来源/generation 退出时释放对应缓存，watch 退出时删除对应 scope，区间完成后删除区间历史。观察每批最多 `min(MaxItems, 2000)` 条，剩余条目确认后在下轮继续返回；待发送每批最多 256 条，扫描和轮转避免失败批次阻塞后续事件。批量写入事务仅用于同步区段，禁止跨 `await` 持有。

待发送扫描每次最多检查 4096 条，以固定序号终点划分轮转周期，防止持续的新事件让旧事件永远无法重试。同一事件再次被规划时合并有效 watch 身份，并替换同 watch 的旧 generation；删除某个 watch 不影响仍归属其他有效 watch 的事件。临时库采用空 `Data Source` 和 `Pooling=False`，生命周期依据 [Microsoft.Data.Sqlite 连接字符串说明](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)。缓存使用同步事务批量写入，关闭应用后下一次启动仍重新建立静默基线；本次不提供磁盘空间配额或跨进程补发。

发送标题统一为 `实际来源名|更新标题`，原生作者来源取已确认作者名，RSS/Atom 取源名；第二行使用净化后的摘要，缺失或重复标题时省略。标题最多 120、摘要最多 240 个 Unicode 文本元素，HTML 标签和嵌入脚本内容清理后截断；直播使用 `主播名|直播标题` 和本地化开播状态。待发送事件与观察游标分开保存：实际提交成功才移除，无关设置变化或提交异常不吞掉未发条目，watch generation 改变/移除则丢弃旧范围；直播完整离线快照还会清理未提交的旧开播。点击链接、图片和稍后再看动作继续使用原事件身份与能力。

后台将作者范围展开成每作者的运行时 watch，新关注作者首次成功观察静默，不重置其他作者。完整关注名单才确认取消关注；不完整结果保留已知成员并加入正向确认者，失败保持 watch 但跳过事件观察。直播 Feed 本身仍须成功且完整才能更新在线状态。账号、来源、凭据与插件变更使关系缓存和通知基线失效；关闭某个自选开关不影响全局全部范围的基线。迁移按更新/开播分别保留原等效范围，不重绑不同插件身份、不打开关闭值；删除通知策略清理该来源的自选通知配置并抑制自动补回，不取消站点关注或删除来源。通知页直接展示全部已有来源，RSS/Atom 用 SettingsCard，插件按 provider 用 SettingsExpander 分组；组内正文显示通知项与有效自选作者，副标题不重复插件名；作者来源级配置移至 ItemsFooter 的通知设置按钮，页脚使用无图标 DefaultSettingsExpanderItemStyle。ItemsFooter 底部根据 manifest 的 creator.following / creator.resolve 和通知 Feed 显示添加通知，不按插件名称或 ID 判断。B 站已声明作者能力，不需要专属 UI 注册。未保存规则的来源显示关闭的虚拟项，用户编辑才持久化，不重新开启删除/关闭值。来源/策略使用开关关闭，只有作者详情可删除。添加限定当前来源，空输入不展开关注名单。详情用原生范围下拉框保存全部/自选，旧混合范围只有用户主动选择后才统一；全部下隐藏个人卡片但保留数据，自选恢复名单与开关。关闭查询和账号变更丢弃过期结果，重复对象进入已有详情。界面效果待用户手动验收，详见统一通知实施计划第四步记录。

`auth` 子结构：

- `required`：bool
- `kind`：`webview-cookie` 或 `opaque-secret`
- `cookieDomains`：cookie 注入域（当前实现是**精确 host 匹配**）
- `authSignalCookies`：登录态信号 cookie（清单声明，供 UI / 校验判断）
- `exposedCookieNames`：**允许插件读取值的 cookie 最小白名单**
- `loginFlow`：`{ loginUrl, successUrlPrefixes[], successCookieNames[] }`

`sourceConfiguration.fields[]`：`{ key, label, kind(text|url), required, placeholder?, defaultValue? }`，最多 20 个。

### 2.3 能力白名单（`PluginManifestRules.SupportedCapabilities`，共 11 项）

`feed.updates`、`feed.live`、`account.auth`、`account.profile`、`account.history`、`collection.viewLater`、`creator.following`、`creator.resolve`、`creator.relation`、`item.actions`、`creator.actions`、`source.configuration`。

宿主**按 capability 分发行为**，不按内置站点 ID 判断（见第 9 节）。声明了 `sourceConfiguration` 就必须同时声明 `source.configuration`，否则报 `SOURCE_CONFIGURATION_CAPABILITY_MISSING`。

### 2.4 加载期校验（`PluginManifestLoader`）

数值与文件限制：

| 项 | 限制 |
|---|---|
| manifest 大小 | <= 256 KB |
| 单个脚本 | <= 1 MB |
| 全部脚本合计 | <= 4 MB |
| 脚本编码 | 严格 UTF-8，非法字节报 `PLUGIN_SCRIPT_ENCODING_INVALID` |
| 包内禁止的扩展名 | `.exe .dll .com .bat .cmd .ps1 .msi .msix .appx .scr`，报 `EXECUTABLE_CONTENT_FORBIDDEN` |
| 插件图标（`icon`） | <= 512 KB（`PluginManifestRules.MaxIconBytes`） |

结构与安全校验：

- 路径必须落在插件根之内（`PLUGIN_PATH_TRAVERSAL`）；脚本必须 `.js`（`SCRIPT_EXTENSION_INVALID`）；文件必须存在（`PLUGIN_FILE_MISSING`）。
- **拒绝 reparse point / 符号链接**：插件根、解析出的文件、以及路径上每一级祖先目录（`PLUGIN_REPARSE_POINT_FORBIDDEN`）。
- `preloadScripts` 与 `capabilities` 必须去重（`MANIFEST_DUPLICATE_VALUE`）；`entry` 不得同时出现在 `preloadScripts` 中。
- 能力、鉴权种类、view kind、来源字段 kind 必须命中白名单。
- host 列表（`network.*`、`auth.cookieDomains`）不得为空、不得重复、不得含 `*` `/` `:`，且必须是合法 DNS 名（`HOST_INVALID`），因此无法通配整个互联网。
- URL 字段必须是绝对 HTTPS（`URL_INVALID`）。
- `view.id` 非空且唯一，`view.title` 非空。
- `sourceConfiguration.defaultFeedIds` 必须是已声明的 view id（`SOURCE_DEFAULT_FEED_INVALID`）；`url` 类型字段的 `defaultValue` 必须是 HTTPS。
- `icon` 走和脚本同一条路径校验（落在插件根内、拒绝 reparse point），扩展名必须命中 `SupportedIconExtensions`（`.png .jpg .jpeg .ico .bmp .gif .webp`，`ICON_EXTENSION_INVALID`），大小受 `MaxIconBytes` 限制（`ICON_TOO_LARGE`）。

包哈希（`PluginPackage.PackageHash`）：

- 算法 **SHA-256**（`IncrementalHash`），对包内**所有文件**按相对路径（`/` 分隔）ordinal-ignore-case 排序，先 append 相对路径的 UTF-8 字节，再 append 文件内容字节。
- 发现阶段与安装记录比对，失配报 `PACKAGE_HASH_MISMATCH`；比对用 `CryptographicOperations.FixedTimeEquals`。
- 结果是 `PluginPackage(RootPath, ManifestPath, Manifest, PackageHash, Scripts)`，其中 `Scripts` 是**加载时快照的脚本文本**，执行阶段只读快照，不再回读磁盘。

### 2.5 插件图标

插件包可以自带一个图标（`icon`，包内相对路径，例如 `bilibili.ico` 或 `assets/live_card_douyu.png`），用户在插件详情页顶部还能换掉它。生效顺序：

| 优先级 | 来源 | 位置 |
|---|---|---|
| 1 | 用户自定义图标 | `AppSettings` 的 `PluginIcon.<pluginId>`，文件复制到 `LocalFolder/plugin-icons/<pluginId>_<guid><ext>` |
| 2 | 插件包自带图标 | 清单 `icon`，运行时拼成 `ms-appdata:///local/FeedPeek/Plugins/<目录>/<icon>` |
| 3 | 字形回退 | `PluginGlyph`（`\uE74C`），用于没有图标的插件 |

- URI 只在插件位于用户插件根之内时生成（`PluginInstallationService.ResolvePackageIconUri`）：`ms-appdata` 到不了任意的开发目录，开发根里的插件继续走字形回退。
- 图标是**每个插件一份**，不是每个来源一份；来源自己的图标仍由 `iconUrl` 字段和来源详情页管理（见第 12 节）。
- 插件详情页顶部与来源详情页同构：96×96 `PersonPicture` + `MenuFlyout`（从文件选择图标 / 恢复默认图标）+ 悬停编辑浮层；「已添加来源」列表里的插件行用同一个 URI 作为 `HeaderIcon`。
- LiveParse 包不需要手写这个字段：转换器从包内资产里挑一个小方形图（`*live_card*` 优先，避开 `*_dark`，`tv_*` 最后），见 `docs/design/liveparse-compat-plan.md` 4.2。

## 3. 协议

### 3.1 信封

请求（`PluginRequestEnvelope<TPayload>`）：

```json
{ "requestId": "N", "apiVersion": 1, "pluginId": "N", "method": "feed.getUpdates", "payload": { } }
```

响应（`PluginResponseEnvelope<TResult>`）：成功是 `{ requestId, success, result }`，失败是 `{ requestId, success: false, error: { code, message, retryable, context } }`。

约定：UTF-8、camelCase（`PluginJson.SerializerOptions` 使用 `JsonSerializerDefaults.Web` 且大小写不敏感）、显式 `apiVersion`。边界类型是 `IPluginTransport.InvokeAsync(requestJson) -> responseJson`，**transport-neutral**，为将来换成 Named Pipe worker 预留。

### 3.2 方法表（`PluginMethods`，共 23 个）

```text
plugin.handshake        plugin.getStatus
auth.begin              auth.complete       auth.validate      auth.clear
source.create           source.get          source.listFeeds   source.delete
account.getProfile      account.getHistory  account.getMoreHistory
creator.getFollowing    creator.resolve     creator.getActions  creator.executeAction
feed.getUpdates         feed.getMoreUpdates feed.getLive
collection.getViewLater collection.getMoreViewLater
item.getActions         item.executeAction
```

`PluginProtocolLimits.MaxPageSize = 200`；`PluginFeedIds` 取 `updates` / `live` / `history` / `viewLater`。

### 3.3 关键 DTO

- `PluginInvocationContext(SubscriptionId, ProviderId, SourceId, FeedId, CredentialProfileId, Options)`：每次业务调用都必须携带。
- `PluginFeedPage(Items, NextCursor, HasMore)` / `PluginFeedItem(ItemId, Title, Url, AuthorName, PublishedAt, CreatorKey, AuthorUrl, Summary, ImageUrl, AuthorImageUrl, DurationText, Metadata)`。
- `PluginLivePage(Items, IsComplete=true)` / `PluginLiveItem(CreatorKey, DisplayName, Url, Title, AvatarUrl, CoverUrl, ViewerCount, StartedAt, Metadata)`。`isComplete` 是可选字段，旧原生插件省略时仍视为完整成功；部分房间查询失败时插件应返回 `false`，全部失败应返回错误。信息流可展示部分成功内容，但通知监控仅用完整成功结果更新在线集合，防止网络恢复后误报开播。LiveParse 兼容层已按此规则标记部分失败，并把缺失或未知直播状态视为失败。
- `PluginSourceDefinition(ProviderId, SourceId, DisplayName, CredentialProfileId, Feeds, Options)` / `PluginFeedDefinition(FeedId, DisplayName, Kind, SupportsPaging, SupportsNotifications)`。
- `PluginCreatorDescriptor(CreatorKey, DisplayName, ProfileUrl, AvatarUrl, SupportedNotificationKinds, Metadata)`。
- `PluginCreatorResolvePayload(Context, Input)` / `PluginCreatorResolveResult(Creator, FollowState)`：可选 `creator.resolve` 接收原始作者链接或 ID，站点路径与 UID 解析由插件负责。`followState` 只接受 `followed` / `notFollowed` / `unknown`；失败返回结构化错误，不能返回假未关注。Host 用请求订阅的 provider/source 赋予作者身份，校验名称、HTTP(S) 地址和通知类型（`video` / `updates` / `live`）。
- `PluginCreatorListResult(Creators, IsComplete=false)`：旧插件省略字段仍可读取关注名单，但名单缺项不能证明未关注；匹配到完整作者 ID 或个人页可确认已关注。只有成功且显式完整的名单才允许据精确作者 ID 判定未关注，不由链接最后一段猜作者 ID。

作者通知查询候选来自有效来源的已启用订阅及 `creator.resolve` / `creator.following` 能力，不固定 `updates` Feed 名称。输入域名与 `creatorResolution.inputHosts` 精确匹配时自动选择唯一来源；多个来源或不透明作者 ID 要明确选择，不向所有插件广播查询。解析字段缺省的旧插件走关注名单回退。登录失效、请求失败、关系未知和确认未关注分别显示；查询与保存都重新核验当前来源及账号，关闭对话框后取消请求。作者类型开关取作者支持类型与来源已启用通知 Feed 能力的交集。
- `ActionDescriptor(ActionId, LocalizationKey, FallbackText, IconSemantic, DangerLevel, RequiresConfirmation, IsRepeatable, Payload)`；`dangerLevel` 取 `none|warning|destructive`，`iconSemantic` 取 `add|remove|save|open|refresh|follow|unfollow|settings`。
- `HostHttpRequest(Url, Method, Headers, Body, BodyEncoding, TimeoutMs, MaxResponseBytes)` / `HostHttpResponse(Status, FinalUrl, Headers, Body, BodyEncoding)`。

Schema 位于 `src/FeedPeek.PluginContracts/Schemas/`：`plugin-manifest.v1`、`plugin-request.v1`、`plugin-response.v1`、`action-descriptor.v1`。

## 4. JS 插件契约与 Host Bridge

### 4.1 入口

```js
globalThis.FeedPeekPlugin = {
  apiVersion: 1,                    // 必须等于 manifest.apiVersion，否则 PLUGIN_EXPORT_VERSION_MISMATCH
  async handle(request) { /* ... */ } // 必须是函数，否则 PLUGIN_HANDLE_MISSING
};
```

宿主只调用 `FeedPeekPlugin.handle(request)`，不扫描其它全局。加载顺序：先按 `preloadScripts` 顺序执行，再执行 `entry`，全部在同一 realm。

### 4.2 Host（`JintPluginTransport.HostBootstrapScript`）

宿主先注入 5 个原生委托（`__feedpeekHttp`、`__feedpeekCredential`、`__feedpeekLog`、`__feedpeekMd5`、`__feedpeekNow`），bootstrap 脚本把它们包装成 `Host` 之后**立即 delete 原始全局**，再用 `Object.defineProperty(globalThis, "Host", { writable: false, configurable: false })` 固化。

```text
Host.http.request({ url, method, headers, body, bodyEncoding, timeoutMs, maxResponseBytes })
                                                       -> HostHttpResponse
Host.credentials.getStatus({ credentialProfileId })    -> { exists }
Host.credentials.getOpaqueSecret({ credentialProfileId, name }) -> { value }
Host.credentials.setOpaqueSecret({ credentialProfileId, name, value })
Host.credentials.deleteOpaqueSecret({ credentialProfileId, name })
Host.crypto.md5(value)                                 -> 小写 hex
Host.log.debug | info | warn | error(message, context)
Host.clock.now()                                       -> ISO-8601 字符串
Host.makeError(code, message, options)                 -> Error
Host.raise(code, message, options)                     -> 抛错
```

**不提供**：CLR / 反射、文件系统、进程、注册表、COM、WinUI、定时器、`fetch` / `XMLHttpRequest`、WebSocket。脚本没有任何绕过 HTTP 桥联网的途径。

### 4.3 调用脚本与返回值约束（`BuildInvocationScript`）

每次调用都是一段 `(async () => { ... })()`：解析请求 -> `await FeedPeekPlugin.handle(request)` -> **递归校验返回值是纯 JSON**（字符串 / 数字 / 布尔 / null、数组、原型为 `Object.prototype` 或 `null` 的对象；函数、循环引用、宿主对象一律报 `PROTOCOL_RESULT_INVALID`）-> `JSON.stringify` 成响应信封。JS 异常按其 `code` / `message` / `retryable` / `context` 字段转成标准错误。

## 5. 执行沙箱与限额

`JintPluginTransport` 的 `Engine` 配置（默认值来自 `PluginRuntimeOptions`）：

| 项 | 默认值 |
|---|---|
| `Strict()` | 开启 |
| `ExperimentalFeatures` | `TaskInterop`（async/await 与 Host Promise 所需） |
| `LimitMemory` | 64 MB |
| `TimeoutInterval`（`InvocationTimeout`） | 10 s |
| `MaxStatements` | 250 000 |
| `MaxRecursionDepth` / `StackOverflowGuard` | 128 / `true` |
| `PromiseTimeout` | 10 s |
| `InitializationTimeout` | 5 s |
| `HostTaskDrainTimeout` | 2 s |
| `MaxScriptBytes` | 1 MB |
| `MaxRequestJsonBytes` | 1 MB |
| `MaxResponseJsonBytes` | 4 MB |
| `MaxConcurrentInvocations` | 1（`Validate()` 强制为 1） |

`PluginRuntimeCatalog` 另有串行保障：同一插件一个 `SemaphoreSlim(1)`；`DisableAsync` 先标记 `_stopping`、等待在飞调用排空，再 `DisposeAsync` 掉整个 realm。

初始化失败与调用期错误码：

```text
REQUEST_INVALID / REQUEST_INVALID_JSON / REQUEST_TOO_LARGE / REQUEST_CONTEXT_INVALID
API_VERSION_UNSUPPORTED / PLUGIN_ID_MISMATCH / METHOD_UNSUPPORTED
PLUGIN_TIMEOUT / PLUGIN_BUDGET_EXCEEDED / PLUGIN_MEMORY_LIMIT / PLUGIN_RECURSION_LIMIT / PLUGIN_RUNTIME_ERROR
RESPONSE_TOO_LARGE / PROTOCOL_RESPONSE_INVALID / PLUGIN_RESULT_INVALID
HOST_AUTHORIZATION_ERROR / HOST_CONTEXT_INVALID / INVOCATION_FORBIDDEN
PLUGIN_RUNTIME_UNAVAILABLE    <- Host 任务超过 HostTaskDrainTimeout 未排空，realm 被隔离
```

隔离是一次性的：一旦 `_faulted` 置位，该插件后续所有调用都直接返回 `PLUGIN_RUNTIME_UNAVAILABLE`，只有重新加载插件才会恢复。

## 6. 网络代理（`PluginHttpBridge`）

| 规则 | 值 |
|---|---|
| Scheme | 只允许绝对 **HTTPS**（`HTTP_URL_INVALID`） |
| 方法 | `GET POST PUT PATCH DELETE HEAD` |
| 初始 host | 必须精确命中 `manifest.network.allowedHosts`（`HTTP_HOST_FORBIDDEN`） |
| 重定向 | 最多 **5** 跳；每跳重新校验 host（`allowedHosts` 并 `allowRedirectHosts`），且不得降级到 HTTP |
| 保留头 | 禁止 `Host` `Cookie` `Set-Cookie` `Authorization` `Proxy-Authorization` `Content-Length` `Transfer-Encoding` `Connection`，以及 `Proxy-*` `Sec-*` |
| 自动重定向 / 自动 cookie | `AllowAutoRedirect = false`、`UseCookies = false` |
| 超时 | 夹在 100 ms ~ 60 s，默认 15 s |
| 响应体上限 | 默认 4 MB，绝对上限 16 MB（流式读取时强制截断，超限即报错） |
| Cookie 注入 | 仅当调用上下文有 `CredentialProfileId` 且请求 host 命中 `manifest.auth.cookieDomains` |
| 响应 | 剥掉 `Set-Cookie`；文本类内容按 UTF-8 返回，二进制按 base64 加 `bodyEncoding` |

Host 之外没有网络出口：插件脚本里的 URL 只有经过这个桥才可能发出。

## 7. 授权：谁可以调用哪个上下文

`PluginInvocationRegistry`（实现 `IPluginInvocationAuthorizer`，在 App 中注入）保存 `(ProviderId, SubscriptionId) -> PluginInvocationContext` 白名单：

- `plugin.handshake` 与 `plugin.getStatus` 允许**不带** `context`；
- 其它方法必须携带 `context`，且 `providerId` / `sourceId` / `feedId` / `credentialProfileId` / `options` 必须与已注册记录**完全一致**（`options` 逐键比较），否则报 `INVOCATION_FORBIDDEN`；
- 授权返回的 `PluginHostContext(PluginId, RequestId, Method, CredentialProfileId)` 会被 transport 再复核一次，三者不一致报 `HOST_CONTEXT_INVALID`；
- 凭据调用额外要求请求里的 `credentialProfileId` 等于当前授权上下文的 profile，否则报 `CREDENTIAL_SCOPE_FORBIDDEN`；除 `getStatus` 外的操作还要求 secret 名非空且不超过 100 字符。

注册来源是 `FeedPeekPluginRuntime.Register(subscription)`，由 `FeedPeekPanelSession` / `FeedPeekNotificationSession` 在会话初始化时对所有插件订阅执行。

## 8. Host 适配层（`FeedPeek.PluginHost`）

- `PluginClient.InvokeAsync<TPayload, TResult>`：生成 `requestId`（`Guid("N")`）-> 序列化 -> 经 `IPluginRequestInvoker` 调用 -> 校验返回的 `requestId` 一致（`PLUGIN_RESPONSE_MISMATCH`）-> 拆错误成 `PluginInvocationException`（带 `Code` / `Retryable` / `Context`）。
- `PluginFeedProvider : IFeedProvider`：按 `FeedId` 与是否有 cursor 路由到 `feed.getUpdates` / `feed.getMoreUpdates`、`account.getHistory` / `account.getMoreHistory`、`collection.getViewLater` / `collection.getMoreViewLater`；校验分页一致性（`HasMore` 必须有 `NextCursor`，终页不得有 cursor）、itemId 页内唯一、`Title` 与 `AuthorName` 必填、所有 URL 必须是 http(s)（`PLUGIN_RESULT_INVALID`）。`feed.live` 不走 `IFeedProvider`，保留独立的 `PluginLivePage` 合同。
- `PluginSourceService` / `PluginAuthService` / `PluginAccountService` / `PluginCreatorService` / `PluginItemActionService`：分别封装 `source.*`、`auth.*`、`account.*`、`creator.*`、`item.*`，调用前校验订阅 `ProviderId` 匹配；条目类操作还要求条目确实属于该订阅。
- 动作响应额外过 `PluginActionValidator`：`actionId` 非空 / 唯一 / 不超过 100，`fallbackText` 1~200，`dangerLevel` 与 `iconSemantic` 必须合法，`payload` 最多 32 项且键不超过 100、值不超过 4096。

## 9. 会话接线与能力分发

`FeedPeekPanelSession.EnsureInitializedAsync` 与 `FeedPeekNotificationSession.EnsureInitializedAsync` 做同样的三步初始化：

1. `FeedSession.InitializeAsync`（SQLite），并删除 Phase 4.3 遗留的自动来源 `feedpeek.video-site`；
2. `FeedPeekPluginRuntime.LoadAsync`：读取安装配置（插件根、停用集合、期望哈希）-> `PluginRuntimeCatalog.LoadAsync` -> 为每个可用插件建 `PluginFeedProvider`；
3. 把 `FeedPeekPluginRuntime.FeedProviders` 注册进 `FeedProviderRegistry`，并为已存在的插件订阅调用 `Register(subscription)`。

**能力驱动而非 ID 驱动**的判定点：

| 行为 | 依据 |
|---|---|
| 来源是否走插件 | `FeedPeekPluginRuntime.IsPluginProvider(providerId)`（即存在已加载的 provider） |
| 通知里的「稍后再看」按钮 | `collection.viewLater` **且** `item.actions`（`FeedPeekNotificationSession.CanAddToViewLater`） |
| 插件详情页显示「添加来源」 | `source.configuration` capability（`PluginSettingsRow.CanConfigureSource`） |
| 插件详情页显示登录 | `manifest.auth.loginFlow` 非空 |
| 关注页直播区数据 | 存在 `feed.live` 订阅，走 `FeedPeekPluginRuntime.GetLiveItemsAsync` |
| 关注页作者归属 | `IsPluginProvider` 且 `CreatorKey` 非空 |

注意 `FeedPeekPanelSession.GetFollowingExtrasAsync` 目前在没有**任何** `updates` 订阅时直接返回空，因此「只有直播、没有更新流」的插件会被这个早退挡住（见第 13 节第 4 条）。

## 10. 安装 / 启停 / 升级 / 卸载（`PluginInstallationService`）

- 用户插件根：`ApplicationData.Current.LocalFolder/FeedPeek/Plugins`。
- `GetPluginsAsync` / `InstallAsync` 返回的 `PluginManagementEntry` 带上 `IconUri`（插件包图标，见 2.5）；`PluginSettingsRow.IconUri` 再叠加用户自定义图标。
- 安装记录：`ApplicationData.Current.LocalFolder/FeedPeek/plugin-installations.v1.json`，元素是 `PluginInstallationRecord(PluginId, Version, PackageHash, DirectoryName, IsEnabled, InstalledAt)`；写入走 `.tmp` 加 `File.Move(overwrite: true)`。
- **默认关闭面**：目录里存在但没有安装记录的插件会被强制加入停用集合，不会被加载（`GetLoadConfigurationAsync`）；以 `.` 开头的目录被忽略；多个目录声明同一 `pluginId` 时全部判为 `PLUGIN_ID_DUPLICATE` 并拒绝加载。
- 安装 / 升级共用“安装插件”入口（同一 `pluginId` 即替换；不比较版本高低，同版本也允许替换）：
  1. 校验源目录，复制到 `.staging-{guid}`；
  2. 再校验 staging（防止复制后标识变化）；
  3. 旧目录移动到 `.backup-{guid}`；
  4. staging 移入目标路径；
  5. 更新版本号与包哈希的安装记录，成功后清理备份。
  安装记录提交前失败：删掉已就位的目录并把备份移回，保证旧版本仍可继续加载。记录提交后备份清理失败只保留隐藏备份，不能回滚目录而留下新哈希。替换不创建或删除来源，也不清除凭据；安装后的插件启用。当前没有在线检查或下载更新。
- 本地插件图标（`file` / `ms-appdata`）先短暂读取字节、关闭文件，再以 `InMemoryRandomAccessStream` 解码，不能让 WinUI `BitmapImage.UriSource` 长期持有包内图标文件。2026-10-01 拒绝访问问题已由运行进程的 `bilibili.ico` 文件句柄确认；原路径 ACL 为当前用户 FullControl、文件非只读。安装/卸载提交后清除本地图标缓存，避免相同 URI 继续显示旧图；读取/解码失败仍触发设置卡片默认图标回退，卸载卡片时解绑回调。
  验证：本地文件分离读取、保留字节后移动/删除插件目录、应用数据 URI 与路径越界，以及作者解析/Runtime 回归共 **76 项通过**；App Debug x64 构建 **0 警告、0 错误**。未替换正在运行的 Release 进程或已安装插件；重新编译并使用新 App 后，由用户复测图标可见时安装/替换插件，以及缺失/损坏图标的回退。
- 启停：只改安装记录里的 `IsEnabled`。停用的插件不加载，因此不再刷新、不再产生通知，也不会出现「找不到 Provider」错误；停用不删缓存。
- 卸载：目录先移到 `.uninstall-{guid}` -> 写记录（去掉该项）-> 成功后删备份；写记录失败则把目录移回。备份删不掉时留待后续清理，**不会复活插件**。
- 所有目录名都过 `ResolveUserPath`：拒绝绝对路径与非法字符，并在 `Path.GetFullPath` 之后做包含性检查。
- **热加载时机**：安装记录或插件目录时间戳改变后，下次 `FeedPeekPluginRuntime.LoadAsync` 会根据 change token 重建 Provider；只有 token 未变化时才跳过加载。来源列表刷新、后续面板/通知操作会检测变化，不在替换目录的瞬间强制重建正在使用的 UI。

## 11. 凭据与登录

- 存储：`PluginCredentialProfileStore` 实现 `IPluginCredentialStore` 与 `IPluginCookieProvider`，基于 Windows `PasswordVault`：
  - Resource：`FeedPeek.PluginCredentialProfile.v1`；
  - UserName：`escape(pluginId)|escape(credentialProfileId)`；
  - 载荷：`{ CookieHeader, Secrets }`，`Secrets` 只保存 `exposedCookieNames` 里显式授权的 cookie 值。
- 旧数据迁移：`LegacyVideoSiteCompatibility`（`feedpeek.video-site` / `account-default`）只在首次加载对应插件时把旧的 `CookieStore` 导入一次；`ClearCredentialsAsync` 会同时清掉旧 `CookieStore`。
- 登录流程（`WebSignInWindow`）：
  1. 宿主调 `auth.begin`，要求插件返回 `state == "webviewRequired"`，否则报错；
  2. 读 `manifest.auth.loginFlow`，用 `auth.cookieDomains` 下的域清一遍旧 cookie；
  3. 导航到 `loginUrl`；
  4. 导航完成且 URL 以某个 `successUrlPrefixes` 开头时，收集 `cookieDomains` 的 cookie；
  5. `successCookieNames` 必须齐全，否则不保存；
  6. `SaveWebCookiesAsync` 落盘 -> `auth.complete` -> `auth.validate`，两者都返回 `state == "authenticated"` 才保留；任一失败即删除刚写入的 profile 并抛错。
- 登出（`ClearCredentialsAsync`）：先尽力调 `auth.clear`（失败也继续），再删本地 profile，并删除「该插件 + 该 profile」下的全部来源（`ClearPluginCredentialsAsync`）。
- **安装、启用、登录都不会自动创建 Source 或 Subscription**；`LegacyVideoSiteCompatibility` 的历史自动来源会在会话初始化时被删除。

## 12. 来源创建与订阅

`FeedPeekPluginRuntime.CreateSourceAsync`：

1. 插件必须已加载；若 `manifest.auth.required`，必须已有该 profile 的凭据，否则报「此网站来源需要先登录」；
2. 生成 `sourceId = "source-{guid:N}"`，用一条**管理用订阅** `{pluginId}:{sourceId}:management` 走 `source.create`；
3. 校验插件返回的 `PluginSourceDefinition`（`providerId` / `sourceId` / `credentialProfileId` 必须与请求一致，`displayName` 非空，`feeds` 非空且 `feedId` 非空）；
4. 选择要启用的 feed：显式传入 > `manifest.sourceConfiguration.defaultFeedIds` > 全部 feeds；
5. 为每个选中的 feed 建订阅 `SubscriptionId = "{pluginId}:{sourceId}:{feedId}"`，`RefreshInterval` 为 15 分钟，`Options` 透传来源参数；一个都没选中则报错。

关注作者（`creator.getFollowing`）**不会**为每个作者建立 Source，只喂给通知规则（`ProviderId + SourceId + CreatorKey`）。

### 12.1 地址型来源编辑（2026-10-01）

- 添加直播间地址时，宿主按插件 `roomUrlTemplate` 匹配路径和数字房间号。模板主机的 `www.` 可省略，输入省略协议时补 HTTPS，HTTP 输入也可识别；保存为模板的规范地址 `source.address`。不把 `live.`、`m.` 等子域当作任意别名，不扩大插件网络白名单或 Cookie 域。
- 单直播间 LiveParse 来源详情复用 RSS 的“源链接”设置卡片。旧来源没有 `source.address` 时，按单个 `roomIds` 和模板推导；多房间配置不显示单链接编辑，原生账户型订阅不显示此入口。
- LiveParse 的 `source.get` 使用上下文的新 `roomIds` 严格获取房间资料，即使当前未开播也返回名称与头像；详情查询失败向宿主报错，不吞掉错误后保存旧资料。`source.create` 保留已有的暂时查询失败回退行为。返回资料不再有头像时删除旧 `source.icon`，让默认图标回退生效。
- 修改前验证当前插件、链接归属及重复房间，查询成功后在 SQLite 事务中一起保存原来源的名称、图标、源链接和订阅参数。保留 provider/source/subscription 身份及用户通知开关，清除旧轮询状态和缓存快照，避免旧 ETag、游标和通知在线状态应用到新地址。
- RSS 修改链接也先重新发现并校验 RSS/Atom，再刷新名称、格式及图标。新链接查询失败不修改原来源。地址保存后详情标题、面包屑和返回后的来源/通知列表都读取新资料；本次更新后的首次通知查询静默建立基线。
- 验证：地址匹配、旧来源链接回填、离线房间资料刷新、头像缺失回退、失败不保存及相关通知/插件回归共 92 项通过；地址保存专项 4 项通过（含随后补充的查询与保存并发用例）。App Debug x64 构建通过，0 警告、0 错误。未启动应用；人工检查不带 `www` 的斗鱼链接添加、修改为另一房间后名称/头像及面包屑更新、查询失败保留原值，以及原通知开关仍保持原值。

### 来源页的统一分组（2026-10-03）

来源页将“我的订阅”和“已安装插件”合并为“我的来源”。RSS/Atom 保持独立 SettingsCard，插件以 ProviderId 分组使用与通知页相同的 Toolkit SettingsExpander，正文展示该插件的独立来源，副标题不重复插件名称。唯一的既有自动网站来源若原本直接打开插件详情，则合并到组底部的无图标 ItemsFooter“插件设置”；同插件多个来源全部保留，正文进入来源详情，页脚按钮进入插件详情。

分组使用数据库全部来源与已安装插件的并集，已停用、加载错误及尚未添加来源的插件仍提供设置入口；找不到安装项的残留来源仍显示在不可用插件组内。来源页和通知页的插件组统一默认折叠，用户后续选择及新增/安装后的页面内刷新状态按 ProviderId 保留。来源页组副标题只显示源类型（网站订阅、直播间；缺少类型信息时回退为插件），不显示启停或版本。通知页组副标题统一为“x 个关注”，统计保存的来源/直播间对象和作者选择，作者配置入口本身不计数；全部范围继续保留保存的选择数，不查询或声称它是账号的全部关注人数。通知组正文副标题只保留通知/关系状态，不加“直播间/作者/网站订阅”前缀。头像复用原生 HeaderIcon 的圆形裁切和失败回退，不修改卡片模板、字体或内边距。列表取消固定最小高度与内部纵向滚动，交由页面 ScrollView 滚动；只有整体空状态保留 180px 提示空间。简繁资源同步，无新增依赖。

验证：App Debug x64 使用 `--no-restore -m:1` 构建通过，0 警告、0 错误；XAML/资源 XML、重复键、简繁键集合及空白检查通过。未启动应用，插件展开、页脚导航、空插件组、圆形头像及实际间距由用户手动验收。

## 13. 现状、边界与已知缺口

### 13.1 设计上明确不做的

- 不做开放式插件商店：插件必须由用户从文件夹手动安装。
- 进程内 Jint **不是安全沙箱**：单 EXE 只提供代码与目录隔离，插件崩溃、死循环或内存泄漏可能影响主程序；第三方不可信插件需要 Phase 7 的 Named Pipe Worker（协议已为此保持 transport-neutral）。
- 插件不能提供 XAML，不能访问任意文件 / Cookie / 域名，不能拉取或播放直播流。

### 13.2 缺口与不一致（✅ 表示已在 LiveParse 兼容工作中修复，见 `docs/design/liveparse-compat-plan.md` 附录 A）

| # | 现象 | 位置 |
|---|---|---|
| 1 | 清单文件名实际是 `plugin.json`，总纲文档写的是 `manifest.json` | `PluginManifestRules.FileName` 对比 `docs/design/rss-plugin-migration.md` |
| 2 | ✅ 插件 `console` / `Host.log` 的 sink 曾是 `NullPluginLogSink.Instance`（日志全丢），现由 `TracePluginLogSink` 接入应用 trace | `FeedPeekPluginRuntime` 构造函数 / `TracePluginLogSink` |
| 3 | ✅ 安装/启停/卸载后不再需要新会话：`ComputeChangeToken` 检测安装状态变化，两个会话每次操作都重同步插件 | `PluginInstallationService.ComputeChangeToken` / `FeedPeekPluginRuntime.LoadAsync` |
| 4 | 关注页直播区在没有 `updates` 订阅时直接早退，纯直播来源不会显示 | `FeedPeekPanelSession.GetFollowingExtrasAsync` |
| 5 | ✅ 凭据域已改为后缀匹配（`host == domain \|\| hasSuffix("." + domain)`），写 `bilibili.com` 可命中 `api.bilibili.com`；请求路由仍用精确 `allowedHosts` | `PluginHttpBridge.IsCredentialDomain` |
| 6 | 登录捕获只在命中 `successUrlPrefixes` 时触发，仅靠 cookie 判断成功的站点无法完成登录 | `WebSignInWindow.MainView_NavigationCompleted` |
| 7 | 直播卡片只渲染 `AvatarUrl` / `DisplayName` / `Title`，`CoverUrl` / `ViewerCount` / `StartedAt` 已在 DTO 里但未展示 | `FeedLiveItemRow` |
| 8 | `Host.http.request` 不接受 `authMode` / `cookieInject` / `signing`，只有「按域自动注入整条 Cookie header」加受限的 opaque secret | `JintPluginTransport` 与 `PluginHttpBridge` |
| 9 | 没有 Host 侧图片代理，`ImageUrl` / `AvatarUrl` 直接由 WinUI 绑定联网，图片请求不经过 `allowedHosts` 校验 | `FeedLiveItemRow` 与 `FeedItemRow` |
| 10 | 凭据生命周期只有 `auth.begin/complete/validate/clear`，没有 `setCredential` / `getCredentialStatus` 这类按需回写的接口，登出完全靠宿主删 profile | `PluginMethods` |
| 11 | 清单以前没有图标字段，插件行与插件详情页只有字形、顶部没有图标 | `PluginManifest.Icon` / `PluginDetailPage`，现已在 2.5 实现 |
