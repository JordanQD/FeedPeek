# LiveParse 插件兼容计划（直播区只读展示）

> 状态：**计划文档**，尚未合并实现。
> 目标插件：`C:\Users\Q\Data\Projects\Live-Sources\references` 下的 `bilibili-2.0.0`、`douyin-2.0.5`、`douyu-2.0.5`。
> 相关文档：`docs/design/plugin-architecture.md`（FeedPeek 插件架构现状）、`docs/design/rss-plugin-migration.md`（总纲）。

## 0. 目标与非目标

### 0.1 目标

让上述三套 LiveParse 插件能以「来源」的形式安装进 FeedPeek，并在**关注页的直播横向区域**展示房间卡片（头像、名称、标题，点击打开直播间网页）。

### 0.2 非目标（本计划明确不做）

- 不做播放：不调用 `getPlayback` / `refreshPlayback`，不解析 m3u8/flv，不播视频。
- 不做弹幕：不调用 `getDanmaku`，不实现 `plugin_js_v1` 驱动，不建 WebSocket。
- 不做分区浏览：不调 `getCategories` / `getRooms` / `search`，一个来源只绑定用户钉住的几个直播间。
- 不做搜索 UI：不接 `search`。
- 不做插件商店 / 在线更新：仍然只支持「从文件夹安装」。

## 1. 结论摘要

| 项 | 结论 |
|---|---|
| 能否直接兼容 | **不能**。清单、JS 导出对象、Host API 形状、宿主约束四层都不一致（见第 3 节） |
| 可行路径 | 宿主侧新增一个 `liveparse-v1` 运行时方言 + 一个通用适配脚本 + 一个清单转换器；**不改动插件源码** |
| 来源模型 | 一个来源 = 若干钉住的直播间：manifest 的 `sourceConfiguration.fields` 增加 `roomIds`（可填多个，也可粘贴直播间链接），`feed.getLive` 对每个房间调 `getRoomDetail({ roomId })`，复用现有「添加来源」表单，无需新 UI |
| 分页 | 不需要。FeedPeek 直播区是一条横向列表，只取第 1 页 |
| 工作量 | 框架 3–4 人日；斗鱼 +1；哔哩 +2–3；抖音 +3–5。合计 **10–15 人日** |
| 最大风险 | 抖音 `a_bogus` 签名依赖 485KB 的 `webmssdk.js`（内含 `eval(...require(...))`），在 Jint 中大概率不可用 |
| 第二风险 | 合规：`references/` 下三套包**均无 LICENSE / README**，且内含平台签名实现 |

## 2. 目标插件现状

### 2.1 共同点

- 宿主是 **LiveParse**（Swift 应用），插件导出 `globalThis.LiveParsePlugin`，实现 8 个核心方法：`getCategories / getRooms / getPlayback / search / getRoomDetail / getLiveState / resolveShare / getDanmaku`，可选再实现凭据生命周期（`setCredential / clearCredential / getCredentialStatus / validateCredential`）与旧名兼容（`setCookie / clearCookie`）。
- Host API 只有 4 类：
  - `Host.http.request({ platformId, authMode, cookieInject, signing, request })`，`request` 为 `{ url, method, headers, body, timeout }`（`timeout` 单位是**秒**），返回 `{ status, statusCode, headers, url, bodyText, bodyBase64 }`；
  - `Host.crypto.md5(value)`；
  - `Host.raise(code, message, context)` / `Host.makeError(...)`；
  - `Host.session.getCookieHeader(platformId)`（仅抖音使用，且有 fallback）；另外插件会直接使用 `console.*`。
- 房间数据模型统一为 `LiveModel`：`userName` / `roomTitle` / `roomCover` / `userHeadImg` / `liveState` / `userId` / `roomId` / `liveWatchedCount`。
- `getRooms(payload)` 的入参是 `{ id, parentId?, page }`（按分区列房间，本计划不使用）；`getRoomDetail({ roomId })` 返回单个 `LiveModel`，是本计划使用的方法。

### 2.2 逐包现状

| 包 | 版本 | 登录 | preloadScripts | 关键差异 |
|---|---|---|---|---|
| `bilibili-2.0.0` | 2.0.0 | 必需（`SESSDATA` 等 cookie） | fflate、login、danmaku | `getRooms` 硬编码 `authMode: "platform_cookie"`；房间列表里的 `cover`/`face` **没有补 `https:` 前缀**（对比 `search` 分支显式补了） |
| `douyin-2.0.5` | 2.0.5 | 必需（`sessionid` 等 cookie） | abogus env/sdk/bind、webmssdk、fflate、login、danmaku | 需要 `a_bogus` 签名；`loginFlow` **没有 `successURLKeyword`**；会调用 `Host.session.getCookieHeader` |
| `douyu-2.0.5` | 2.0.5 | 不需要 | danmaku | 纯 JS 自实现签名，只用 `Host.crypto.md5` 与 `Host.http.request` |

## 3. 差异清单（四层，逐条带证据）

### 3.1 清单层

| FeedPeek 要求 | LiveParse 实际 | 后果 |
|---|---|---|
| 文件名 `plugin.json` | `manifest.json` | 发现阶段报 `MANIFEST_MISSING` |
| `capabilities: string[]`（11 项白名单） | `capabilities` 是**对象**（`categories`/`rooms`/`playback`/`danmaku` 等） | 反序列化即失败，报 `MANIFEST_INVALID_JSON` |
| `network.allowedHosts` 必填且不允许通配 | 没有 `network` 字段 | 无法发出任何 HTTP 请求 |
| `views[]` 必填（`kind` 取 `feed`/`collection`/`live`） | 没有 `views`；`liveTypes` 是平台编号 | 报 `MANIFEST_FIELD_MISSING` |
| `auth.kind` 取 `webview-cookie`/`opaque-secret`，且 `loginFlow` 在 `auth` 内 | `auth` 里只有 `credentialKinds: ["cookie"]`；`loginFlow` 在**顶层**，字段名是 `loginURL` | 登录链路无法建立 |
| `pluginId` 必须含点号 | `bilibili` / `douyin` / `douyu` 是单词 | 报 `PLUGIN_ID_INVALID` |

### 3.2 JS 契约层

FeedPeek 只调用 `globalThis.FeedPeekPlugin.handle(request)` 并按协议信封返回；LiveParse 插件导出 `globalThis.LiveParsePlugin` 的 8 个独立方法、直接返回模型对象。装载原生插件时会报 `PLUGIN_EXPORT_MISSING`。

### 3.3 Host API 层

| 维度 | FeedPeek | LiveParse |
|---|---|---|
| 调用形状 | `Host.http.request({ url, method, headers, body, bodyEncoding, timeoutMs, maxResponseBytes })` | `Host.http.request({ platformId, authMode, cookieInject, signing, request: { url, method, headers, body, timeout } })` |
| 返回形状 | `{ status, finalUrl, headers, body, bodyEncoding }` | `{ status, statusCode, headers, url, bodyText, bodyBase64 }` |
| 超时单位 | 毫秒（默认 15s，上限 60s） | 秒 |
| 平台 cookie | 命中 `manifest.auth.cookieDomains` 时自动注入整条 Cookie 头 | `authMode: "platform_cookie"` 显式要求注入 |
| 额外能力 | 只有 `crypto.md5` / `credentials.*` / `log` / `clock` | 还有 `session.getCookieHeader`、`cookieInject`、`signing`、`runtime.*`，并直接使用 `console.*` |

### 3.4 宿主约束层

- FeedPeek 的 `Host` 通过 `Object.defineProperty` 固化并 `Object.freeze`，**插件侧无法猴补丁替换 `Host`**，只能在宿主侧选择方言。
- FeedPeek 的 Jint 以 `Strict()` 解析所有脚本；`fflate` 这类 UMD 包在严格模式下顶层 `this` 为 `undefined`，可能在加载期就整体抛错。
- `webmssdk.js` 内含 `eval` 包裹的 `require` 分支，Jint 没有 `require`，该分支一旦执行即失败。
- FeedPeek 的 `MaxScriptBytes = 1MB`、脚本总量 `4MB`、`MaxStatements = 250000`；抖音的 preload 合计约 700KB，执行预算需要实测。

## 4. 兼容方案

### 4.1 总体架构

```text
原始 LiveParse 包（不改动）
        |  安装时转换 LiveParsePackageConverter：读 manifest.json，产出 FeedPeek plugin.json
        v
FeedPeek 用户插件目录（plugin.json + 原 JS 文件）
        |  PluginManifestLoader：校验 runtime / runtimeOptions / views / network
        v
JintPluginTransport
   |- runtime = feedpeek-v1  -> 原生 bootstrap（现状不变）
   |- runtime = liveparse-v1 -> LiveParse bootstrap（Host 方言 + console）
   |                           + 载入插件 preload/entry
   |                           + 注入宿主自带的适配脚本（包装成 FeedPeekPlugin.handle）
        v
FeedPeekPanelSession -> GetFollowingExtrasAsync -> feed.getLive -> getRoomDetail({ roomId }) x N（并行）
        v
PluginLiveItem -> FeedLiveItemRow（关注页直播横向区域）
```

三个关键点：**插件文件不动**、**适配脚本由宿主提供**（升级插件不会丢）、**转换器只在安装前跑一次**。

### 4.2 清单转换（manifest.json 到 plugin.json）

| FeedPeek 字段 | 取值规则 |
|---|---|
| `pluginId` | `liveparse.` 前缀加原 `pluginId`（满足反向域名正则） |
| `version` / `displayName` / `entry` | 原样；`apiVersion` 固定 `1` |
| `preloadScripts` | **只保留登录 preload**（文件名含 `_login`），其余（danmaku / fflate / webmssdk / abogus / sdk）全部裁掉——本计划不需要弹幕、解压与本地签名库 |
| `capabilities` | 固定 `["feed.live", "source.configuration"]`；原清单声明了登录（`auth.required` 或 `loginFlow`）时追加 `"account.auth"` |
| `auth.required` | **不照抄**。原清单的 `auth.required` 描述的是「整个平台」（搜索、已购房间、弹幕都要登录），而 FeedPeek 只映射直播视图。转换器对**已实测匿名可用**的平台（`douyin`、`douyu`）写 `false`，登录改为可选；未知平台仍沿用原值。见 6.8.5 |
| `auth` | `kind: "webview-cookie"`；`cookieDomains` 取第 5 节的逐平台 host 清单（**必须展开到具体子域**，FeedPeek 是精确 host 匹配）；`authSignalCookies` 取原 `loginFlow.authSignalCookies`；`exposedCookieNames: []` |
| `auth.loginFlow` | `loginUrl` 取原 `loginFlow.loginURL`；`successUrlPrefixes` 由 `successURLKeyword` 合成 `https://` 加该关键字（抖音没有该字段，回退用 `loginURL` 的 origin，例如 `https://www.douyin.com/`）；`successCookieNames` 取原 `authSignalCookies` |
| `network.allowedHosts` | 第 5 节逐平台的 host 清单（`allowRedirectHosts` 同值即可） |
| `views` | `[{ "id": "live", "kind": "live", "title": "正在直播" }]` |
| `sourceConfiguration.fields` | `[{ "key": "roomIds", "label": "直播间 ID", "kind": "text", "required": true, "placeholder": "每行一个或用逗号分隔，例如 74751；也可以直接粘贴直播间链接" }]` |
| `sourceConfiguration.defaultFeedIds` | `["live"]` |
| `runtime` | `"liveparse-v1"` |
| `runtimeOptions` | `{ "roomUrlTemplate": "<原 hostBehavior.externalRoomURLTemplate>" }` |

> `externalRoomURLTemplate` 形如 `https://live.bilibili.com/{roomId}`，适配脚本用它把 `roomId` 拼成直播卡片点击后打开的地址。

### 4.3 运行时方言（liveparse-v1）

宿主侧需要两段自带脚本（放在 `src/FeedPeek.PluginRuntime/Execution/`，不写进插件包）：

1. **Bootstrap**：定义 LiveParse 形状的 `Host`——`http.request` 把形状差异在 JS 里翻译成 FeedPeek 桥的扁平行式（`timeout` 秒转毫秒、`body` 转 `bodyText`/`bodyBase64`、`finalUrl` 转 `url`、`status` 同时给 `status` 与 `statusCode`），并补一个 `console` 转发到宿主日志。`Host.session.getCookieHeader` 先返回空串（插件自带匿名回退），`cookieInject` / `signing` 未实现时抛 `HOST_FEATURE_UNSUPPORTED`，让失败可诊断。
2. **适配脚本**：在插件 entry 执行之后注入，做三件事：
   - 校验 `globalThis.LiveParsePlugin` 存在且实现了 `getRoomDetail`；
   - 定义 `globalThis.FeedPeekPlugin = { apiVersion, handle(request) }`，把 FeedPeek 方法映射到 LiveParse 方法：`plugin.handshake`、`plugin.getStatus`、`source.create`（本地合成，不发网络请求）、`feed.getLive`、`auth.begin|complete|validate|clear`；
   - 把 `LiveModel` 映射成 `PluginLiveItem`（字段对应见下表），并做 URL 归一化。

| LiveModel | PluginLiveItem | 处理 |
|---|---|---|
| `roomId` | `CreatorKey` | 必填；同页内唯一由宿主校验 |
| `userName` | `DisplayName` | 空则回退 `"房间 <roomId>"` |
| `roomTitle` | `Title` | 可空 |
| `userHeadImg` | `AvatarUrl` | 归一化：`//x` 补 `https:`，`http://x` 升为 `https://x`，非 https 则置空 |
| `roomCover` | `CoverUrl` | 同上（关注页当前只渲染头像，封面留给后续） |
| `liveWatchedCount` | `ViewerCount` | 解析 `"1.2万"` 得 `12000`；无法解析则 `null`，不猜 |
| `roomId`（插件没返回时用用户填的那个）加 `runtimeOptions.roomUrlTemplate` | `Url` | 宿主校验必须是 http(s) |
| — | `StartedAt` | 无数据，恒为 `null` |

`auth.*` 的映射：`auth.begin` 直接返回 `{ state: "webviewRequired" }`（登录窗口由 FeedPeek 的 WebView2 负责）；`auth.complete` 先调用插件的 `setCredential`/`setCookie` 再探测状态；`auth.validate` 调 `validateCredential`/`getCredentialStatus`，把 `valid` 归一成 FeedPeek 要求的 `authenticated`；`auth.clear` 调 `clearCredential`/`clearCookie`。

### 4.4 来源模型：一个来源 = 若干钉住的直播间

> 本节在 M2 期间按用户要求改过一次。原方案是「一个来源 = 一个分区」，用 `getRooms({ id: categoryId })`；**该方案已废弃**，因为 `getRooms` 返回的是**平台该分区的热门房间**（斗鱼分区 1 = 英雄联盟，返回的是全站该分区榜单），不是用户自己想看的房间。

现在的模型是**钉具体直播间**，同样复用已有的 manifest 来源表单，不新增 UI：

1. 转换器在 `sourceConfiguration.fields` 里放一个必填的 `roomIds`（`kind: "text"`）；
2. 用户在「设置 → 来源 → 插件详情 → 添加来源」里填房间号，可以填多个（逗号/空格/换行分隔），也可以直接粘贴直播间链接（`https://www.douyu.com/74751`）——解析规则是「取每段里最后一个路径段的第一串数字」，并自动去重、最多 20 个；
3. 这些选项随 `source.create` 记进 `SourceDefinition.Options`，再复制到每条 `FeedSubscription.Options`；
4. 拉取时 `feed.getLive` 的 payload 带 `context.options.roomIds`，适配脚本**对每个房间并行调用 `getRoomDetail({ roomId })`**，把返回的 `LiveModel` 映射成卡片。

得到的语义是「**这个来源 = 我钉住的这几个直播间**」。多个直播间 = 一个来源；分组（例如「常看的主播」和「赛事房间」）就添加多个来源。

**只显示正式开播的房间**（用户明确要求：轮播与未开播都不显示）：

- 判定依据是 `getRoomDetail` 返回的 `liveState`，接受值由转换器写进 `runtimeOptions.liveStates`，适配脚本据此过滤（值不在集合里就丢弃）。
- 转换器写入的集合是 `"1"`。这个值不是猜的——逐平台核对过三份插件的状态映射，它们对「正式开播」的定义完全一致，且都**不会**把轮播映射成 `"1"`：

| 插件 | 正式开播 | 轮播 | 未开播 / 未知 | 出处 |
|---|---|---|---|---|
| 斗鱼 | `show_status===1 && videoLoop===0` → `"1"` | → `"2"` 或 `"0"` | → `"0"` | `douyu/index.js` `_douyu_getRoomDetail` |
| 哔哩哔哩 | `live_status===1` → `"1"` | `live_status===2` → `"0"` | → `"0"` / `"3"` | `bilibili/index.js` `_bili_getLiveStateString` |
| 抖音 | `status===2` 且有可播流 → `"1"` | — | → `"0"` / `"3"` | `douyin/index.js` `_dy_statusToLiveState` |

- 副作用（已在 6.6 实测确认）：用户钉的 `74751` 当前是轮播，会被过滤掉；换成真在播的房间则正常显示。
- 允许值是可调的：想放宽就改转换器写入的 `liveStates`（例如 `"1,2"`），不需要改方言或适配脚本。
- 抖音的 `"1"` 还要求「有可播流」，所以签名不可用时（M0 已确认首选签名返回空串）抖音房间可能被判为不在播而全部隐藏——这是保守方向，不会误报。

另一个取舍：

- **单个房间失败不拖垮整条直播区**：逐房间 try/catch，失败的跳过；只有当**所有**房间都失败时才把第一个错误抛上去，让界面能给出提示。

`getCategories` / `getRooms` / `search` 都不再使用（插件里仍然存在，只是不调用）。

### 4.5 FeedPeek 侧需要改动的地方

| # | 改动 | 位置 | 说明 |
|---|---|---|---|
| 1 | 清单新增可选 `runtime` 与 `runtimeOptions` 并纳入校验 | `PluginManifest`、`PluginManifestRules`、`PluginManifestLoader` | `runtime` 缺省即 `feedpeek-v1`，不影响现有插件；`liveparse-v1` 额外要求声明 `feed.live` 且存在 `kind: live` 的 view |
| 2 | 按 `runtime` 选择 bootstrap，并在 entry 之后注入适配脚本 | `JintPluginTransport` | 原生路径行为不变 |
| 3 | LiveParse 形状的 `Host` 与 `console` | `Execution/LiveParseDialect.cs`（宿主自带脚本） | 见 4.3 |
| 4 | 放开「没有 updates 订阅就早退」 | `FeedPeekPanelSession.GetFollowingExtrasAsync` | 现状：没有任何 `feed.updates` 订阅时直接返回空，纯直播来源永远不会出现在直播区 |
| 5 | 安装时自动转换 | `PluginRuntime/Manifest/LiveParsePackageConverter.cs` + `PluginInstallationService.InstallAsync` | 用户直接选原始 LiveParse 文件夹安装：检测到 `manifest.json` 且没有 `plugin.json` 时，在临时目录里产出 FeedPeek 包再走原有安装流程。**不需要额外工具**（原方案的 `tools/` 脚本已取消） |
| 6 | （可选）直播卡片展示封面与观看数 | `FeedLiveItemRow` | 现在只渲染头像、名称、标题；`PluginLiveItem` 里已经有 `CoverUrl`/`ViewerCount` |

> 第 4 条是本计划里唯一会改变现有产品行为的改动；如果不做，纯直播来源不会显示任何内容。第 6 条纯属体验增强，可以先不做。

## 5. 逐平台落地数据

host 清单来自对各包 `index.js` 与登录 preload 中 URL 字面量的静态提取，仅含插件主动请求的域名（API 响应里返回的图片 CDN 不计入 —— 那些 URL 由 WinUI 直接联网，见第 6 节风险 5）。

### 5.1 斗鱼 `douyu-2.0.5`（先做这一个）

| 项 | 值 |
|---|---|
| 转换后 pluginId | `liveparse.douyu` |
| runtime | `liveparse-v1`，`roomUrlTemplate: https://www.douyu.com/{roomId}` |
| 保留 preload | 无（原包只有 danmaku preload，裁剪后为空） |
| auth | 不需要（原 `auth.required: false`） |
| allowedHosts | `www.douyu.com`、`m.douyu.com`、`apic.douyucdn.cn` |
| 需要的 LiveParse 方法 | `getRoomDetail`（每个钉住的房间一次） |
| 已知坑 | 房间列表里 `liveState` 可能为空串；封面/头像字段来自 `rs16_avif`/`avif`，同样是协议相对或 http，需要归一化 |

### 5.2 哔哩哔哩 `bilibili-2.0.0`

| 项 | 值 |
|---|---|
| 转换后 pluginId | `liveparse.bilibili` |
| runtime | `liveparse-v1`，`roomUrlTemplate: https://live.bilibili.com/{roomId}` |
| 保留 preload | `lp_plugin_bilibili_1.0.11_login.js`（裁掉 fflate 与 danmaku） |
| auth | 必需。`cookieDomains` 必须展开为 `api.bilibili.com`、`api.live.bilibili.com`、`live.bilibili.com`、`www.bilibili.com`、`passport.bilibili.com`；`successCookieNames: ["SESSDATA","bili_jct","DedeUserID"]` |
| loginFlow | `loginUrl` 取原值；`successUrlPrefixes: ["https://live.bilibili.com/p/eden/area-tags"]`（由原 `successURLKeyword` 合成） |
| allowedHosts | 上面的 cookieDomains 加 `s1.hdslb.com` |
| 示例可钉房间 | `74751`（超级小桀），M2 已实测通过 |
| 已知坑 | ① `getRooms` 硬编码 `authMode: "platform_cookie"`，未登录会返回 `-352`（原包 changelog 明确写了「列表返回352」）；② 房间列表分支的 `cover`/`face` 未补 `https:`，而 `search` 分支补了，说明字段值确实是协议相对 —— 归一化必须做；③ 需要 `Host.crypto.md5`（FeedPeek 已有）与 wbi 签名，签名在插件内自实现，宿主无需额外能力 |

### 5.3 抖音 `douyin-2.0.5`（风险最高）

| 项 | 值 |
|---|---|
| 转换后 pluginId | `liveparse.douyin` |
| runtime | `liveparse-v1`，`roomUrlTemplate: https://live.douyin.com/{roomId}` |
| 保留 preload | `lp_plugin_douyin_1.0.13_login.js`；**先裁掉** `abogus_bdms_env/sdk/bind.js`、`webmssdk.js`、`fflate`、`danmaku` |
| auth | 必需。`cookieDomains` / `allowedHosts` 取 `www.douyin.com`、`live.douyin.com`、`webcast.amemv.com`、`ttwid.bytedance.com` |
| loginFlow | 原包**没有** `successURLKeyword`，需要合成：`successUrlPrefixes: ["https://www.douyin.com/"]`（`loginURL` 的 origin），`successCookieNames: ["sessionid","sessionid_ss","sid_guard"]` |
| 已知坑 | ① 房间列表要 `a_bogus`，优选实现是 `globalThis.__lp_douyin_abogus_sign`（由 abogus/webmssdk 提供），回退是插件自带的纯 JS `sign_datail`，但原包 changelog 写着「classic 164-char sign soft-fails empty data」，即分类/房间列表可能拿到空数据；② 登录 preload 会调 `Host.session.getCookieHeader`，方言里先返回空串，走插件的匿名回退；③ 485KB 的 `webmssdk.js` 若必须保留，需实测 Jint 的语句/内存预算与 `eval`/`require` 分支 |

## 6. 风险与缓解

| # | 风险 | 影响 | 缓解 |
|---|---|---|---|
| 1 | 抖音 `a_bogus` 在 Jint 里跑不起来 | 抖音可能只能拿到空房间列表 | 先用 0.5 天做「Jint 直接加载 webmssdk/abogus 并调用 `sign_datail`」的验证；不行就放弃抖音，或改用单个房间详情接口，或把签名下沉到宿主 C# |
| 2 | 严格模式 + UMD 包 | fflate 等加载期抛错导致插件初始化失败 | 转换器直接裁掉这些 preload（本计划只需要登录 preload），规避而非适配 |
| 3 | Jint 语句/内存预算 | 大 SDK 触发 `PLUGIN_BUDGET_EXCEEDED` / `PLUGIN_MEMORY_LIMIT` | 裁掉 SDK 后基本消失；如仍需，为方言单独放宽 `PluginRuntimeOptions`（不全局放宽） |
| 4 | 平台风控（bilibili `-352`） | 直播列表为空 | 引导用户先登录；失败时把插件错误码透传到界面提示 |
| 5 | 插件返回的图片 URL 由 WinUI 直接联网 | 图片请求不经过 `allowedHosts` 校验 | 现状即如此（FeedPeek 没有图片代理）。接受，或在后续计划里补图片代理 |
| 6 | 合规：三套包均无 LICENSE/README，且内含平台签名实现 | 无法对外分发 | 仅本机自用可接受；若要分发必须先取得授权，或改为 FeedPeek 原生插件重写接口调用（仍无法回避签名代码的授权问题） |
| 7 | 插件升级导致适配失效 | 新版本方法签名变化 | 适配脚本由宿主提供，插件升级只影响字段映射；加一条契约测试即可覆盖 |

## 6.5 M0 前置验证：实测结论

已按原定方式跑通 M0：把三套真实包用转换器转成 FeedPeek 包，再经 `PluginManifestLoader` + `JintPluginTransport` 实际加载并调用。探针是可选执行的（设置 `FEEDPEEK_LIVEPARSE_REFERENCES` 才运行，否则空跑，CI 不受外部路径影响）。

### 6.5.1 阶段 A：加载、初始化、方法派发（三个包全部通过）

```text
douyu-2.0.5:    liveparse.douyu    | hosts=3 | kept=(none) | dropped=1 | auth=none     | manifest=ok | init=ok(5ms)  | handshake=ok | source.create=ok | getLive=UPSTREAM: room list code invalid: undefined
bilibili-2.0.0: liveparse.bilibili | hosts=6 | kept=lp_plugin_bilibili_1.0.11_login.js | dropped=2 | auth=required | manifest=ok | init=ok(7ms)  | handshake=ok | source.create=ok | getLive=UPSTREAM: apiError code=undefined msg=
douyin-2.0.5:   liveparse.douyin   | hosts=4 | kept=lp_plugin_douyin_1.0.13_login.js   | dropped=6 | auth=required | manifest=ok | init=ok(12ms) | handshake=ok | source.create=ok | getLive=BLOCKED: douyin room list empty or blocked
```

（`getLive` 的失败是探针自带的 HTTP 桩固定返回 `{}` 所致，不是缺陷；关键在于**错误码由插件自己抛出**，说明调用链已经完整走到插件内部。）

结论：

1. **裁剪 preload 后，三个真实 `index.js` 都能在 Jint 4.16.1 加 `Strict()` 下解析并执行完成**，包括 112KB 的抖音版本。
2. **初始化耗时 5–12ms**，`MaxStatements` 与内存预算完全不是瓶颈——原计划里对「大文件 + 严格模式」的担心（风险 2、3）可以划掉。
3. 方言层对真实插件成立：适配脚本能识别 `LiveParsePlugin` 并包装成 `FeedPeekPlugin.handle`，`handshake` 与 `source.create` 都返回预期结构。
4. 转换器产出的清单能通过 FeedPeek 的完整校验（host 白名单、auth、views、sourceConfiguration、runtimeOptions）。

### 6.5.2 阶段 B：抖音签名（结论偏负面，但没死）

```text
[bare]        abogus_bdms_env.js=ok(19ms) | abogus_bdms_sdk.js=FAILED JavaScriptException: undefined is not a function
              abogus_bdms_bind.js=ok(0ms) | webmssdk.js=ok(92ms) | index.js=ok(10ms)
              typeof __lp_douyin_abogus_sign -> function | typeof sign_datail -> function
              sign_datail() -> len=164 (24ms) | __lp_douyin_abogus_sign() -> len=0 (0ms)
[self+window] abogus_bdms_sdk.js=FAILED（补 self/window 别名无效）
              sign_datail() -> len=168 | __lp_douyin_abogus_sign() -> len=0
```

结论：

1. `abogus_bdms_sdk.js`（147KB 混淆包）**在 Jint 中加载即抛错**；补 `self` / `window` 别名无效，说明它依赖更深层的浏览器能力。
2. 因此**首选签名 `__lp_douyin_abogus_sign` 虽然 `typeof` 是 function，实际调用返回空串（len=0）**，即不可用。
3. 插件自带的纯 JS 回退 `sign_datail()` **可调用，且能产出 164/168 字符的 `a_bogus`**，但按插件自己的 changelog，这个 classic 签名对分类/房间列表会「soft-fails empty data」。
4. 所以抖音能否真正拉到房间列表，**必须联网加真实登录态实测**才能定论，离线 M0 给不出结论。这直接决定 M4 是 3 天还是「换方案」（改用单房间详情接口，或把签名下沉到宿主侧）。

### 6.5.3 对计划的影响

| 原假设 | M0 后的修正 |
|---|---|
| 风险 2（严格模式 + UMD 包） | **消除**：裁掉非登录 preload 后没有 UMD 包进入引擎 |
| 风险 3（语句 / 内存预算） | **基本消除**：实测初始化 5–12ms |
| 风险 1（抖音签名） | **确认存在**，且比预估更具体：首选签名返回空串，只剩 classic 回退 |
| M1 工期 | 不变（3–4 天）；其中转换器由独立脚本改为安装时转换，省掉一个外部工具 |
| M4 工期 | 维持 3–5 天，但**第一步应是联网实测**，而不是继续写代码 |

## 6.6 M2 联网实测：斗鱼钉住直播间

探针自动从斗鱼分区榜单里挑一个**当前真在播**的房间（`show_status===1 && videoLoop===0`），再把它与用户问过的 `74751`（轮播）一起钉上，验证过滤行为。走完整链路（转换 → 加载 → `feed.getLive` → 真实 HTTPS → `getRoomDetail` → 映射加过滤 → FeedPeek 自己的校验层 `PluginCreatorService.GetLiveAsync`）。开关：`FEEDPEEK_LIVEPARSE_REFERENCES` + `FEEDPEEK_LIVEPARSE_NETWORK=1`。

```text
converted pluginId=liveparse.douyu
allowedHosts=apic.douyucdn.cn,m.douyu.com,www.douyu.com
liveStates=1
handshake=ok
candidates=15
broadcasting room=475252
pinned=74751,475252
FeedPeek validation=ok(1 live items)
  - 孙悟空丨兰林汉 | 兰林汉：猴来！一元粉丝牌！双倍亲密度~ | https://www.douyu.com/475252
    avatar=https://apic.douyucdn.cn/upload/avatar_v3/202004/31d1d439343b44ff91673fd4dce083b8_big.jpg
kept=475252
74751 kept=no
475252 kept=yes
verdict=PASS
```

结论：

1. **斗鱼数据链路端到端打通**，真实房间数据能通过 FeedPeek 的校验层（URL 必须是 http(s)、`creatorKey` 唯一、名称非空）。
2. **「只显示正式开播」已验证**：同一个来源里钉了轮播房间与在播房间，最终只留下在播的那个（`verdict=PASS`）。
3. 期间发现并修掉一个真实缺陷：**斗鱼插件用扁平调用形状** `Host.http.request({ url, method, timeout })`，而登录 preload 用包装形状 `{ platformId, authMode, request: {...} }`。方言最初只认包装形状，导致 `url` 被解析成空串并报 `HTTP_URL_INVALID`。现在两种形状都支持——这正是 M2 存在的意义。
4. `viewers=0`：`betard` 详情接口里没有 `room_biz_all.hot`，插件回退成 `"0"`。直播卡片当前不渲染观看数，暂无影响；将来要展示需要区分「0」与「未知」。
5. 仍缺 **UI 手工验收**（7.2）：需要在真实应用里安装插件、添加来源、打开主面板看到卡片。目前验证到数据层为止。

## 6.7 与原宿主 AngelLive 的契约对照

参考实现：`C:\Users\Q\Data\Projects\AngelLive`（Swift，插件运行时在 `Shared/AngelLiveCore/Sources/AngelLiveCore/LiveParse/`，核心是 `Plugin/JSRuntime.swift`）。方言已按这份真实实现逐项对齐，而不是按插件文档猜。

### 6.7.1 已对齐

| 维度 | AngelLive 的真实行为 | FeedPeek 现状 |
|---|---|---|
| JS 引擎与并发 | JavaScriptCore；每个 pluginId 一个 `JSRuntime`，内部 `DispatchQueue` 串行（同插件串行、跨插件并行） | Jint；每插件一个 `PluginRuntimeCatalog` 条目加 `SemaphoreSlim(1)`，语义等价 |
| 浏览器环境 shim | 注入 `document` / `window` / `navigator`（带固定 UA）与 `btoa` / `atob` polyfill，供依赖浏览器的第三方脚本使用 | ✅ 按同样方式注入（另加 `self`；`self` 是 UMD 包的常见探测目标） |
| 日志 | 只有 `console.log` / `console.error`，单参数；敏感期静默 | ✅ `.log/.error` 接到宿主日志 sink，另加 `.info/.warn/.debug` 别名 |
| 错误协议 | `Host.makeError` 返回 `new Error("LP_PLUGIN_ERROR:" + JSON.stringify({code,message,context}))`，context 值强制 `String()` 且丢弃 null/undefined | ✅ 逐字复刻，调用脚本按同一 marker 解析 |
| `Host.http.request` 入参 | `options.request ?? options` 双形状；`timeoutMs` → `timeout` → `options.timeoutMs` → `options.timeout`，默认 20s，clamp `[0.1,120]`；`bodyBase64` 优先于 `body` | ✅ 复刻 |
| `Host.http.request` 出参 | `status / headers / setCookies / url / bodyText(可 null) / bodyBase64(恒有)` | ✅ 复刻（C# 侧为文本响应补算 base64） |
| 凭据请求的头处理 | `platform_cookie` 时**剥离**插件自带的 `Cookie` / `Authorization`，由宿主注入，而不是报错 | ✅ 复刻（此前 FeedPeek 会因保留头直接报 `HTTP_HEADER_FORBIDDEN`） |
| 凭据域判定 | `loginFlow.cookieDomains` ∪ `host(loginFlow.loginURL)`，且 **`host == domain \|\| host.hasSuffix("." + domain)`** 后缀匹配 | ✅ `PluginHttpBridge.IsCredentialDomain` 改为后缀匹配；请求路由仍用精确 `allowedHosts` |
| `Host.session.getCookieHeader` | 同步；只能读自己平台的凭据，请求别的 platformId 返回 `""` | ✅ 通过 `IPluginCookieProvider` 在调用前预取 + 平台 id 归属校验（`liveparse.douyin` 也接受 `douyin`） |
| `cookieInject` | 规则解析完整（header/query/body、prefix），但**实现只应用 `target=header`**，query/body 直接跳过 | ✅ 同样只应用 header（与真实实现一致，不擅自"补齐"） |
| 请求签名 | **宿主没有签名能力**；签名完全由插件用 `Host.crypto.md5` + `Host.session.getCookieHeader` 自己拼 | ✅ 不实现（实现了反而会和插件内置的 a_bogus/wbi 冲突） |
| 导出与调用 | 只认 `globalThis.LiveParsePlugin`；**只传一个对象参数**；`apiVersion` 读 JS 导出属性并严格等于 1；`preloadScripts` 按序先执行 | ✅ 复刻（适配脚本额外校验 `LiveParsePlugin.apiVersion`） |
| v1 方法名回退 | 仅当错误是 `Missing function` 时才用 v1 名重试 | ✅ `getRoomDetail` 缺失时回退 `getLiveLastestInfo` |
| `Host.crypto` | `md5`（小写 hex）、`base64Decode`（先百分号解码、再 base64、再 UTF-8，失败返回 `""` 不抛错） | ✅ 复刻 |
| 执行预算 | **完全没有**：无超时、无语句/内存上限 | 方言单独放宽到 60s 调用超时、500M statements、256MB（原生契约仍保持 10s/250k/64MB） |

### 6.7.2 有意保留的差异

| 项 | AngelLive | FeedPeek | 原因 |
|---|---|---|---|
| `Host.capabilities` | 全 `true`（`loginTransaction` / `loginChallengeProtocol=2` / `loginChallengePush` / `loginChallengeBootstrap` / `credentialExposure` / `webSocketPlatformCookie`） | 只把 `credentialExposure` 置 `true`，其余 `false` | 这三个目标插件**一次都没读** `Host.capabilities`；谎报 `true` 会让插件跳过自己的回退路径并撞上我们没实现的能力 |
| `Host.ws` / `Host.nativeStream` / `Host.stream` | WebSocket 桥与原生播放流解析 | 占位函数，调用即 reject `UNSUPPORTED` | 本计划不做弹幕与播放（见第 0 节） |
| 登录事务 / 登录挑战 | `login_transaction`、`seedTransactionCookies`、6 个 `loginChallenge*` 方法 | 未实现（`UNSUPPORTED`） | 本计划只需要 WebView 抓 cookie 的 `webview-cookie` 流程 |
| 错误的文本兜底映射 | 无 marker 时按关键词猜测（`requires cookie` → `AUTH_REQUIRED` 等） | 未实现，未知错误统一 `PLUGIN_ERROR` | 目标插件都走 `Host.raise`，即都带 marker；作为 TODO 记录 |
| `source` 语义 | **source = 远程插件索引 URL**；宿主内没有"钉住房间"的 source 概念（最接近的是首页 section 里的房间卡片） | Source = 用户配置的来源（本计划 = 钉住的直播间） | FeedPeek 的 Source 本来就是"用户配置的数据来源"，与插件契约无关；插件侧只看到 `context.options.roomIds` |

### 6.7.3 liveState 的权威定义

`LiveModel.swift:275-280`：`close = "0"`（下播）、`live = "1"`（直播中）、`video = "2"`（录播/轮播）、`unknow = "3"`。

- 房间类接口（`getRooms` / `search` / `getRoomDetail` / `resolveShare`）**不做同义词归一**，插件返回什么就存什么；只有 `getLiveState` 会把 `live/on/true/streaming/playing` 等归一成 `"1"`。
- 宿主的通用判定是 `PlatformHostBehavior.isPlayableRoom`：`liveState ?? "3"` 精确匹配 `hostBehavior.playableLiveStates`，**未声明时默认 `["1"]`**；斗鱼插件声明的是 `["1","2"]`。
- 本计划要求"只显示正式开播"，所以转换器写死 `liveStates = "1"` —— 比斗鱼的 `playableLiveStates` 更严，正好把 `video = "2"`（轮播）也排除掉，与 `docs` 里 4.4 的取舍一致。

## 6.8 M4 联网实测：抖音

钉一个房间走完整链路。开关：`FEEDPEEK_LIVEPARSE_REFERENCES` + `FEEDPEEK_LIVEPARSE_NETWORK=1`，可用 `FEEDPEEK_LIVEPARSE_DOUYIN_ROOM` 换房间号。

```text
roomId=7247041636524377637
pluginId=liveparse.douyin | kept=lp_plugin_douyin_1.0.13_login.js | dropped=6
handshake=ok
getLive=ok(0 rooms)
requests=5
  POST https://ttwid.bytedance.com/ttwid/union/register/                -> 200 {"status_code":0,"message":"union register success",...}
  GET  https://live.douyin.com/webcast/room/web/enter/?aid=6383&...      -> 200 body=(空)
  GET  https://live.douyin.com/webcast/room/web/enter/?aid=6383&...      -> 200 body=(空)
  GET  https://live.douyin.com/webcast/room/web/enter/?aid=6383&...      -> 200 body=(空)
  GET  https://live.douyin.com/7247041636524377637                       -> 200 body=<!DOCTYPE html>...（约 920KB）
```

结论：

1. **链路是通的**：抖音插件能匿名注册 ttwid、能发签名接口、能走 HTML 兜底，宿主侧全程不再阻塞。
2. **签名仍是硬阻塞**：`webcast/room/web/enter` 三次都是 200 但 **body 为空**——正是插件 changelog 写的「classic 164-char sign soft-fails empty data」。要用真实签名就必须让 `abogus_bdms_sdk.js` 能在 Jint 里跑。
3. **HTML 兜底是可行的，而且是当前唯一能出数据的那条路**（换成真实房间号后实测，见 6.8.1）。最初 0 房间的原因是房间号无效，不是解析失效——这是本轮的一个教训，见 6.8.2。
4. Jint 缺失的浏览器 API 已定位：`TextEncoder / TextDecoder / URL / URLSearchParams / performance / crypto / queueMicrotask / setTimeout / setInterval / localStorage / XMLHttpRequest / fetch / WebAssembly / structuredClone`；`abogus_bdms_sdk.js` 在 `line 2 col 135941` 调用其中之一，抛 `undefined is not a function`。

期间修掉两个真实宿主缺陷（对其它插件同样重要）：

| 缺陷 | 现象 | 修法 |
|---|---|---|
| 保留头策略过严 | 插件在**匿名**模式下自带 `Cookie` 头（抖音用它带自己注册的 ttwid），FeedPeek 直接报 `HTTP_HEADER_FORBIDDEN`，三次请求全失败 | liveparse 方言允许 `Cookie`/`Cookie2`/`Authorization`；凭据模式下的剥离由方言在调用前完成，与 AngelLive 一致 |
| 方言覆盖没生效 | `JintPluginTransport` 构造 `Engine` 的 lambda 闭包捕获的是**构造参数** `options`，导致 6.7 里给方言放宽的超时与语句预算全部被忽略，抖音解析 920KB HTML 时爆 `PLUGIN_BUDGET_EXCEEDED` | 改用生效后的 `_options`，并把方言语句预算提到 500M、调用超时 60s（时间仍由调用超时兜底） |

### 6.8.1 换用真实房间号后：通过

用户提供的真实直播间 `https://live.douyin.com/180109685512`，同样的链路、同样的 5 个请求：

```text
roomId=180109685512 | handshake=ok
FeedPeek validation=ok(1 live items)
  - 城阳电工 | 城阳电工小号正在直播 | https://live.douyin.com/180109685512
    avatar=https://p26.douyinpic.com/aweme/100x100/aweme-avatar/tos-cn-i-0813c000-ce_oITHEncohBA8oYAOAlixEe1ABAdwcCEIIMfqEi.jpeg?from=3067671334
```

要点：

- **抖音这条线可以用了**：数据是插件从直播间 HTML 里解析出来的（`webcast/room/web/enter` 三次仍然 200 空 body，说明签名路径没有被用到）。
- 结果是走 **FeedPeek 自己的校验层**（`PluginCreatorService.GetLiveAsync`）拿到的：URL 形态、`creatorKey` 唯一性、必填名称全部通过。
- 插件返回的 `liveState` 是 `"1"`，因此它也顺利通过「只显示正式开播」的过滤（4.4）。

### 6.8.2 教训：验证必须用真实房间号

第一轮用的 `7247041636524377637` 是**从插件源码里抄来的示例常量**，并不是一个有效的直播间。基于它得出的「HTML 兜底已失效」是**错误结论**——真相是房间号无效导致解析结果为空。

因此：任何关于「插件能不能取到数据」的判断，都必须在**用户提供的真实房间**上做；示例常量、文档里的演示 id 都不能作为证据。

### 6.8.3 仍未闭合的部分

- **签名路径没有被验证过**：`webcast/room/web/enter` 一直返回空 body，所以「ABogus SDK 能不能在 Jint 里跑」对当前功能**没有实际影响**，也没有必要为此投入。
- **匿名 HTML 路径的稳定性未知**：它依赖抖音的页面格式与匿名访问策略，可能随对方改版或限流而失效；也覆盖不了必须登录才能看的房间。
- **每个房间一次约 1MB 的 HTML 请求**：钉多个房间时要并行拉多份大页面。方言已放宽语句预算与超时，但房间数多时仍需关注耗时（当前上限 20 个）。

### 6.8.5 获取直播间与直播状态**不需要登录**

这是用户提出的问题，实测答案是不需要——而且插件自己就是这么设计的：

- `douyin/index.js:1389`：`const authMode = sessionCookie ? "platform_cookie" : "none";`，即**匿名是插件明确支持的路径**。
- 6.8.1 的实测全程没有凭据：`CredentialProfileId = null`，插件用 `authMode: "none"` 自己注册 ttwid（`ttwid.bytedance.com`）并带自己的 Cookie 完成匿名访问。
- 因此「直播间信息 + 直播状态」这一档能力（`getRoomDetail` 的 `liveState`，或 `getLiveState`）不依赖登录态。

登录态真正影响的是：已购/付费房间、搜索、部分分区列表、弹幕，以及匿名被限流时的稳定性。

**转换器原来的行为是错的**：它把原清单的 `auth.required: true` 照抄进 FeedPeek 的清单，而 FeedPeek 用这个标志在**创建来源时**拦截（`FeedPeekPluginRuntime.CreateSourceAsync` 报「此网站来源需要先登录」、插件详情页也提前警告）。结果是：明明匿名就能看，用户却被要求先登录才能添加来源。

现在的规则（见 4.2 的 `auth.required` 行）：对已实测匿名可用的平台写 `false`，但**保留 `loginFlow`**，所以登录入口仍在、随时可登录，只是不再是前置条件。

### 6.8.6 内存预算也是方言必须放宽的一项

第一次跑通后，下一轮同一房间号却报 `PLUGIN_MEMORY_LIMIT`（64MB）——说明抖音这条 HTML 路径**在原生预算下是不稳定的**：解析约 1MB 页面时同时持有原始 HTML、正则中间值与解码后的 JSON。

已把方言的内存上限放宽到 256MB；随后**连续 3 次**同一房间探测全部 `FeedPeek validation=ok(1 live items)`。原生契约仍保持 64MB。
### 6.8.4 决策：不做浏览器 API shim

原来的二选一（补 `TextEncoder`/`URL`/`performance`/`crypto` 等 shim 去救签名 SDK，或放弃抖音）现在**可以取消**：真实房间已经能通过 HTML 路径取到数据，不必再赌那 147KB 的混淆 SDK 能否在 Jint 里加载。

保留一个前提：若某天抖音把直播间数据改成必须走签名接口，再回来考虑 shim（或等插件自己更新）。

## 7. 验收标准




### 7.1 自动化（在 `tests/FeedPeek.Core.Tests`，不新增测试项目）

1. 清单校验：`runtime` 非法值报 `RUNTIME_UNSUPPORTED`；`liveparse-v1` 缺 `feed.live` 或缺 live view 报 `RUNTIME_VIEW_UNSUPPORTED`。
2. 方言装载：LiveParse 形状的 fixture 插件能完成初始化，`plugin.handshake` 返回 `providerId` 与 capabilities。
3. 参数传递：`feed.getLive` 的 `context.options.roomIds` 被解析成去重后的房间号列表，并对每个房间各发一次 `getRoomDetail`；粘贴完整直播间链接、混用逗号/空格/换行都能解析。
4. 形状翻译：LiveParse 的 `request.timeout`（秒）在宿主侧表现为 `timeoutMs`（毫秒），`headers` 原样透传。
5. 字段映射：`LiveModel` 到 `PluginLiveItem` 的 7 个字段映射正确，`1.2万` 解析为 `12000`，协议相对图片 URL 归一为 `https:`。
6. 错误路径：没有可用房间号时报 `SOURCE_OPTION_MISSING`；单个房间失败被跳过、全部失败时插件错误码原样透传到协议错误。
7. 登录链路：`auth.complete` 与 `auth.validate` 在插件返回 `valid` 时归一为 `authenticated`。
8. 回归：现有原生方言（`mock.feed`）的全部用例保持通过。

### 7.2 手工验收（每个平台一次）

1. 用转换器产出包，从「设置 → 来源 → 安装插件」安装，插件卡片显示名称/版本/已启用。
2. 在插件详情页「添加来源」填写直播间 ID（可粘贴链接），来源出现在来源列表。
3. 打开主面板 → 关注页，直播区出现所钉直播间的卡片（头像、名称、标题）。
4. 点击卡片能在浏览器打开正确直播间地址。
5. 需要登录的平台：先「登录」走 WebView2，登录成功后直播列表有数据；「退出」后来源与本地内容被清除。
6. 停用插件后直播区不再显示该来源，且不产生「找不到 Provider」错误。

## 8. 里程碑与工作量

| 阶段 | 内容 | 估算 |
|---|---|---|
| M0 前置验证 | 用 Jint 单独加载三份 `index.js`（按 4.2 裁剪 preload），确认能解析/执行；单独验证抖音 `sign_datail` 是否能返回非空房间 | ✅ 已完成，见 6.5 |
| M1 方言与转换器 | 4.5 的第 1–5 项 + 7.1 全部自动化用例 | ✅ 已完成（7.1 的 8 项全部有对应用例；App 构建 0 警告 0 错误） |
| M2 斗鱼打通 | 转换器数据 + 手工验收 7.2 | ✅ 数据链路已完成并联网实测通过（见 6.6）；仅剩 UI 手工验收 |
| ~~M3 哔哩打通~~ | 用户已有自己的 `feedpeek.bilibili` 插件，**取消** | — |
| M4 抖音打通 | **已联网实测通过（见 6.8.1）**：走插件自带的匿名 HTML 兜底路径，真实房间能出数据并通过 FeedPeek 校验；签名接口未被使用，也不需要 | ✅ 数据链路完成 |
| M5 体验收尾（可选） | 直播卡片显示封面/观看数 | 1 天 |

合计：**M0+M1+M2 约 5–6 天可交付「斗鱼能看」**；三平台全通 10–15 天。

## 9. 未决问题

1. 抖音 `a_bogus` 是否必须在 Jint 内完成？能否把签名放进宿主 C#（那样就不受 Jint 限制，但要复刻 485KB SDK 的算法，成本高）。
2. 用户怎么知道要填哪个房间号？现在是自己粘贴直播间链接或房间号（已支持两种）。是否需要「从链接一键添加」的入口？（当前靠用户自己填来源表单）
3. `Host.session.getCookieHeader` 是否需要真实现？它要求宿主把整条 Cookie 头同步暴露给插件，属于权限放宽；当前先返回空串走插件匿名回退。
4. 是否允许同一插件添加多个来源（例如「常看的主播」与「赛事房间」各一个）？现有模型允许，需要确认产品上是否接受。
5. 合规：这三套包是否可以本机使用/是否可以改造后再分发。

## 附录 A：原型验证结论（工作区改动，未提交）

本计划的实现已在工作区完成（未提交），用于验证「方言 + 钉房间映射」这条主链路。结论：**成立**，并且已用真实斗鱼房间端到端实测。

已改动的文件（如需回退可直接 checkout）：

| 文件 | 改动 |
|---|---|
| `src/FeedPeek.PluginContracts/Manifest/PluginManifest.cs` | 新增可选 `Runtime` 与 `RuntimeOptions` |
| `src/FeedPeek.PluginContracts/Manifest/PluginManifestRules.cs` | 新增 `NativeRuntime` / `LiveParseRuntime` / `SupportedRuntimes` / `ResolveRuntime` |
| `src/FeedPeek.PluginRuntime/Manifest/PluginManifestLoader.cs` | 新增 `ValidateRuntime`（`RUNTIME_UNSUPPORTED`、`RUNTIME_OPTIONS_INVALID`、`RUNTIME_VIEW_UNSUPPORTED`） |
| `src/FeedPeek.PluginRuntime/Execution/LiveParseDialect.cs` | 新增：LiveParse 形状的 bootstrap 脚本 + 适配脚本模板 |
| `src/FeedPeek.PluginRuntime/Execution/JintPluginTransport.cs` | 按 `runtime` 选 bootstrap，entry 之后注入适配脚本 |
| `src/FeedPeek.PluginRuntime/Manifest/LiveParsePackageConverter.cs` | 新增：LiveParse 包到 FeedPeek 包的转换（host 推导、preload 裁剪、auth/views/sourceConfiguration 合成） |
| `src/FeedPeek.App/Services/PluginInstallationService.cs` | 安装时检测 LiveParse 包并先在临时目录转换 |
| `src/FeedPeek.App/Services/FeedPeekPanelSession.cs` | 去掉 `GetFollowingExtrasAsync` 的「无 updates 订阅即早退」 |
| `tests/FeedPeek.Core.Tests/Fixtures/Plugins/mock.live/**` | 新增 LiveParse 形状的 fixture 插件（含 login preload） |
| `tests/FeedPeek.Core.Tests/PluginRuntimeTests.cs` | 新增 3 个用例 + LiveParse HTTP bridge + 上下文辅助方法 |
| `tests/FeedPeek.Core.Tests/LiveParseCompatTests.cs` | 新增：转换器用例 + M0 真实包探针（按环境变量开关） |
| `tests/FeedPeek.Core.Tests/LiveParseSignerProbeTests.cs` | 新增：抖音签名 bundle 探针（按环境变量开关） |
| `tests/FeedPeek.Core.Tests/LiveParseNetworkProbeTests.cs` | 新增：M2 斗鱼联网探针（自动找在播房间 + 轮播对照，按环境变量开关） |
| `src/FeedPeek.PluginRuntime/Host/PluginHostServices.cs` | 新增可选 `IPluginCookieProvider`，支撑同步的 `Host.session.getCookieHeader` |
| `src/FeedPeek.PluginRuntime/Host/PluginHttpBridge.cs` | 凭据域改为后缀匹配（对齐 AngelLive 的 `host == domain \|\| hasSuffix("." + domain)`） |
| `src/FeedPeek.App/Services/FeedPeekPluginRuntime.cs` | 把凭据存储同时作为 cookie 源注入宿主服务 |

验证结果：

- `tests/FeedPeek.Core.Tests`：**33/33 通过**，0 警告 0 错误。
- `src/FeedPeek.App`：x64 Debug **构建成功，0 警告 0 错误**。
- M0 真实包探针：三个包全部转换、加载、初始化、握手成功（见 6.5）。

> 探针是选择性执行的：只在设置了 `FEEDPEEK_LIVEPARSE_REFERENCES`（必要时再加 `FEEDPEEK_LIVEPARSE_DOUYIN_SIGNER=1`）时才会读外部目录，否则直接返回，不影响常规测试。报告写在 `%TEMP%/feedpeek-liveparse-probe.txt` 与 `%TEMP%/feedpeek-liveparse-signer.txt`。

与计划书仍有差异、实现时按计划书为准的三点：

- 适配脚本读取 `context.options.roomIds`（与 4.4 一致）；房间号解析支持链接/逗号/空格/换行、去重、上限 20；
- `Host.http.request` 的 `cookieInject` / `signing` 直接抛 `HOST_FEATURE_UNSUPPORTED`，`Host.session.getCookieHeader` 直接返回空串（与 4.3 一致）；
- 转换器的 host 清单对 `bilibili` / `douyin` / `douyu` 用的是内置审计表，未知插件回退到从脚本字面量提取（计划书 4.2 未写明这一点，此处补记）。

### 附录 A.2 对照 AngelLive 后仍未实现的项（都不影响「钉房间只看在播」）

| 项 | AngelLive 位置 | 本计划是否需要 |
|---|---|---|
| 错误的关键词兜底映射（无 `LP_PLUGIN_ERROR:` 时按文本猜 `AUTH_REQUIRED`/`RATE_LIMITED`/`TIMEOUT`/`BLOCKED`/…） | `LiveParsePluginError.swift:125-162` | 不需要（目标插件都走 `Host.raise`），但复刻成本低，建议补 |
| `Host.ws.*`（WebSocket 桥，弹幕驱动用） | `HostWebSocketBridge.swift` | 不需要（不做弹幕） |
| `Host.nativeStream.*` / `Host.stream.*`（原生播放流解析） | `NativeStreamBridge.swift` | 不需要（不做播放）；注意 AngelLive 自己的 provider 注册表也是空的 |
| 登录事务（`authMode: "login_transaction"`、`seedTransactionCookies`、`LoginTransactionStore`） | `LoginTransactionStore.swift` | 不需要（我们只需要 host 自己的 WebView 抓 cookie 流程） |
| 扫码登录挑战（6 个 `loginChallenge*` 方法 + manifest `loginChallenge`） | `PlatformLoginChallengeService.swift` | 不需要 |
| 设备码 / apiToken / oauth 凭据种类（`credentialKinds`） | `PlatformDeviceAuthCoordinator.swift`、`PlatformAPITokenVault.swift` | 不需要 |
| `getHomeFeed`（插件驱动首页：banners + sections，≤8 区块 × ≤20 房间） | `Home/PluginHomeFeed.swift` | 不需要，但它是 AngelLive 里最接近「一个来源=一组房间」的形态，若以后想做「分区/推荐」可参考 |
| 方法级 v2→v1 回退的其余名字（`getRoomList`/`getPlayArgs`/`searchRooms`/`getRoomInfoFromShareCode`/`getDanmukuArgs`） | `LiveParseJSPlatformManager.swift:344-368` | 本计划只用到 `getRoomDetail`←`getLiveLastestInfo`；其余按需再补 |
| 逗号分隔的 `capabilities` 对象解析（`{ feature: "available" }`）+ 缺失时扫描 JS 文本判能力 | `PlatformCapability.swift:228-289` | 不需要：FeedPeek 的能力由自己转换出的 manifest 决定 |

## 附录 B：结论的数据来源

- LiveParse 插件规范：`C:\Users\Q\Data\Projects\Live-Sources\PluginAuthoringGuide.md`
- 三份插件清单：`references/bilibili-2.0.0/manifest.json`、`references/douyin-2.0.5/manifest.json`、`references/douyu-2.0.5/manifest.json`
- Host API 调用点：各包 `index.js` 与 `lp_plugin_*_login.js`（例如哔哩登录 preload 第 75 行、抖音 `index.js` 第 1308 行）
- host 清单：对三个 `index.js` 与两个 login preload 中 `https://` 字面量的静态提取
- FeedPeek 侧约束：`docs/design/plugin-architecture.md` 与 `src/FeedPeek.PluginRuntime` / `src/FeedPeek.App` 源码
- 原宿主实现（6.7 的对照基准）：`C:\Users\Q\Data\Projects\AngelLive`，重点文件 `Shared/AngelLiveCore/Sources/AngelLiveCore/LiveParse/Plugin/JSRuntime.swift`（Host 表面、错误协议、HTTP 信封、凭据注入）、`LiveParsePluginManifest.swift`（清单字段）、`LiveParsePluginManager.swift`（生命周期/凭据）、`LiveModel.swift:275-280`（`LiveState` 枚举）、`Models/PlatformHostBehavior.swift`（`playableLiveStates`）
