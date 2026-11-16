# FeedPeek 原生插件开发指南（供 AI 直接读取）

协议：`apiVersion: 1`，运行时：`feedpeek-v1`。本文按 2026-10-25 当前源码核对。
本文包含最小模板、方法和字段合同，可单独交给编程 AI。网站接口必须另外提供证据，不能从 FeedPeek 协议推测。

## 1. 给编程 AI 的任务指令

你正在为 FeedPeek 开发一个可从本地文件夹安装的 JavaScript 网站插件。请先读取全文，再执行：

1. 确认目标网站、需要的功能、一个来源代表什么（账户/频道/作者/房间）、登录要求、插件 ID，以及实际接口文档或脱敏响应。
2. 先完成 `plugin.json`、`index.js`、`README.md`，只声明已经实现并验证的能力。
3. 网站接口、签名、分页、登录证据缺失时，明确缺少什么；可交付标明为演示的离线模板，但不得编造接口或宣称真实站点可用。
4. 使用本文的 Host API 和业务对象合同。不得生成 Node.js 应用、浏览器扩展、WinUI 界面或额外服务来代替插件。
5. 实现来源恢复、正确分页、错误传播和可复现的脱敏测试；保留请求的宿主上下文，不生成或改写 Source ID。
6. 交付文件本身、安装步骤、能力清单、测试结果和未验证部分。区分本地模拟、真实网络和应用界面验证。

用户可复制以下需求单，与本文一起发送：

```text
请按这份指南直接生成 FeedPeek 原生插件文件。
目标网站及首页：
pluginId（例如 orgname.site）：
插件显示名称：
功能：更新 / 直播 / 历史 / 稍后再看 / 作者 / 动作（选择需要的）
一个来源代表：
来源配置字段：
登录：不需要 / WebView Cookie（说明登录入口和成功条件）
接口依据：文档链接、已核实请求，或脱敏 JSON 响应
分页与时间字段说明：
输出目录：
不要猜测缺失接口；先完成有证据的部分并列出阻塞项。
```

## 2. 包、运行环境与身份

```text
my-site-1.0.0/
├── plugin.json
├── index.js
└── README.md
```

- JavaScript 在 Jint 中运行，入口为 `globalThis.FeedPeekPlugin = { apiVersion: 1, async handle(request) {...} }`。
- 没有 Node.js、npm 加载器、`require`、ES module 导入、DOM、`window`、浏览器存储或全局 `fetch` 保证。HTTP 使用 `Host.http.request`。依赖须预先打包为普通 JS，或列入 `preloadScripts`，按顺序在入口前执行。
- 返回普通 JSON 值；不要返回 `Date`、`Map`、`Set`、`undefined`、函数、BigInt、循环引用或 Host 对象。时间先转 ISO 8601 字符串。
- 插件可以有多个来源，一个来源可以有多个 Feed。身份为 `providerId + sourceId + feedId + itemId`；`providerId` 必须等于 `pluginId`。
- `pluginId` 应为你自己的稳定标识，格式 `^[a-z0-9]+(?:[.-][a-z0-9]+)+$`；不要沿用模板 ID。版本使用 SemVer，如 `1.0.0`，升级保留插件 ID。
- 运行环境会重建；不能要求先调用过 `source.create` 才能 `source.get/listFeeds` 或读取内容。配置从当前 `context.options` 恢复。宿主负责持久化来源名称和订阅。

## 3. Manifest 合同

第 10 节包含完整最小 manifest。所有数组即使为空也显式填写。无登录插件使用 `auth: null`。

| 字段 | 规则 |
|---|---|
| pluginId / version / apiVersion | 稳定 ID / SemVer / 固定 1 |
| runtime | 新插件填写 `feedpeek-v1`；省略时也使用原生合同 |
| displayName | 必填，最多 100 字符 |
| entry / preloadScripts | 包内相对 `.js` 路径；不得越界、使用符号链接；入口不能同时放进 preloadScripts |
| capabilities | 只允许第 5 节列出的值，不能重复 |
| network.allowedHosts | HTTP 请求的精确 DNS 主机名，例如 `api.example.com`；不含协议、路径、端口、通配符 |
| network.allowRedirectHosts | 额外允许的重定向目标主机，通常为空；不是初始请求权限 |
| views | `{ id, kind, title }[]`；kind 为 `feed` / `collection` / `live` |
| sourceConfiguration | 需同时声明 `source.configuration`，见下文 |
| website | 可选，绝对 HTTPS 首页，作为“打开源网站”的默认值 |
| icon | 可选，包内图片，最大 512 KiB；png/jpg/jpeg/ico/bmp/gif/webp |
| notifications | 可选，`{ "autoAddToCustom": false }`；原生插件默认不自动加入自定义来源通知 |
| creatorResolution | 声明 `creator.resolve` 时可配置 `{ "inputHosts": ["www.example.com"] }`，1–16 个精确主机；该字段不授予 HTTP 权限 |

来源配置例子：

```json
{
  "fields": [{
    "key": "channelId", "label": "频道 ID", "kind": "text", "required": true,
    "placeholder": "请输入频道 ID", "defaultValue": null
  }],
  "defaultFeedIds": ["updates"]
}
```

字段 kind 仅支持 `text` / `url`，最多 20 个字段；值经 options 传入，**所有值为字符串**。`defaultFeedIds` 必须属于 views 中的 ID。
当前内容读取路由使用固定 Feed ID：`updates`、`history`、`viewLater`；直播为 `live`。不要自行发明 Feed ID 后期待宿主自动调用。

## 4. 请求与返回：不要自行包装 envelope

`handle` 收到的请求例子：

```json
{
  "requestId": "host-generated-id",
  "apiVersion": 1,
  "pluginId": "example.updates",
  "method": "feed.getUpdates",
  "payload": {
    "context": {
      "subscriptionId": "host-subscription-id",
      "providerId": "example.updates",
      "sourceId": "host-source-id",
      "feedId": "updates",
      "credentialProfileId": null,
      "options": {}
    },
    "cursor": null,
    "pageSize": 50
  }
}
```

直接返回 `{ items: [...], nextCursor: null, hasMore: false }`。运行时负责包装 `{ requestId, success, result/error }`，插件不能再包一层。
除 handshake/status 外，方法都需要宿主授权的 `payload.context`。按原值使用 provider/source/feed/subscription/profile/options，不得伪造另一个账户或来源的上下文。
`pageSize` 范围 1–200。`options`、`metadata`、动作 `payload` 等字典的值必须是字符串；结构化值自行 JSON 编码为字符串。

## 5. 能力与方法

下表中的形状在第 6–8 节定义。未实现的方法用 `Host.raise` 报错，不能以成功空结果伪装支持。

| 能力 | 方法 | payload（省略公共 context）→ 业务结果 |
|---|---|---|
| 基础，始终实现 | plugin.handshake | `{}` → `{ providerId, apiVersion: 1, capabilities: string[] }` |
| 基础，始终实现 | plugin.getStatus | `{}` → `{ state: "ready", message: null, details: null }` |
| 来源，始终实现 | source.create | `{ displayName, options }` → SourceDefinition |
| 来源，始终实现 | source.get | `{}` → SourceDefinition |
| 来源，始终实现 | source.listFeeds | `{}` → **`{ sources: SourceDefinition[] }`**，不是 feeds 数组 |
| 来源，始终实现 | source.delete | `{}` → `{ deleted: true }`，可重复调用 |
| source.configuration | 无新增方法 | 允许 manifest 声明配置字段和默认 Feed |
| feed.updates | feed.getUpdates / feed.getMoreUpdates | `{ cursor, pageSize }` → FeedPage |
| feed.live | feed.getLive | `{}` → LivePage |
| account.auth | auth.begin | `{ options }` → `{ state: "webviewRequired", data: null }`（Cookie 登录） |
| account.auth | auth.complete | `{ evidence }` → AuthState |
| account.auth | auth.validate / auth.clear | `{}` → AuthState |
| account.profile | account.getProfile | `{}` → AccountProfile |
| account.history | account.getHistory / account.getMoreHistory | `{ cursor, pageSize }` → FeedPage |
| collection.viewLater | collection.getViewLater / collection.getMoreViewLater | `{ cursor, pageSize }` → FeedPage |
| creator.following | creator.getFollowing | `{}` → `{ creators: Creator[], isComplete: boolean }` |
| creator.resolve | creator.resolve | `{ input: string }` → `{ creator: Creator, followState }` |
| creator.relation | 无独立方法 | 关系声明；不要发明 `creator.getRelation`，解析关系通过 creator.resolve 的 followState 返回 |
| item.actions | item.getActions | `{ itemId, metadata }` → ActionDescriptor[] |
| item.actions | item.executeAction | `{ actionId, payload }` → **字符串字典** |
| creator.actions | creator.getActions | `{ creatorKey, metadata }` → ActionDescriptor[] |
| creator.actions | creator.executeAction | `{ creatorKey, actionId, payload }` → **字符串字典** |

`feed.getLive` 不接收普通分页参数。已声明支持分页的 Feed 应实现首屏和更多两个方法。
动作执行成功可返回 `{ "message": "已完成" }` 或 `{}`；当前宿主动作服务读取字符串字典，不使用 `{ completed, data }` 对象。

## 6. 来源与内容 DTO

以下为字段形状说明，`null` 表示可空；线上字段使用 camelCase。

```text
SourceDefinition = {
  providerId: string, sourceId: string, displayName: string,
  credentialProfileId: string | null,
  feeds: [{ feedId: string, displayName: string, kind: "feed" | "collection" | "live",
            supportsPaging: boolean, supportsNotifications: boolean }],
  options: StringDictionary | null
}
FeedPage = { items: FeedItem[], nextCursor: string | null, hasMore: boolean }
FeedItem = {
  itemId: string, title: string, url: string, authorName: string,
  publishedAt: string (ISO 8601 with timezone), creatorKey: string | null,
  authorUrl: string | null, summary: string | null, imageUrl: string | null,
  authorImageUrl: string | null, durationText: string | null,
  metadata: StringDictionary | null
}
StringDictionary = { [key: string]: string }
```

- `source.create` 必须沿用 `context.sourceId`、`context.credentialProfileId`；不得自行分配，feeds 不为空。源名称不能为空。
- `itemId` 使用站点稳定 ID，不使用数组序号或刷新时间；一页内唯一，跨刷新保持稳定。
- title / authorName 非空；条目 URL 必须是 HTTP(S)，可选图片及作者 URL 提供时也须是 HTTP(S)。推荐 HTTPS。
- `publishedAt` 为站点实际发布时间，注意秒与毫秒、时区。缺失时如需显示回退，可用 `Host.clock.now()`，同时写 `metadata: { "feedpeek.publishedAtReliable": "false" }`，避免将回退时间当成可靠通知时间。
- 有更多结果：`hasMore: true` 且 nextCursor 为非空字符串；结束：`hasMore: false, nextCursor: null`。游标是插件定义的不透明字符串；不能将同一游标无限重复。
- 真实空列表是成功；鉴权失败、风控、限流、解析失败须抛错，不能吞错返回空列表。

## 7. 直播、作者与通知

```text
LivePage = { items: LiveItem[], isComplete: boolean }
LiveItem = {
  creatorKey: string, displayName: string, url: string,
  title: string | null, avatarUrl: string | null, coverUrl: string | null,
  viewerCount: integer | null, startedAt: ISO8601String | null,
  metadata: StringDictionary | null
}
Creator = {
  creatorKey: string, displayName: string, profileUrl: string | null,
  avatarUrl: string | null, supportedNotificationKinds: string[],
  metadata: StringDictionary | null
}
```

直播只返回正在直播的条目；同页 creatorKey 唯一、名称非空、人数非负、链接为 HTTP(S)。
完整直播结果返回 `isComplete: true`；只拿到部分页返回 false；失败或状态无法判断时抛错。不要把请求失败当成全部下播。
关注名单同样须标记完整性；`creator.getFollowing` 结果的 isComplete 默认 false，直播 isComplete 为兼容旧插件默认 true，因此新插件应显式返回。
作者通知类型只能为 `updates`、`video`、`live`，不重复。creator.resolve 的 followState 只能为 `followed` / `notFollowed` / `unknown`；拿不到关系不能猜成未关注。

三层声明作用不同：capability 决定功能、Feed 的 supportsNotifications 决定是否支持通知、notifications.autoAddToCustom 决定是否自动加入自定义来源通知。模板关闭通知；开发者验证真实时间、稳定 ID 和完整性后再开启。

## 8. 登录与动作

Cookie 登录 manifest 示例（域名和 Cookie 名必须按目标站点核实）：

```json
{
  "required": true,
  "kind": "webview-cookie",
  "cookieDomains": ["example.com"],
  "authSignalCookies": ["session_id"],
  "exposedCookieNames": [],
  "loginFlow": {
    "loginUrl": "https://www.example.com/login",
    "successUrlPrefixes": ["https://www.example.com/account"],
    "successCookieNames": ["session_id"]
  }
}
```

该对象放入 plugin.json 的 `auth`；声明 `account.auth`，有账户资料则再声明 `account.profile`。
Cookie 由宿主注入符合域范围的请求，不向 AI、日志或示例提交真实 Cookie。`exposedCookieNames` 保持最小，普通 Cookie 登录无需读取原始 Cookie。
`auth.complete/validate` 必须调用已核实的账户接口确认真实登录，成功才返回 authenticated，不能只检查 Cookie 存在。

```text
AuthState = { state: "authenticated" | "signedOut", profile: AccountProfile | null,
              message: string | null }
AccountProfile = { accountId: string, displayName: string, avatarUrl: string | null,
                   profileUrl: string | null, metadata: StringDictionary | null }
ActionDescriptor = {
  actionId: string, localizationKey: string | null, fallbackText: string,
  iconSemantic: string | null, dangerLevel: "none" | "warning" | "destructive",
  requiresConfirmation: boolean, isRepeatable: boolean,
  payload: StringDictionary | null
}
```

iconSemantic 可用 `add/remove/save/open/refresh/follow/unfollow/settings`。动作的 itemId 应放在描述符 payload 中，`item.executeAction` 顶层没有 itemId。破坏性动作应要求确认。
`opaque-secret` 是合同支持的另一种凭据类型，但当前应用的常规登录界面走 WebView Cookie；不要仅声明 opaque-secret 就宣称已有 token 输入界面。

## 9. Host API、网络与错误

```js
const response = await Host.http.request({
  url: 'https://api.example.com/verified-endpoint',
  method: 'GET',
  headers: { Accept: 'application/json' },
  body: null, bodyEncoding: 'utf8', timeoutMs: 5000, maxResponseBytes: 262144
});
if (response.status === 429) {
  Host.raise('RATE_LIMITED', '请求过于频繁，请稍后重试。', { retryable: true });
}
if (response.status < 200 || response.status >= 300) {
  Host.raise('SITE_HTTP_ERROR', '站点请求失败。', {
    retryable: response.status >= 500,
    context: { status: String(response.status) }
  });
}
if (response.bodyEncoding !== 'utf8') Host.raise('SITE_RESPONSE_INVALID', '预期文本响应。');
const data = JSON.parse(response.body);
// 接下来校验站点业务状态、必需字段，再映射到 FeedItem。
```

上面的地址仅是占位示意，不能当作实际接口。先把真实 API 主机加入 network.allowedHosts。
HTTP 返回 `{ status, finalUrl, headers, body, bodyEncoding }`；bodyEncoding 为 `utf8` 或 `base64`。不要按浏览器 Response 调用 `.json()`，也不要使用 LiveParse 的 bodyText。

| API | 用法 |
|---|---|
| Host.http.request(options) | Promise；支持 GET/POST/PUT/PATCH/DELETE/HEAD，仅 HTTPS |
| Host.clock.now() | 同步返回 UTC ISO 8601 字符串 |
| Host.crypto.md5(text) | 同步返回 MD5 字符串 |
| Host.log.debug/info/warn/error(message, context) | 同步日志；禁止记录凭据和个人响应原文 |
| Host.makeError(code, message, options) | 创建错误；options 可含 retryable、context |
| Host.raise(code, message, options) | 立即抛错；允许运行时/取消错误传播，不要 catch 后假装成功 |
| Host.credentials.getStatus({ credentialProfileId }) | Promise，返回 `{ exists }` |
| Host.credentials.getOpaqueSecret({ credentialProfileId, name }) | Promise，返回 `{ value }` |
| Host.credentials.setOpaqueSecret({ credentialProfileId, name, value }) | Promise |
| Host.credentials.deleteOpaqueSecret({ credentialProfileId, name }) | Promise |

凭据操作只能针对当前授权 profile。没有原生 `Host.session` API；不要混用兼容插件接口。
原生 HTTP 禁止手写 Host/Cookie/Set-Cookie/Authorization/Proxy-Authorization/Content-Length/Transfer-Encoding/Connection 头。不要尝试通过自带请求库绕过。
所有 Host 异步任务应 await，禁止遗留后台请求。Cookie 域权限与 HTTP 主机白名单不是同一个字段。

默认原生预算：初始化 5 秒、调用与 Promise 10 秒、250,000 条语句、64 MiB、递归 128 层；请求 JSON 1 MiB、响应 JSON 4 MiB、单脚本 1 MiB。宿主可调整，不能把默认预算当成耗时保证。有大量分页的接口应控制调用量，不得静默返回不完整结果。
Jint 是受限的应用内运行环境；用户仍需信任插件来源。

## 10. 最小可安装示例

以下两个文件与插件资料目录 `FeedPeek-Plugin/templates/native-updates` 保持一致。它只显示一条固定数据，用来验证包、来源恢复和更新合同；并未接入任何真实网站。

### plugin.json

```json
{
  "pluginId": "example.updates",
  "version": "1.0.0",
  "apiVersion": 1,
  "runtime": "feedpeek-v1",
  "displayName": "示例更新插件",
  "website": "https://example.com/",
  "entry": "index.js",
  "preloadScripts": [],
  "capabilities": ["feed.updates", "source.configuration"],
  "auth": null,
  "network": { "allowedHosts": [], "allowRedirectHosts": [] },
  "views": [{ "id": "updates", "kind": "feed", "title": "更新" }],
  "sourceConfiguration": {
    "fields": [],
    "defaultFeedIds": ["updates"]
  },
  "notifications": { "autoAddToCustom": false }
}
```

### index.js

```js
(() => {
  'use strict';
  const pluginId = 'example.updates';
  const capabilities = ['feed.updates', 'source.configuration'];

  function source(payload) {
    const context = payload.context;
    return {
      providerId: pluginId,
      sourceId: context.sourceId,
      displayName: payload.displayName || '示例更新',
      credentialProfileId: context.credentialProfileId ?? null,
      feeds: [{
        feedId: 'updates', displayName: '更新', kind: 'feed',
        supportsPaging: false, supportsNotifications: false
      }],
      options: payload.options ?? context.options ?? {}
    };
  }

  globalThis.FeedPeekPlugin = {
    apiVersion: 1,
    async handle(request) {
      const payload = request.payload;
      switch (request.method) {
        case 'plugin.handshake':
          return { providerId: pluginId, apiVersion: 1, capabilities };
        case 'plugin.getStatus':
          return { state: 'ready', message: null, details: null };
        case 'source.create':
        case 'source.get':
          return source(payload);
        case 'source.listFeeds':
          return { sources: [source(payload)] };
        case 'source.delete':
          return { deleted: true };
        case 'feed.getUpdates':
          // 固定数据只用于验证接入；接入网站时替换为已核实的 HTTP API 与字段映射。
          return {
            items: [{
              itemId: 'welcome-1', title: 'FeedPeek 插件接入成功',
              url: 'https://example.com/', authorName: 'Example',
              publishedAt: '2026-01-01T00:00:00Z', creatorKey: 'example',
              authorUrl: null, summary: '这是本地示例数据，不是网站实时内容。',
              imageUrl: null, authorImageUrl: null, durationText: null, metadata: {}
            }],
            nextCursor: null, hasMore: false
          };
        case 'feed.getMoreUpdates':
          return { items: [], nextCursor: null, hasMore: false };
        default:
          Host.raise('METHOD_UNSUPPORTED', '此插件未实现该方法。', {
            context: { method: request.method }
          });
      }
    }
  };
})();
```

另外创建 README.md，说明实际能力、配置、登录方式、安装步骤及未实现部分。

## 11. 验证与交付

1. Manifest 可解析，入口/前置脚本存在，ID、capabilities、views、默认 Feed 及方法一致。
2. 在真实 Jint/PluginHost 中验证 handshake、创建来源、更新首屏/尾页、重建运行环境后的来源恢复。
3. 有分页时测试正常末页、空页、重复页、无效游标；有网络时测试脱敏成功响应、缺字段、鉴权失败、429、5xx、取消传播。
4. 来源隔离、时间、URL、稳定 itemId 和通知完整性符合本文；不要把模拟 HTTP 成功说成真实网站成功。
5. 发布包只放插件文件和所需资源，排除 node_modules、bin/obj、测试缓存、账户信息；不放 exe/dll/ps1 等可执行文件。
6. 安装时选择包含 plugin.json 的目录。安装/登录后仍需手动添加来源；源码修改不会自动更新已安装副本，测试改版需重新安装。

宿主 fixture 测试步骤见插件资料目录 `FeedPeek-Plugin/tests/README.md`（不是应用仓库的 tests 目录）。写插件 JS 本身不需要 .NET；运行宿主合同测试需要 .NET 9 SDK、应用源码和插件测试工程。仅取得本文时可先生成上述文件，宿主验证需另行获取测试工程或在应用中验收。

## 12. 协议边界与维护

- 本文以原生插件为公开入口。当前宿主另外存在 `liveparse-v1` 兼容路径，但新插件不要混合两种入口或 manifest。旧的兼容方案文档保留为设计历史，不是本文规范。
- 当前公开能力表没有 `live.roomSearch`、`live.searchRooms`、播放或弹幕方法。不要把设计提案生成为已支持的 API。
- 若文档与未来宿主不一致，以目标版本的 `PluginManifestRules`、DTO、`PluginMethods`、ManifestLoader、JintPluginTransport、PluginHost 服务为准；记录目标应用版本并重新验证。
- 应用源码中的 JSON Schema 是辅助校验，不能代替 manifest 加载器和宿主业务验证，也不代表可在线下载的 schema 服务。
