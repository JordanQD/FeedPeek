# FeedPeek Phase 4：站点插件迁移实施计划

> 状态：4.0–4.9 已完成自动验证；4.3–4.4 已人工验收，4.5–4.9 待人工验收
> 基线提交：`9c6dd3e Phase3`  
> 原则：每个检查点均保持可构建、可回滚；兼容矩阵通过前不删除 legacy 路径。

## 1. 当前起点

- 三个 Flyout 页面已经使用 `FeedPeekPanelSession` 和通用 `FeedItemRow`，主窗口、托盘和卡片 UI 不需要重写。
- `FeedPeek.PluginContracts` 与 `FeedPeek.PluginRuntime` 已具备 v1 JSON envelope、Jint Runtime、Host Bridge、发现/禁用及执行预算。
- `BiliWebDataProvider`、WebView 登录、账户资料、直播、关注关系和后台通知仍使用站点专属服务。
- 第一版只加载仓库内置并经项目审核的插件；不把进程内 Jint 当作不可信代码的操作系统沙箱。

## 2. 分段检查点

| 检查点 | 范围 | 独立退出条件 |
|---|---|---|
| 4.0 | 固化 Phase 3；冻结兼容矩阵、依赖顺序和回滚点 | Phase 3 有明确 Git 基线；计划文档可单独审阅 |
| 4.1 | 补齐 auth/source/account/creator/live DTO；增加 transport-neutral 调用客户端、调用授权表和 `IFeedProvider` 适配层 | mock transport 能通过通用 Core 返回分页 Feed；越权上下文与插件错误被拒绝 |
| 4.2 | 创建内置站点 JS 插件；先迁移更新列表与游标分页 | 不接 UI 也能验证更新、分页、异常和取消 |
| 4.3 | 注册插件 Source/Subscription；切换关注页读取路径 | RSS 与站点更新可共存；legacy 路径仍可回退 |
| 4.4 | 迁移历史、稍后再看和 item actions；协调本地/远端状态 | 两页及加入/移除操作达到迁移前效果 |
| 4.5 | manifest 驱动 WebView2 登录；持久凭据/Cookie 代理；账户资料与退出 | 登录、重启保留、失效和退出路径可人工验收 |
| 4.6 | 迁移直播、关注列表、creator actions 与作者展示语义 | 站点更新能稳定取得 creator；卡片来源位显示 UP 主名称/头像；直播与关注/取消关注达到迁移前效果 |
| 4.7 | 后台通知和单作者规则改用通用 creator 标识 | 视频通知显示 UP 主；按 UP 主订阅更新通知、直播通知及自定义作者规则可用 |
| 4.8 | 通用插件接入：用户插件目录、动态 Provider、插件管理、可选插件登录与多来源配置 | 不改宿主代码即可安装/启用另一个合规站点插件；一个网站插件可创建多个来源，需要账户权限时复用该插件的登录凭据 |
| 4.9 | 执行扩展兼容矩阵；升级/回滚/卸载验收；删除 legacy provider、模型和 adapter | 默认路径完全使用通用核心和动态插件注册；构建与人工关键路径通过 |

### 2.1 已完成记录

- 4.0：Phase 3 基线为 `9c6dd3e`；本计划、兼容矩阵与停止规则由 `53bdc49` 固化。
- 4.1：新增 auth/source/account/creator/live v1 DTO；新增 `FeedPeek.PluginHost`，提供强类型 `PluginClient`、Runtime Catalog invoker、授权上下文注册表和 `PluginFeedProvider`。
- `PluginFeedProvider` 已覆盖 updates/history/viewLater 方法路由、opaque cursor、插件结果校验和 `FeedItem` 映射；瞬时直播数据保留独立 `PluginLivePage` 合同，避免丢失直播语义。
- 4.1 时默认 WinUI 运行路径尚未切换；未修改 XAML、RSS 行为或 legacy 服务。
- 4.1 完成时现有测试项目共 22 项通过。
- 4.2：新增仓库维护的内置 `plugins/feedpeek.video-site` JavaScript 插件；从项目自有 legacy 实现重新实现更新解析，没有引入第三方插件代码或品牌资产。
- 站点插件已实现 handshake/status、更新首刷、opaque cursor 分页、archive/PGC/opus 映射、结构化站点错误和取消传播；登录等未实现能力没有提前写入 capability 列表。
- 插件作为内容进入 WinUI Release 输出的 `Plugins/feedpeek.video-site`，但尚未注册 Source/Subscription，也未切换默认 UI 数据路径。
- 4.2 完成时现有测试项目共 25 项通过；使用脱敏 fixture，不以实时站点网络作为自动验证前提。
- 4.3：WinUI composition root 已加载输出目录中的内置站点插件，并将其作为 `feedpeek.video-site` Provider 注册到现有 `FeedPeekPanelSession`。
- 启动通用会话时会持久化内置站点 Source/Subscription；RSS 与站点订阅共享同一 SQLite、轮询与关注页查询。现有 Cookie 存在且插件可用时启用站点订阅，否则保留为禁用状态。
- 4.3 暂以只读 `LegacyPluginCookieProvider` 把原 `CookieStore` 注入 Host HTTP；正式 credential profile、manifest 驱动登录和退出仍严格留在 4.5。
- 关注页继续使用现有 WinUI `ListView`，通过官方 `ISupportIncrementalLoading` 模式接入 opaque cursor 加载更多；Core 新增游标页持久化与 `HasMore` 查询。
- 单个来源刷新失败不再阻断同一会话内其他来源刷新，因此 RSS 与站点更新可以共存；legacy provider 与站点专属服务尚未删除，可按 4.2 提交回退。
- 4.3 完成时现有测试项目共 26 项通过；Debug x64 与 Release x64 均为 0 警告、0 错误。真实账号、滚动分页与 RSS 共存路径待人工验收。
- 4.4：内置站点插件已实现历史记录、稍后再看分页，以及加入/移除稍后再看的 item actions；历史页和稍后再看页继续复用现有 WinUI `ListView`，并接入与关注页相同的增量加载集合。
- 网站稍后再看列表是站点条目的权威状态：远端刷新成功后才重建本地 saved 缓存；远端操作成功后才提交本地状态。远端失败不会提前改变按钮或列表状态。RSS 等非插件条目的 saved 状态仍完全保留在本地。
- 同一站点视频在 updates/history/viewLater 三个 feed 中按 provider/source/item 视为同一逻辑条目；saved 状态同步到所有缓存副本，历史和稍后再看查询按该逻辑键去重。
- 4.4 暂通过只暴露 `bili_jct` 的窄 credential bridge 支持 CSRF 动作；持久 credential profile、登录和退出仍由 4.5 完成。
- 4.4 完成时现有测试项目共 30 项通过；Release x64 为 0 警告、0 错误。真实账号的历史分页、加入/移除和外部状态同步待人工验收。
- 4.5：WebView2 登录窗口改为读取 manifest 的登录 URL、成功 URL、Cookie 域和成功 Cookie；宿主按 `pluginId + credentialProfileId` 将 profile 持久化到 Windows Credential Locker。
- 插件 HTTP 只能向 manifest 声明的 Cookie 域注入 Cookie；插件脚本只能读取 manifest `exposedCookieNames` 显式授权的 Cookie 值，当前站点仅授权动作所需的 `bili_jct`。
- 登录闭环通过 `auth.begin/complete/validate/clear`；账户资料改走 `account.getProfile`。旧设置导入的 Cookie 会迁移到 profile；在 4.7 迁走后台通知前仍同步一份 legacy Cookie，Credential Locker 是插件路径的权威来源。
- 登录入口已从“通用”移到“来源”的网站插件卡片。退出登录会删除该网站来源的 SQLite 缓存并通知已打开的托盘面板刷新；RSS 与其他网站数据不受影响。
- 当前内置网站实现为“一个插件 + 一个可选凭据 profile + 一个聚合来源”；4.8 将来源数量动态化。来源可不绑定凭据；需要账户权限的多个来源共享该插件的凭据。
- 4.5 完成时现有测试项目共 32 项通过，Debug/Release x64 构建均为 0 警告、0 错误；WebView 登录、重启保留、失效与退出待人工验收。
- 4.6：内置站点插件新增 `creator.getFollowing`、`feed.getLive`、`creator.getActions` 与 `creator.executeAction`；关注列表、直播列表和关注/取消关注不再依赖主面板 legacy provider。
- 网站条目的卡片归属位显示 UP 主名称和头像，RSS 条目仍显示 RSS 来源；三个页面均可通过官方 `MenuFlyout` 执行作者主页及关注关系操作。
- 关注页恢复官方 `ItemsRepeater + StackLayout` 横向直播区域，并沿用现有直播区域显示设置；后台通知与自定义作者通知规则仍严格留在 4.7。
- 4.6 完成时现有测试项目共 33 项通过，Debug/Release x64 构建均为 0 警告、0 错误；真实账号关注列表、直播、作者菜单和关注/取消关注待人工验收。
- 4.7：后台监控改为复用通用 Core、RSS Provider 与站点插件 Runtime；更新通知以 `ProviderId + SourceId + FeedId + ItemId` 去重，直播和自定义作者规则以 `ProviderId + SourceId + CreatorKey` 匹配，不再依赖 `mid/aid` 作为宿主领域标识。
- 通知标题使用插件返回的作者名称和头像；网站通知的“稍后再看”激活参数携带通用条目键，再由插件 item action 执行。RSS 等不支持该动作的来源不显示此按钮。
- 通知设置中的旧 `Mid` 规则会在读取时迁移到内置网站的通用作者标识；新增规则通过 `creator.getFollowing` 验证作者。后台通知不再使用 legacy provider，也不再为其镜像新的登录 Cookie。
- 4.7 通知收口后，后台只消费 Core 每轮轮询返回的 `NewItems`，不再扫描全部 SQLite 条目另算新旧；订阅首次成功拉取建立基线，之后的通知基线跨应用重启保留。复合条目/直播键改用 JSON 写入 LocalSettings，避免换行分隔破坏 `ProviderId + SourceId + FeedId + ItemId`。
- RSS 一轮批量更新最多提交 20 条通知，覆盖常见的定时批量发布；通知注册、来源刷新、规则过滤和 App Notification 提交均输出可诊断日志，不再静默吞掉平台异常。
- 4.7 完成时 `FeedPeek.Core.Tests` 33 项、`FeedPeek.Compatibility.Tests` 16 项通过，Debug x64 构建 0 警告、0 错误；真实通知弹出、点击打开、稍后再看和自定义作者筛选待人工验收。
- 4.8：增加应用数据目录下的用户插件安装区、安装记录、包哈希校验和持久启停状态；Runtime 同时发现内置及用户插件，并为每个启用插件动态注册一个 Provider。未经安装记录授权的目录不会运行。
- Manifest 新增宿主渲染的 `sourceConfiguration` 字段合同；来源设置页继续复用 `SettingsCard`、`ListView`、`ContentDialog` 和系统 `FolderPicker`，支持安装、启停、卸载及一个网站插件创建多个来源，不接受插件提供 XAML。
- 插件登录改为按 `pluginId + credentialProfileId` 参数化；每个插件当前使用一个共享 profile。可选登录插件可创建匿名来源，必须登录的插件会在创建来源前拦截；退出登录会移除绑定该 profile 的来源和缓存。
- 内置网站插件已实现 `source.create/get/listFeeds/delete`，版本升至 1.2.0；面板、作者/直播、条目动作与后台通知按动态 Provider/Source 分发。4.8 完成时 `FeedPeek.Core.Tests` 34 项、`FeedPeek.Compatibility.Tests` 16 项均通过，Debug/Release x64 构建 0 警告、0 错误；动态安装与交互路径待人工验收。
- 4.9：删除旧 `IBiliDataProvider`/`BiliWebDataProvider`、legacy adapter、站点专属模型、旧会话/快照、旧通知规划器及孤立 `VideoCard`；默认运行路径只保留 Core、RSS 和动态插件 Provider。
- 停用或损坏的用户插件不会再通过遗留订阅进入前台/后台刷新；通知“稍后再看”按钮改为按 manifest 的 `collection.viewLater + item.actions` 能力显示，不再按内置插件 ID 判断。
- 用户插件升级继续使用 staging/backup 目录切换；卸载先把目录移入隐藏备份，安装记录写入成功后再删除备份，写入失败则恢复原目录。卸载入口同时清除该插件共享凭据、来源和本地缓存。
- 旧 Cookie 和旧作者通知规则仅作为单向数据迁移入口保留，不再承担网络请求、页面或后台通知职责。4.9 完成时 `FeedPeek.Core.Tests` 34 项、精简后的 `FeedPeek.Compatibility.Tests` 4 项通过，Debug/Release x64 构建均为 0 警告、0 错误。

## 3. 4.1 结构边界

4.1 增加独立 `FeedPeek.PluginHost` 层。依赖方向固定为：

```text
FeedPeek.Core              FeedPeek.PluginContracts
          \                 /
           FeedPeek.PluginHost
                    |
          FeedPeek.PluginRuntime
```

- Core 不引用 Jint，也不理解插件动作或站点字段。
- Runtime 不引用 Core，只负责 manifest、transport 与 Host API。
- PluginHost 负责 envelope 调用、DTO 校验、Core 模型映射和调用上下文授权。
- WinUI 宿主在后续检查点组合这些服务；4.1 不修改 XAML 或默认运行路径。

## 4. 兼容矩阵

| 迁移前能力 | 新合同/宿主位置 | 切换检查点 | 4.0 基线 |
|---|---|---:|---|
| 关注作者视频更新 | `feed.getUpdates` / `feed.getMoreUpdates` | 4.2–4.3 | legacy 可用 |
| 最新视频分页 | opaque cursor + `FeedPage` | 4.2–4.3 | legacy 可用 |
| 正在直播 | `feed.getLive` | 4.6 | legacy 可用 |
| 历史记录 | `account.getHistory` / `account.getMoreHistory` | 4.4 | legacy 可用 |
| 稍后再看列表 | `collection.getViewLater` / `collection.getMoreViewLater` | 4.4 | legacy 可用 |
| 加入/移除稍后再看 | `item.getActions` / `item.executeAction` | 4.4 | legacy 可用 |
| 关注/取消关注 | `creator.getActions` / `creator.executeAction` | 4.6 | legacy 可用 |
| WebView 登录 | manifest login flow + Host credential/cookie proxy | 4.5 | legacy 可用 |
| 账户资料 | `account.getProfile` | 4.5 | legacy 可用 |
| 站点条目显示 UP 主 | `creatorKey/authorName/authorImageUrl` + 通用卡片展示策略 | 4.6 | legacy 可用 |
| 关注列表 | `creator.getFollowing` | 4.6 | legacy 可用 |
| 后台与按 UP 主更新通知 | 通用 provider/source/creator identity | 4.7 | 已切换通用路径 |
| 用户插件发现、安装、启停和升级 | 用户插件目录 + 持久插件记录 | 4.8 | 无 |
| 动态 Provider/Source、可选登录与多来源 | manifest source schema + plugin credential profile + 设置向导 | 4.8 | 无 |
| 通用 item/creator action 分发 | capability + action descriptor dispatcher | 4.6–4.8 | 内置站点分支可用 |
| 托盘/Flyout/设置/退出生命周期 | WinUI 宿主 | 全程不改语义 | 已验收基线 |

直播播放与弹幕仍是可选扩展，不作为 Phase 4 退出门槛。

## 5. 控件与 UI 约束

- 4.0–4.5 不重做主面板卡片；4.6 只切换站点条目的作者展示语义，不改变现有卡片布局。
- 来源设置继续复用现有 CommunityToolkit `SettingsCard` 与 WinUI `ListView`。
- 登录继续复用现有 WebView2 窗口，只把 URL、Cookie 域和成功条件改为 manifest 驱动。
- 4.6 若恢复横向直播区域，候选是官方 `ItemsRepeater + StackLayout`；实施前单独向用户确认具体布局。
- 4.8 插件和来源管理复用现有 CommunityToolkit `SettingsCard`、WinUI `ListView` 与系统文件选择器，不自制列表或安装控件。
- 4.8 的关系固定为 `Plugin（网站） 1 → N Source（来源）`；每个插件当前最多一个可选 credential profile，需要登录的来源引用它，无需登录的来源保持空引用。不要把登录重新做成全局设置，也不要在每个来源上复制同一份 Cookie。
- 不新增自制列表、选项卡、卡片框架或新控件依赖。

## 6. 验证和停止规则

- 每个检查点只运行与改动直接相关的必要测试，再进行 Release x64 整体构建。
- 4.1 没有 UI 行为变化，不启动应用；从 4.3 开始由用户执行对应人工交互验收。
- 任何检查点未通过时停在该检查点修复，不提前删除 legacy 代码。
- Git 提交按检查点划分；额度不足时停在最近一个已构建通过的提交。

### 6.1 Phase 4.8 人工验收

1. 打开“设置 → 来源”，确认“已安装插件”中可见内置网站插件，已有 RSS 和网站来源都显示在同一个来源列表中。
2. 对内置网站插件连续执行两次“添加来源”，确认生成两个不同来源；关闭并重新打开设置和托盘面板后，两者仍存在且更新可刷新。
3. 登录网站插件后添加需要登录的来源；退出登录，确认绑定该账户的来源及内容被清除，RSS 和匿名来源不受影响。
4. 用“从文件夹安装插件”选择一个合规测试插件目录；关闭并重新打开设置后确认它可添加来源，停用后重新打开托盘面板不再轮询，重新启用后恢复。
5. 卸载用户插件，确认插件目录、安装记录以及该插件来源被移除；内置插件不显示停用和卸载按钮。
6. 验证新网站来源的更新通知、UP 主名称/头像、直播和稍后再看动作；插件不声明相应能力时，宿主不应伪造对应操作。

### 6.2 Phase 4.9 人工验收

1. 保留现有 RSS、内置网站登录和本地内容，直接运行 4.9 构建；确认三栏、来源列表、历史和稍后再看均能读取原数据。
2. 刷新 RSS 与内置网站来源，确认关注更新、分页、UP 主名称/头像和直播仍可用；关闭 Flyout 时不出现取消错误提示。
3. 对一个用户插件执行“停用 → 关闭并重开设置和托盘面板”，确认旧缓存可保留，但该插件不再刷新、通知或产生“找不到 Provider”错误；重新启用后恢复。
4. 用同一 `pluginId` 的新版本文件夹执行安装，确认升级成功；再用损坏或不合规的版本尝试升级，确认旧版本仍可继续加载。
5. 卸载用户插件，确认插件、来源、缓存和该插件登录状态被清除；重新安装后不应自动恢复旧登录状态。
6. 验证 RSS 通知没有“稍后再看”，声明 `collection.viewLater` 与 `item.actions` 的网站插件通知有该按钮，点击后动作成功。
7. 退出并重新启动应用，确认通知基线、来源与启停状态保持；托盘左键、右键“设置/退出”和设置/登录窗口生命周期无回归。

## 7. 主要风险

- 站点接口和风控可能变化；解析测试应使用脱敏 fixture，不依赖实时网络作为唯一证据。
- 当前全局 Cookie 与通用 credential profile 的迁移必须延后到 4.5，并保持可回滚；不得静默复制或导出明文凭据。
- 远端“稍后再看”与本地 saved 状态必须在 4.4 明确定义一致性，避免按钮成功但列表状态回滚。
- 后台通知已在 4.7 切换到通用实现；4.9 删除 legacy 类型前仍需保留旧设置规则的单向迁移兼容。
