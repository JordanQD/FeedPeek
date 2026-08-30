# FeedPeek Phase 4：站点插件迁移实施计划

> 状态：4.0–4.4 已完成自动验证；4.3 已人工验收，4.4 待人工验收
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
| 4.6 | 迁移直播、关注列表与 creator actions | 直播与关注/取消关注达到迁移前效果 |
| 4.7 | 后台通知和单作者规则改用通用标识 | 刷新、视频/直播通知及自定义作者规则可用 |
| 4.8 | 执行兼容矩阵；删除已无调用方的 legacy provider、模型和 adapter | 默认路径完全使用通用核心与站点插件；构建和人工关键路径通过 |

### 2.1 已完成记录

- 4.0：Phase 3 基线为 `9c6dd3e`；本计划、兼容矩阵与停止规则由 `53bdc49` 固化。
- 4.1：新增 auth/source/account/creator/live v1 DTO；新增 `FeedPeek.PluginHost`，提供强类型 `PluginClient`、Runtime Catalog invoker、授权上下文注册表和 `PluginFeedProvider`。
- `PluginFeedProvider` 已覆盖 updates/history/viewLater 方法路由、opaque cursor、插件结果校验和 `FeedItem` 映射；瞬时直播数据保留独立 `PluginLivePage` 合同，避免丢失直播语义。
- 默认 WinUI 运行路径尚未切换；未修改 XAML、RSS 行为或 legacy 服务。
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
| 关注列表 | `creator.getFollowing` | 4.6 | legacy 可用 |
| 后台与单作者通知 | 通用 source/creator identity | 4.7 | legacy 可用 |
| 托盘/Flyout/设置/退出生命周期 | WinUI 宿主 | 全程不改语义 | 已验收基线 |

直播播放与弹幕仍是可选扩展，不作为 Phase 4 退出门槛。

## 5. 控件与 UI 约束

- 4.0–4.5 不重做主面板卡片。
- 来源设置继续复用现有 CommunityToolkit `SettingsCard` 与 WinUI `ListView`。
- 登录继续复用现有 WebView2 窗口，只把 URL、Cookie 域和成功条件改为 manifest 驱动。
- 4.6 若恢复横向直播区域，候选是官方 `ItemsRepeater + StackLayout`；实施前单独向用户确认具体布局。
- 不新增自制列表、选项卡、卡片框架或新控件依赖。

## 6. 验证和停止规则

- 每个检查点只运行与改动直接相关的必要测试，再进行 Release x64 整体构建。
- 4.1 没有 UI 行为变化，不启动应用；从 4.3 开始由用户执行对应人工交互验收。
- 任何检查点未通过时停在该检查点修复，不提前删除 legacy 代码。
- Git 提交按检查点划分；额度不足时停在最近一个已构建通过的提交。

## 7. 主要风险

- 站点接口和风控可能变化；解析测试应使用脱敏 fixture，不依赖实时网络作为唯一证据。
- 当前全局 Cookie 与通用 credential profile 的迁移必须延后到 4.5，并保持可回滚；不得静默复制或导出明文凭据。
- 远端“稍后再看”与本地 saved 状态必须在 4.4 明确定义一致性，避免按钮成功但列表状态回滚。
- 后台通知直到 4.7 仍保留 legacy 实现，不能误判为站点插件已完成全量切换。
