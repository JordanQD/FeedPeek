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
| `icon` | string? | 包内相对路径的插件图标，见 2.5 |

新增字段都带默认值，因此旧清单（没有 `runtime` / `runtimeOptions` / `icon`）继续可用。

`auth` 子结构：

- `required`：bool
- `kind`：`webview-cookie` 或 `opaque-secret`
- `cookieDomains`：cookie 注入域（当前实现是**精确 host 匹配**）
- `authSignalCookies`：登录态信号 cookie（清单声明，供 UI / 校验判断）
- `exposedCookieNames`：**允许插件读取值的 cookie 最小白名单**
- `loginFlow`：`{ loginUrl, successUrlPrefixes[], successCookieNames[] }`

`sourceConfiguration.fields[]`：`{ key, label, kind(text|url), required, placeholder?, defaultValue? }`，最多 20 个。

### 2.3 能力白名单（`PluginManifestRules.SupportedCapabilities`，共 11 项）

`feed.updates`、`feed.live`、`account.auth`、`account.profile`、`account.history`、`collection.viewLater`、`creator.following`、`creator.relation`、`item.actions`、`creator.actions`、`source.configuration`。

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
creator.getFollowing    creator.getActions  creator.executeAction
feed.getUpdates         feed.getMoreUpdates feed.getLive
collection.getViewLater collection.getMoreViewLater
item.getActions         item.executeAction
```

`PluginProtocolLimits.MaxPageSize = 200`；`PluginFeedIds` 取 `updates` / `live` / `history` / `viewLater`。

### 3.3 关键 DTO

- `PluginInvocationContext(SubscriptionId, ProviderId, SourceId, FeedId, CredentialProfileId, Options)`：每次业务调用都必须携带。
- `PluginFeedPage(Items, NextCursor, HasMore)` / `PluginFeedItem(ItemId, Title, Url, AuthorName, PublishedAt, CreatorKey, AuthorUrl, Summary, ImageUrl, AuthorImageUrl, DurationText, Metadata)`。
- `PluginLivePage(Items)` / `PluginLiveItem(CreatorKey, DisplayName, Url, Title, AvatarUrl, CoverUrl, ViewerCount, StartedAt, Metadata)`。
- `PluginSourceDefinition(ProviderId, SourceId, DisplayName, CredentialProfileId, Feeds, Options)` / `PluginFeedDefinition(FeedId, DisplayName, Kind, SupportsPaging, SupportsNotifications)`。
- `PluginCreatorDescriptor(CreatorKey, DisplayName, ProfileUrl, AvatarUrl, SupportedNotificationKinds, Metadata)`。
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
- 安装 / 升级（同一 `pluginId` 即升级）：
  1. 校验源目录，复制到 `.staging-{guid}`；
  2. 再校验 staging（防止复制后标识变化）；
  3. 旧目录移动到 `.backup-{guid}`；
  4. staging 移入目标路径；
  5. 更新安装记录，成功则删备份。
  任一步失败：删掉已就位的目录并把备份移回，保证旧版本仍可继续加载。
- 启停：只改安装记录里的 `IsEnabled`。停用的插件不加载，因此不再刷新、不再产生通知，也不会出现「找不到 Provider」错误；停用不删缓存。
- 卸载：目录先移到 `.uninstall-{guid}` -> 写记录（去掉该项）-> 成功后删备份；写记录失败则把目录移回。备份删不掉时留待后续清理，**不会复活插件**。
- 所有目录名都过 `ResolveUserPath`：拒绝绝对路径与非法字符，并在 `Path.GetFullPath` 之后做包含性检查。
- **热加载限制**：安装与启停只影响下一次 `FeedPeekPluginRuntime.LoadAsync`（`_loaded` 为 true 时直接早退）。当前 UI 安装完只刷新列表，因此新插件要到下一个会话（新的 Flyout 或重新打开的设置页）才真正生效。

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



