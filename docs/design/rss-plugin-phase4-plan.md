# FeedPeek Phase 4：站点插件迁移实施计划

> 状态：4.0–4.1 实施中  
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

