# FeedPeek：从 BiliRadar 迁移到通用托盘 RSS 阅读器与站点插件架构

> 文档用途：供当前仓库及后续 FeedPeek 项目中的开发者或编码代理直接读取并执行。  
> 推荐实施方式：在当前 BiliRadar 仓库内渐进迁移为 FeedPeek；保留可运行基线和阶段检查点，不进行一次性推倒重写。  
> 文档状态：实施中；Phase 0–3 已完成自动验证。
> 最近更新：2026-08-30。

## 1. 执行摘要

目标是把当前仅面向单一视频站点的 BiliRadar，迁移为一个中性品牌的 Windows 托盘信息流应用：

- 主程序原生支持 RSS 2.0 与 Atom。
- 其他网站通过可安装插件接入。
- 当前视频站点能力迁移为独立站点适配器插件。
- 主程序保留现有托盘、Flyout、列表虚拟化、通知和后台驻留体验。
- 第一阶段只有一个用户可见 EXE，插件从独立目录加载，但仍在主程序进程内执行。
- 插件合同采用 JSON 风格，未来可以把运行时移动到独立 Worker 进程，而不修改插件协议和 UI。

该迁移技术上可行，但不是简单替换 `IBiliDataProvider`。当前项目的数据模型、会话、页面、通知、登录和业务动作都带有站点专属语义，必须先建立通用信息流内核，再迁移站点实现。

## 2. 已确认的架构决策

以下决策作为迁移的默认前提。除非出现明确阻断，不应在实现过程中反复改换方向。

1. **在现有仓库渐进迁移**：当前 BiliRadar 代码是迁移起点；`main`、发布标签和迁移前测试共同作为行为与回退基线。
2. **保持 packaged MSIX**：沿用 WinUI 3、Windows App SDK、x64-only 和 Store-like 发布模式。
3. **第一阶段单 EXE**：不立即增加 `PluginWorker.exe`。
4. **插件外置**：站点实现不放入主程序核心程序集，插件以独立目录和 manifest 存在。
5. **协议先行**：插件调用采用版本化 JSON 请求/响应，不向插件暴露 WinUI 类型或主程序内部对象。
6. **宿主拥有 UI**：插件返回数据、视图描述和动作描述，不直接提供 XAML 页面。
7. **宿主拥有凭据**：插件不自行持久化 Cookie；宿主根据站点和域名为受控 HTTP 请求注入凭据。
8. **宿主拥有通用状态**：已读、未读、收藏、去重、轮询、通知和持久化由主程序负责。
9. **站点能力可选**：直播、历史、稍后再看、关注关系等不是所有插件必须实现的基础接口。
10. **为进程隔离预留**：未来开放不可信第三方插件时，将同一协议迁移到 Named Pipe/JSON Worker。

### 2.1 为什么选择在现有项目上迁移

直接基于当前项目改造是可行且推荐的，因为最难验证的部分——DesktopFlyouts 托盘、无宿主窗口生命周期、Flyout 关闭后重建、页面 Dispose、通知激活和 MSIX 打包——已经在 BiliRadar 中实现并经过验证。重新创建应用再搬运这些代码，会重复承担生命周期和打包回归风险。

原地迁移的主要风险是改动面大、长期分支容易失去可运行状态。通过以下约束控制：

- 从当前可运行提交创建 `codex/feedpeek-migration` 分支；`main` 和迁移前发布标签保持不动。
- 每个 Phase 结束时必须能构建、真实启动并完成托盘关键路径验证。
- 先增加新合同和新实现，再切换调用方，最后删除旧实现；不先删除 `IBiliDataProvider` 或旧页面。
- 使用明确的 composition root/内部开发开关在迁移期间选择 legacy BiliRadar 路径或新 FeedPeek 路径，不在业务代码中散布条件编译。
- 项目名、默认命名空间、包身份和资源的机械重命名推迟到功能通用化之后，避免品牌重命名噪声与架构改动混在一起。
- 不在同一阶段升级 Windows App SDK、DesktopFlyouts 或 CommunityToolkit。

因此，本文后续提到的“目标结构”表示当前仓库迁移完成后的结构，而不是要求另建一个完全独立的仓库。

## 3. 产品边界

### 3.1 主程序负责

- 托盘图标、Flyout、设置窗口和应用生命周期。
- RSS/Atom 订阅添加、编辑、删除和刷新。
- 通用条目存储、分页、排序、去重和已读状态。
- 后台轮询、取消、超时、重试和错误聚合。
- Windows App Notification 的生成、激活和动作路由。
- 通用卡片、通用列表、来源筛选和空状态。
- 插件发现、校验、启用、停用、升级和可恢复异常隔离；第一阶段不承诺进程级崩溃隔离。
- 凭据保存、WebView 登录和按域名注入 Cookie。
- 插件权限检查、请求域名白名单和日志脱敏。

### 3.2 插件负责

- 解析特定网站的数据和分页游标。
- 将网站数据映射为通用 `FeedItem`。
- 声明登录、直播、历史、稍后再看等可选能力。
- 提供条目动作和来源动作的描述及执行实现。
- 判断站点错误、登录失效、风控和限流状态。
- 提供插件自身的数据迁移逻辑，但不直接操作主程序数据库。

### 3.3 明确不做

第一阶段不实现：

- 开放式插件商店。
- 不可信 DLL 在主进程内执行。
- 插件提供任意 WinUI XAML 页面。
- 插件访问主程序任意文件、任意 Cookie 或任意 HTTP 域名。
- 为追求动态卸载而引入复杂的 `AssemblyLoadContext` 生命周期。
- 直播播放与弹幕，除非它们被单独确定为新产品范围。

## 4. 当前 BiliRadar 的可复用与需重构部分

### 4.1 可复用

- `TrayFlyoutService` 的 DesktopFlyouts 托盘和 Flyout 状态管理。
- `App.xaml.cs` 的单实例、显式退出和无窗口托盘驻留方式。
- `MainPanelControl` 的外壳、ProgressBar、InfoBar 和页面释放机制。
- `ListView` 虚拟化和 `ItemsRepeater` 横向区域。
- Flyout 关闭时取消请求、导出快照和 Dispose UI 的思路。
- `VideoCard` 的视觉结构、图片异步加载、版本校验和缓存思路。
- 深浅色主题、本地化、设置窗口和通知注册的基础代码。

### 4.2 必须通用化

- `BiliVideoUpdate` → `FeedItem`。
- `VideoUpdateRow` → `FeedItemRow`。
- `MainWindowSnapshot` → 通用会话快照或直接从数据库恢复。
- `MainPanelSession` → `FeedSession`。
- `UpdateMonitorService` → `FeedPollingService`。
- `BackgroundNotificationMonitor` → `FeedNotificationMonitor`。
- `NotificationService` 中视频、直播和 `aid` 激活参数。
- `Following/History/ViewLater` 固定导航。
- `CookieStore` 的单一全局站点 Cookie 模型。
- `VideoCard` 中稍后再看、关注关系和站点请求头。

### 4.3 迁入站点插件

- `BiliWebDataProvider`。
- `IBiliDataProvider` 中的站点业务能力。
- `BiliAccountService`、`BiliKernelAuthService` 和登录状态校验。
- 关注作者、直播、历史、稍后再看和站点专属 URL。
- `mid`、`aid`、`bvid`、直播房间号等标识。
- 站点 User-Agent、Referer、WBI 签名和错误码处理。

## 5. 推荐的新解决方案结构

目标产品名确定为 `FeedPeek`。下面结构是迁移完成后的目标形状，不要求在第一阶段一次性移动或重命名全部现有文件。

```text
FeedPeek.slnx
├── src/
│   ├── FeedPeek.App/
│   │   ├── App.xaml(.cs)
│   │   ├── Controls/
│   │   ├── Pages/
│   │   ├── Services/
│   │   └── Styles/
│   ├── FeedPeek.Core/
│   │   ├── Models/
│   │   ├── Feeds/
│   │   ├── Notifications/
│   │   ├── Persistence/
│   │   └── Security/
│   ├── FeedPeek.PluginContracts/
│   │   ├── Manifest/
│   │   ├── Protocol/
│   │   └── Dtos/
│   ├── FeedPeek.PluginRuntime/
│   │   ├── Discovery/
│   │   ├── JavaScript/
│   │   ├── HostBridge/
│   │   └── Validation/
│   └── FeedPeek.Rss/
│       ├── RssProvider.cs
│       └── AtomProvider.cs
├── tests/
│   ├── FeedPeek.Core.Tests/
│   ├── FeedPeek.PluginRuntime.Tests/
│   └── FeedPeek.Compatibility.Tests/
└── docs/
```

插件在开发环境和安装环境中使用相同的目录形状：

```text
Plugins/
└── site-adapter/
    ├── manifest.json
    ├── index.js
    ├── preload/
    └── assets/
```

MSIX 安装目录是只读的。内置插件可以随包发布；用户安装或升级的插件必须复制到应用数据目录，例如 `ApplicationData.Current.LocalFolder/Plugins`。

## 6. 通用领域模型

合同 DTO 不引用 WinUI、`ApplicationData`、数据库实体或主程序服务。

标识层次必须统一：

- `PluginId`：安装包和运行时实例的标识，例如一个 JS 插件目录。
- `ProviderId`：插件公开的数据提供者标识。第一版一个插件只公开一个 Provider，允许 `ProviderId == PluginId`，但合同保留两者以支持未来一个插件提供多个 Provider。
- `SourceId`：用户配置出的来源实例，例如一个 RSS URL，或某插件下的一个登录账户。
- `FeedId`：来源内部的逻辑信息流，例如 `updates`、`live`、`history`、`viewLater`。
- `SubscriptionId`：用户的轮询和通知规则，指向一个 `ProviderId + SourceId + FeedId` 组合。
- `ItemId`：插件保证在同一 Feed 内稳定的条目标识。

```csharp
public sealed record FeedItem(
    string ProviderId,
    string SourceId,
    string FeedId,
    string ItemId,
    string Title,
    string? CreatorKey,
    string AuthorName,
    string? AuthorUrl,
    string Url,
    DateTimeOffset PublishedAt,
    string? Summary,
    string? ImageUrl,
    string? AuthorImageUrl,
    string? DurationText,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record FeedPage(
    IReadOnlyList<FeedItem> Items,
    string? NextCursor,
    bool HasMore);

public sealed record FeedSubscription(
    string SubscriptionId,
    string ProviderId,
    string SourceId,
    string FeedId,
    string DisplayName,
    string? Endpoint,
    string? CredentialProfileId,
    bool IsEnabled,
    TimeSpan RefreshInterval,
    IReadOnlyDictionary<string, string>? Options);

public sealed record SourceDefinition(
    string ProviderId,
    string SourceId,
    string DisplayName,
    string? CredentialProfileId,
    IReadOnlyList<FeedDefinition> Feeds,
    IReadOnlyDictionary<string, string>? Options);

public sealed record FeedDefinition(
    string FeedId,
    string DisplayName,
    string Kind,
    bool SupportsPaging,
    bool SupportsNotifications);

public sealed record CreatorDescriptor(
    string ProviderId,
    string SourceId,
    string CreatorKey,
    string DisplayName,
    string? ProfileUrl,
    string? AvatarUrl,
    IReadOnlyList<string> SupportedNotificationKinds);
```

### 6.1 标识与去重

- 全局唯一键使用 `ProviderId + SourceId + FeedId + ItemId`。
- 核心模型不得使用 `long aid`、`long mid` 等站点类型。
- 站点标识保存在插件私有 metadata 或 action payload 中。
- `CreatorKey` 是同一 Source 内稳定的字符串作者标识，用于关注关系和自定义作者通知；不得假设它是数字 UID。
- 插件必须保证同一内容在分页和刷新之间返回稳定 `ItemId`。
- RSS 条目优先使用规范化 GUID，其次使用规范化链接，最后使用内容哈希。

### 6.2 Source 与 Subscription 创建流程

RSS 和插件来源走两条明确路径：

1. **RSS**：用户输入 URL；宿主规范化 URL、探测 feed、创建 `SourceDefinition`，其 `ProviderId` 为内置 RSS Provider，默认生成一个 `FeedId=updates`，随后创建 `FeedSubscription`。
2. **需要账户的插件**：宿主先创建新的 `credentialProfileId` 并执行 `auth.begin/complete/validate`；验证成功后调用 `source.create`，插件返回 `SourceDefinition` 和可用 feeds；宿主让用户选择要启用的 feed，并为每个选择创建 subscription。
3. **无需账户的插件**：宿主根据插件 manifest 的 source configuration schema 收集参数，调用 `source.create`，然后按返回的 feeds 创建 subscription。
4. **多账户**：每次添加账户都产生独立 `credentialProfileId` 和 `SourceId`。Source 保存其凭据配置引用；插件调用只能使用当前 Source 绑定的凭据配置。
5. **关注作者**：`creator.getFollowing` 返回 `CreatorDescriptor[]`，不会为每个作者自动创建 Source。自定义作者通知规则保存 `ProviderId + SourceId + CreatorKey + notificationKind`。
6. **删除**：删除 Source 前宿主列出关联 subscriptions；删除完成后调用 `source.delete`，再按用户选择保留或清除凭据、条目缓存和通知规则。

## 7. 插件 Manifest

下面是推荐的 manifest 形状。示例名称刻意保持中性；实际发布前需完成品牌和合规审查。

```json
{
  "pluginId": "community.video-site-adapter",
  "version": "1.0.0",
  "apiVersion": 1,
  "displayName": "视频站点适配器",
  "entry": "index.js",
  "preloadScripts": [],
  "capabilities": [
    "feed.updates",
    "feed.live",
    "account.auth",
    "account.history",
    "collection.viewLater",
    "creator.relation",
    "item.actions"
  ],
  "auth": {
    "required": true,
    "kind": "webview-cookie",
    "cookieDomains": ["example.com"],
    "authSignalCookies": ["SESSION"],
    "loginFlow": {
      "loginUrl": "https://passport.example.com/login",
      "successUrlPrefixes": ["https://www.example.com/"],
      "successCookieNames": ["SESSION"]
    }
  },
  "network": {
    "allowedHosts": [
      "api.example.com",
      "www.example.com",
      "images.example-cdn.com"
    ],
    "allowRedirectHosts": ["short.example.com"]
  },
  "views": [
    { "id": "updates", "kind": "feed", "title": "更新" },
    { "id": "history", "kind": "feed", "title": "历史" },
    { "id": "viewLater", "kind": "feed", "title": "稍后再看" }
  ]
}
```

### 7.1 Manifest 校验

插件加载前必须校验：

- `pluginId`、语义化版本和 `apiVersion`。
- 入口文件必须位于插件根目录内，禁止路径穿越。
- 声明的域名、能力和凭据种类必须属于宿主支持范围。
- 不允许通配整个互联网的网络权限。
- 插件包哈希与安装记录一致。
- 未知字段可忽略，但未知必需能力必须拒绝加载。
- 插件目录中不得包含可执行文件，除非未来协议明确允许。

## 8. 插件协议

第一阶段即使在同一进程，也必须通过序列化边界调用插件，不应直接传递 C# 服务对象。

### 8.1 请求封包

```json
{
  "requestId": "01J...",
  "apiVersion": 1,
  "pluginId": "community.video-site-adapter",
  "method": "feed.getUpdates",
  "payload": {
    "context": {
      "subscriptionId": "subscription-01",
      "providerId": "community.video-site-adapter",
      "sourceId": "account-default",
      "feedId": "updates",
      "credentialProfileId": "credential-default",
      "options": {}
    },
    "cursor": null,
    "pageSize": 30
  }
}
```

### 8.2 成功响应

```json
{
  "requestId": "01J...",
  "success": true,
  "result": {
    "items": [],
    "nextCursor": null,
    "hasMore": false
  }
}
```

### 8.3 错误响应

```json
{
  "requestId": "01J...",
  "success": false,
  "error": {
    "code": "AUTH_EXPIRED",
    "message": "登录状态已失效",
    "retryable": false,
    "context": {}
  }
}
```

### 8.4 第一版方法集合

基础方法：

- `plugin.handshake`
- `plugin.getStatus`
- `auth.begin`
- `auth.complete`
- `auth.validate`
- `auth.clear`
- `source.create`
- `source.get`
- `source.listFeeds`
- `source.delete`
- `account.getProfile`
- `creator.getFollowing`
- `feed.getUpdates`
- `feed.getMoreUpdates`
- `feed.getLive`
- `account.getHistory`
- `account.getMoreHistory`
- `collection.getViewLater`
- `collection.getMoreViewLater`
- `item.getActions`
- `item.executeAction`
- `creator.getActions`
- `creator.executeAction`

动作必须使用字符串标识，例如：

- `viewLater.add`
- `viewLater.remove`
- `creator.follow`
- `creator.unfollow`
- `browser.open`

核心层只负责展示动作、确认危险操作、调用插件并处理结果，不理解动作内部的站点含义。

### 8.5 JavaScript 入口合同

宿主按 manifest 顺序加载 preload scripts，最后加载 `entry`。入口必须在同一 JavaScript realm 中设置唯一导出对象：

```js
globalThis.FeedPeekPlugin = {
  apiVersion: 1,

  async handle(request) {
    switch (request.method) {
      case "plugin.handshake":
        return { providerId: "community.video-site-adapter", apiVersion: 1 };
      case "feed.getUpdates":
        return await getUpdates(request.payload);
      default:
        throw Host.makeError("UNSUPPORTED", "Unsupported method", {
          method: request.method
        });
    }
  }
};
```

运行规则：

- `apiVersion` 必须与 manifest 一致。
- 宿主只调用 `FeedPeekPlugin.handle(request)`，不扫描或调用其他全局函数。
- `handle` 可以返回 JSON 可序列化值或 Promise；宿主必须等待 Promise 完成。
- `undefined`、函数、循环引用、宿主对象和超出大小限制的返回值视为协议错误。
- JS 异常由宿主转成标准错误 envelope；`Host.makeError` 保留结构化错误码。
- 请求取消时宿主取消关联 Host 调用并丢弃晚到响应；超过预算的脚本按 `PLUGIN_TIMEOUT` 处理。
- 每个插件使用独立 realm；禁用或升级插件时销毁整个 realm，不尝试清理其全局变量。

### 8.6 序列化兼容规则

- JSON 统一使用 UTF-8、camelCase 和显式 `apiVersion`。
- `DateTimeOffset` 使用 RFC 3339/ISO 8601 UTC 字符串。
- 持续时间在协议中使用整数毫秒，不直接序列化 .NET `TimeSpan`。
- 枚举在协议中使用小写字符串，不使用易变化的整数值。
- 新增可选字段时旧宿主必须忽略；删除或改变字段语义必须提升 API 主版本。
- `PluginContracts` 项目保存请求、响应、manifest 和 action descriptor 的 JSON Schema，并用合同测试验证示例插件。
- Action descriptor 至少包含 `actionId`、宿主本地化键或 fallback 文本、宿主图标语义、危险级别、是否需要确认、是否可重复执行和 opaque payload。

## 9. Host Bridge

参考已检查的外部插件包，可以采用 `globalThis.Host` 风格，但需要重新定义适合本项目的最小桥接面。

```text
Host.http.request()
Host.credentials.getStatus({ credentialProfileId })
Host.credentials.getOpaqueSecret({ credentialProfileId, name })
Host.credentials.setOpaqueSecret({ credentialProfileId, name, value })
Host.credentials.deleteOpaqueSecret({ credentialProfileId, name })
Host.crypto.md5()
Host.log.debug/info/warn/error()
Host.raise()
Host.clock.now()
```

第一阶段不提供任意文件系统、进程启动、注册表、COM 或 WinUI 访问。

第一阶段默认使用 **Jint 4.16.1** 并锁定版本。它是纯托管 .NET JavaScript 解释器，便于 packaged x64 项目部署。启用时只注入显式构造的 `Host` facade，不启用 CLR 类型解析、反射或任意 .NET 对象访问。Jint 的 Promise 支持仍标记为实验性，因此 Phase 3 必须先用包含 `async/await`、Host HTTP Promise、取消和超时的样例插件完成准入验证；若无法满足，不得通过阻塞 UI 线程或暴露 CLR 绕过，应将插件 Runtime 提前迁入 Worker 或另立经过审计的引擎选型阶段。

运行时还必须设置语句或执行时间预算、最大递归/调用深度、最大返回 JSON、并发上限和取消边界。单 EXE 只能提供代码与目录隔离，不是安全边界；第三方不可信插件必须使用 Worker。

### 9.1 HTTP 代理

`Host.http.request` 由主程序执行，至少强制：

- URL 主机必须位于 manifest 的 `allowedHosts`。
- 重定向目标也必须重新检查。
- 每请求超时和最大响应体大小。
- 禁止插件覆盖 `Host`、`Cookie` 等保留头。
- User-Agent、Referer 等由插件声明或按请求设置，但需要经过白名单清洗。
- Cookie 由宿主按 `pluginId + credentialProfile + domain` 注入。
- 每次 credentials 调用中的 `credentialProfileId` 必须等于当前 invocation context 绑定的值；仅 `auth.begin/complete` 可以使用宿主刚创建并显式授权给本次登录流程的 profile。
- 日志中移除 Cookie、Token、签名和包含凭据的 URL 参数。
- 取消令牌必须从 Flyout/应用生命周期传递到 HTTP 请求。

### 9.2 凭据

- 不再使用一个全局 `BiliCookie` 键。
- 凭据按插件和账户配置隔离。
- 插件只能请求宿主代发带凭据的 HTTP 请求，默认不能读取原始 Cookie 字符串。
- 插件可按自己的 `pluginId + credentialProfileId` 读写少量受保护 opaque secret，用于确实无法由 HTTP Cookie 代理承载的 refresh token 等数据；不得枚举或读取其他插件命名空间。
- WebView 登录由宿主打开，manifest 提供登录 URL、Cookie 域名和成功判断条件。
- `auth.begin` 请求宿主打开声明式登录流程；宿主捕获成功条件后调用 `auth.complete`，插件随后通过 `auth.validate` 返回标准账户状态。
- 清除插件时可选择同时清除其凭据、缓存和订阅。
- 迁移旧 Cookie 前必须得到用户确认，并在成功后移除旧存储。

### 9.3 图片请求

插件返回的 `ImageUrl`、`AuthorImageUrl` 和 `CreatorDescriptor.AvatarUrl` 不能直接交给 WinUI 控件联网：

- UI 只能绑定宿主图片服务返回的本地缓存 URI、流或已验证图片对象。
- 宿主图片服务根据条目的 `ProviderId` 找到 manifest，并按 `allowedHosts`/CDN 声明检查 URL 和每次重定向。
- 图片请求默认不携带账户 Cookie；确需鉴权图片时必须由 manifest 单独声明，并仍由凭据代理注入。
- 限制内容类型、响应体大小、解码尺寸、并发、超时和缓存总量。
- RSS 图片使用内置 RSS Provider 的网络策略，同样经过 URL、重定向和大小检查。
- 当前 `VideoCard` 中直接引用站点 User-Agent/Referer 的下载逻辑必须迁移到该图片服务。

## 10. UI 与插件能力

插件不提供 XAML。宿主根据 manifest 的 `views` 和能力渲染有限的原生页面模板。

### 10.1 通用主界面

建议默认页面：

- 全部
- 未读
- 来源

当用户选择具有扩展能力的来源时，宿主可以显示来源内的附加视图：

- 更新
- 直播
- 历史
- 稍后再看

这样普通 RSS 不会看到无意义的直播或稍后再看按钮，同时站点插件仍能保持当前 BiliRadar 的主要体验。

### 10.2 通用卡片

`VideoCard` 迁移为 `FeedItemCard`：

- 标题、作者、描述、封面、头像和相对时间继续保留。
- 主点击动作默认为打开条目 URL。
- 右侧主按钮由宿主根据动作描述渲染。
- 右键菜单由 `item.getActions` 返回的动作描述构建。
- 插件动作只返回文本键、图标语义、危险级别、是否需要确认和 payload。
- 插件不能返回任意 XAML、路径图标 XML 或可执行脚本作为 UI。

### 10.3 页面生命周期

- 继续保持 `Frame.CacheSize=0`。
- 切页前 Dispose 当前页面。
- Flyout 关闭时取消页面和插件请求。
- 插件调用完成后必须检查页面/session 版本，禁止写回已销毁 UI。
- 插件错误显示为宿主统一的 InfoBar，不直接弹出自定义窗口。

## 11. 后台轮询与通知

### 11.1 轮询

- 主程序维护统一调度器，不允许每个插件自行创建永久 Timer。
- 每个订阅拥有最小刷新间隔、退避状态和最后成功时间。
- 同一插件请求设置并发上限。
- 支持 `ETag`、`Last-Modified` 和站点游标。
- 应用启动后的第一次拉取只建立通知基线，不把全部历史内容作为新通知。
- 网络、认证、限流和解析错误分别处理。

### 11.2 通知

通知激活参数改为：

```text
providerId
sourceId
feedId
subscriptionId
itemId
actionId
actionPayloadToken
```

通知中不再直接保存 `aid`、`mid` 或原始 Cookie。`actionPayloadToken` 指向宿主数据库中的短期动作记录，激活后由宿主重新校验插件、条目和动作。

插件可以建议通知标题、正文和可用动作，但最终通知内容和按钮由宿主构建。

## 12. 持久化

建议使用 SQLite 存储规模化数据，`ApplicationData.LocalSettings` 仅保留少量偏好。

最低表集合：

- `plugins`
- `credential_profiles`
- `sources`
- `subscriptions`
- `feed_items`
- `item_states`
- `creator_notification_rules`
- `polling_state`
- `plugin_action_tokens`
- `schema_migrations`

关键约束：

- `feed_items` 对 `(provider_id, source_id, feed_id, item_id)` 建唯一索引。
- `sources` 保存 `provider_id`、`source_id` 和可空 `credential_profile_id`，并对 `(provider_id, source_id)` 建唯一索引。
- `creator_notification_rules` 对 `(provider_id, source_id, creator_key, notification_kind)` 建唯一索引。
- 存储原始插件 metadata 时设置大小上限。
- 插件升级不能自行修改数据库结构，只能通过宿主版本化存储 API。
- 主界面快照只保存必要 UI 状态；条目数据从数据库恢复，避免复制大量对象。

## 13. RSS/Atom 内置能力

RSS 是主程序能力，不需要第三方执行代码。

应支持：

- RSS 2.0 与 Atom 1.0。
- Feed URL 规范化和可选网页 feed autodiscovery。
- `ETag`、`If-None-Match`、`Last-Modified`、`If-Modified-Since`。
- GUID、链接和内容哈希三级去重。
- HTML 摘要净化。
- 相对链接转绝对链接。
- enclosure/media 图片提取。
- 单 feed 响应体、条目数和解析耗时限制。
- 清晰的无效 XML、证书、重定向和超时错误。

不得默认执行 feed 中的脚本或把未净化 HTML 注入 WebView。

## 14. 当前站点功能兼容矩阵

| 当前 BiliRadar 功能 | 新架构承载位置 | 第一版目标 |
|---|---|---|
| 托盘左键打开 Flyout | 主程序 | 保持一致 |
| 右键仅设置/退出 | 主程序 | 保持一致 |
| 关注作者视频更新 | 插件 `feed.getUpdates` | 保持一致 |
| 最新视频分页 | 插件游标 + 宿主列表 | 保持一致 |
| 正在直播 | 插件 `feed.getLive` | 保持一致 |
| 历史记录 | 插件账户能力 | 保持一致 |
| 稍后再看列表 | 插件集合能力 | 保持一致 |
| 加入/移除稍后再看 | 插件 item action | 保持一致 |
| 关注/取消关注 | 插件 creator action | 保持一致 |
| 自定义作者通知 | 宿主订阅规则 + 插件作者标识 | 保持一致 |
| WebView 登录 | 宿主登录代理 + manifest | 保持一致 |
| 账户头像、名称和 UID 显示 | 插件 `account.getProfile` | 保持一致 |
| 关注列表及关注数量 | 插件 `creator.getFollowing` | 保持一致 |
| 登出与清除账户数据 | 宿主 `auth.clear` + 插件清理回调 | 保持一致 |
| 视频/直播通知总开关 | 宿主通知规则 | 保持一致 |
| 全部关注/自定义作者通知模式 | 宿主规则 + `ProviderId + SourceId + creatorKey` | 保持一致 |
| 单作者视频/直播通知开关 | 宿主作者订阅规则 | 保持一致 |
| 语言、开机自启、启动动作 | 主程序设置 | 保持一致 |
| 默认页、面板位置和面板高度 | 主程序 UI 设置 | 保持一致 |
| 直播区域展开/折叠 | 来源视图设置 | 保持一致 |
| 直播播放与弹幕 | 可选扩展 | 默认不纳入迁移门槛 |

## 15. 单 EXE 与 Worker 路线

### 15.1 第一阶段：单 EXE

```text
FeedPeek.exe
├── WinUI App
├── PluginRuntime
└── 外部 JS 插件目录
```

适用前提：插件由项目维护者信任和审核。

优点：

- 构建、调试和 MSIX 生命周期简单。
- 不需要 IPC、Worker 激活和双进程退出协调。
- 保持当前托盘应用只有一个进程的行为。

限制：

- 插件崩溃、死循环或内存泄漏可能影响主程序。
- 不是不可信代码的安全边界。
- 更新运行中的插件通常需要重启应用。

### 15.2 后续阶段：Worker 进程

```text
FeedPeek.exe
    ↕ Named Pipe / JSON
FeedPeek.PluginWorker.exe
    └── 外部插件目录
```

仅在以下需求出现时实施：

- 开放第三方插件安装。
- 插件需要可靠的崩溃隔离和超时强制终止。
- 需要更严格的权限与资源限制。
- 插件运行时频繁升级或依赖冲突。

由于第一阶段已经使用 JSON 合同，从单进程迁移到 Worker 时应只替换 transport 和 runtime host，不修改业务 DTO、页面或插件方法。

## 16. 品牌、代码来源与合规边界

本节是工程风险控制，不构成法律意见。

- 主程序名称、图标、商店描述和截图保持站点中立。
- 不使用站点官方 Logo、字标、吉祥物、品牌主题资产或容易造成官方合作误解的设计。
- 站点插件尽量由用户单独安装，不作为主程序核心功能宣传。
- 插件界面应清晰标注“非官方第三方适配器”，但免责声明不能替代授权。
- 站点名称仅在确有必要的来源识别和兼容说明中使用，并在发布前完成正式合规审查。
- 优先使用正式开放 API、授权流程和公开文档。
- 插件拆分、改名或增加第二个 EXE 不会自动消除接口、商标、数据或平台条款风险。
- 已检查的 `C:\Users\Q\Downloads\bilibili-2.0.0` 目录没有发现 LICENSE 或来源说明；只能借鉴 manifest/宿主桥接思想，不应直接复制其中 JS 或品牌图片到 FeedPeek。
- 第三方依赖必须记录来源、版本、许可证和 NOTICE 要求。

相关官方资料：

- [.NET 插件教程与 AssemblyLoadContext](https://learn.microsoft.com/dotnet/core/tutorials/creating-app-with-plugin-support)
- [Jint 4.16.1（NuGet，BSD-2-Clause）](https://www.nuget.org/packages/Jint/4.16.1)
- [MSIX packaged desktop 应用文件系统行为](https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)
- [Windows App SDK 部署概览](https://learn.microsoft.com/windows/apps/windows-app-sdk/deployment-overview)
- [哔哩哔哩开放平台开发者服务协议](https://open.bilibili.com/agreement/developer-service)

## 17. 分阶段实施计划

每个阶段都必须保持可构建、可运行，并设置明确退出条件。不要在一个阶段同时完成通用化、动态插件、品牌重做和 Windows App SDK 升级。

### Phase 0：冻结行为基线

目标：把迁移前的当前 BiliRadar 提交固化为可运行基线，而不是只把它当作外部参考。

- 记录三页数据、菜单、通知、登录和托盘生命周期行为。
- 为 B 站 JSON 映射、分页、去重和通知基线增加特征测试。
- 保存不含隐私数据的 mock 响应。
- 记录当前设置键和 Cookie 迁移需求。

退出条件：测试可以独立证明核心映射和关键交互预期。

### Phase 1：在当前仓库抽出通用项目骨架

- 保留当前 `BiliRadar.csproj` 作为可启动 WinUI 宿主，不重写 App、托盘或 MSIX 配置。
- 在同一解决方案增加 `FeedPeek.Core`、`FeedPeek.PluginContracts`、`FeedPeek.PluginRuntime`、`FeedPeek.Rss` 和测试项目。
- 在新增项目中定义通用 DTO 和协议 envelope；现有 BiliRadar 宿主通过 ProjectReference 使用它们。
- 增加 composition root 和 legacy adapter，使现有 `BiliWebDataProvider` 可以暂时通过新合同提供数据。
- 暂不更改包身份、exe 名、默认命名空间和用户数据目录。
- 使用 mock feed 验证新合同，但默认运行路径仍保持旧功能。

退出条件：应用行为与 Phase 0 一致；新增项目和 mock/legacy adapter 可测试，尚未删除任何旧功能。

### Phase 2：通用核心与 RSS

- 实现领域模型、SQLite、FeedSession 和 FeedPollingService。
- 实现 RSS/Atom 添加订阅、刷新、未读和通知。
- 直接复用原托盘弹窗和“关注 / 历史 / 稍后再看”三页入口，三页统一使用 WinUI Gallery `ListView with Images` 模式展示通用更新。
- 列表项不保留选中状态，整项点击打开；元数据统一显示“来源名称 · 更新时间”。
- RSS 来源的添加、查看和删除统一放在设置窗口的“来源”页面。
- 不再维护新旧双面板、内部开发开关或按 Debug/Release 区分的 UI 路径；旧 BiliRadar 已由独立仓库保存。

退出条件：RSS 路径在原托盘弹窗中完整可用，三栏读/存状态可持久化，设置页可管理来源。

### Phase 3：插件合同与 Runtime

- 实现 manifest 解析和安全校验。
- 实现版本化 JSON 请求/响应。
- 实现 Host HTTP、凭据、日志、错误和取消桥接。
- 引入并锁定 Jint 4.16.1，验证 Host API、`async/await`/Promise、执行预算、内存增长和死循环处理边界。
- 使用 mock JS 插件跑通发现、调用、禁用和错误隔离。
- 协议接口保持 transport-neutral。

退出条件：mock 插件能返回 feed、分页和动作；错误不会破坏主程序会话。

实施记录（2026-08-30）：

- `FeedPeek.PluginContracts` 已提供 v1 manifest、请求/响应 envelope、方法名、feed/action/Host DTO、transport-neutral `IPluginTransport` 及三份 JSON Schema。
- `FeedPeek.PluginRuntime` 已锁定 Jint 4.16.1；每插件使用独立 realm，并通过串行调用通道、取消令牌、Promise 超时、语句预算、内存限制、递归限制和响应体上限约束执行。
- Host Bridge 已实现 HTTP、opaque credential、日志、MD5、时钟和结构化错误；HTTP 对初始 URL 和每次重定向重新执行 manifest 域名校验，禁止插件设置 Cookie、Host、Authorization 等保留头。
- manifest 加载已校验 API/语义化版本、能力、凭据类型、视图、路径穿越、重解析点、可执行文件、通配域名及可选安装哈希。
- mock JS 插件已跑通发现、加载、Host Promise、feed/分页、动作、禁用、结构化错误后的继续调用、取消、死循环与内存增长边界。
- 必要验证位于现有 `FeedPeek.Core.Tests`，没有新增测试项目；当前 18 项通过。Release x64 整体解决方案构建 0 警告、0 错误。

### Phase 4：站点插件迁移

详细的检查点、兼容矩阵和停止规则见 [`rss-plugin-phase4-plan.md`](rss-plugin-phase4-plan.md)。

- 将 `BiliWebDataProvider` 的读取逻辑迁移到插件。
- 实现更新、直播、历史和稍后再看。
- 实现关注关系和稍后再看动作。
- 由宿主 WebView 和凭据代理完成登录。
- 不复制来源未知插件的代码或品牌资产。
- 逐项切换现有 UI 调用到插件合同；只有兼容矩阵通过后才删除 `IBiliDataProvider`、旧模型和 legacy adapter。

退出条件：兼容矩阵中除明确可选项外全部达到迁移前功能效果；默认运行路径已使用通用核心和站点插件，legacy 路径可以安全移除。

### Phase 5：FeedPeek 品牌、包身份与用户数据迁移

- 将 DisplayName、应用图标、资源文本、程序集/项目名和默认命名空间分成可审阅的小批次改为 FeedPeek。
- 包身份单独决策：若目标是给现有安装原位升级，优先保留既有 MSIX identity，仅改变用户可见品牌；若必须使用新的 Store 产品/identity，则按独立应用处理并提供显式数据迁移。
- 在改变任何 identity 或数据目录之前，先验证现有 `ApplicationData`、通知激活、开机启动和单实例键的影响。
- 对现有设置、通知规则和条目状态执行版本化、幂等的 schema migration。
- 敏感凭据迁移必须取得用户确认；不得把旧明文 Cookie 复制到通用数据库。
- 验证重复执行迁移不会生成重复数据。
- 迁移成功后清理临时明文和旧兼容键。

退出条件：用户可见品牌为 FeedPeek，升级/新装路径已明确，用户状态一致，失败可安全回滚或重试。

### Phase 6：打包与发布验收

- Debug x64 框架依赖构建。
- Release x64 自包含 MSIX。
- 安装、升级、卸载和应用数据保留验证。
- 托盘、Explorer 重启、多显示器/DPI、设置窗口、通知激活和显式退出。
- 插件缺失、禁用、损坏、超时、登录失效和 API 变更场景。
- 商店元数据、截图、品牌资源和隐私声明审查。

退出条件：构建/打包零错误，关键路径无回归，事件日志无新增崩溃。

### Phase 7：可选 Worker 进程

仅在第三方插件需求明确后启动。

- 为现有 JSON 协议实现 Named Pipe transport。
- 增加 Worker 启动、握手、心跳、超时终止和自动重启。
- 限制单插件并发和资源使用。
- 主程序退出时可靠结束 Worker。
- 插件崩溃后不影响托盘和其他来源。

退出条件：同一插件无需修改即可在进程内和 Worker transport 下通过合同测试。

## 18. 测试策略

### 18.1 合同测试

- manifest 缺字段、未知版本、路径穿越和越权域名。
- JSON 请求/响应 schema 兼容。
- 插件超时、取消、异常、无效 JSON 和超大响应。
- 动作幂等性和重复点击。

### 18.2 数据测试

- RSS GUID、链接、哈希去重。
- 多来源同 ID 不冲突。
- 分页中重复和顺序变化。
- 条目更新时间变化但 ItemId 不变。
- 首次通知基线和后续新条目通知。

### 18.3 UI 与生命周期

- Flyout 连续打开/关闭。
- 刷新或加载更多时关闭 Flyout。
- 页面 Dispose 后插件结果不能写回。
- 插件缺失时显示来源不可用，不让主程序崩溃。
- 登录窗口关闭后托盘继续驻留。
- 右键菜单仍只包含设置和退出。

### 18.4 安全测试

- 插件请求未声明域名。
- 重定向到未声明域名。
- 插件尝试设置 Cookie 或 Host 头。
- 日志不得包含 Cookie、Token 或认证 URL。
- 插件目录符号链接/路径穿越。
- 恶意 HTML 摘要和异常图片 URL。

## 19. 迁移完成定义

只有满足以下条件，才能认为迁移完成：

- 主程序在不安装站点插件时是一款完整可用的 RSS/Atom 托盘阅读器。
- 主程序源码和商店资产保持站点中立。
- 站点适配器位于独立插件目录，通过版本化合同调用。
- 当前 BiliRadar 主要功能均由插件能力恢复，或被明确记录为移除项。
- Cookie 不再以全局明文站点键供所有代码读取。
- 插件不能绕过宿主访问未声明域名或其他插件凭据。
- Flyout 关闭、页面切换和应用退出时请求能可靠取消。
- Debug、Release 和 MSIX 验证全部通过。
- 插件损坏或不可用不会阻止主程序启动和 RSS 功能。
- 后续切换到 Worker 进程不需要改变插件业务协议。

## 20. 给当前仓库迁移代理的首轮任务

读取本文档后，不要立即迁移站点代码或重命名整个仓库。首轮只执行以下工作：

1. 检查当前仓库的 `AGENTS.md`、分支、工作区状态和构建方式。
2. 创建 `codex/feedpeek-migration` 分支并记录基线 commit；不要触碰用户已有的无关改动。
3. 执行 `dotnet build BiliRadar/BiliRadar.csproj -p:Platform=x64`，并记录托盘/Flyout/退出行为基线。
4. 增加 `FeedPeek.Core`、`FeedPeek.PluginContracts` 及测试项目，但保持 `BiliRadar.csproj` 仍是唯一启动项目。
5. 定义 `FeedItem`、`FeedPage`、`FeedSubscription` 和协议 envelope。
6. 创建 mock provider 与 legacy adapter，不修改默认 UI 数据来源。
7. 构建并真实启动，确认新增项目没有破坏托盘驻留、Flyout 和退出路径。
8. 提交实际目录结构、已验证行为和 Phase 1 差距，再进入 RSS 与 PluginRuntime 实现。

任何实现若需要偏离“单 EXE、宿主 UI、宿主凭据、JSON 合同”的已确认决策，应先在设计记录中解释阻断证据和替代方案。
