# FeedPeek 迁移 Phase 1：通用项目骨架

> 实施日期：2026-08-29  
> 分支：`codex/feedpeek-migration`  
> 启动项目：`BiliRadar/BiliRadar.csproj`  
> 部署模型：现有 packaged MSIX、x64-only

## 1. 本阶段边界

Phase 1 只建立通用项目、领域模型、JSON 协议和 legacy 兼容接入点。以下内容没有提前实施：

- RSS/Atom 网络请求与 XML 解析。
- SQLite、FeedSession、统一轮询或通用通知。
- 插件发现、manifest 安全校验、Jint 或 Worker。
- 通用页面、FeedItemCard 或默认 UI 路径切换。
- BiliRadar 项目目录、程序集、命名空间和 exe 重命名。

Phase 1 完成后，为避免迁移版与原始 BiliRadar 并行安装/运行时冲突，经用户明确要求追加了名称隔离：迁移版使用 `FeedPeek Preview` 可见名称和 `JordanQD.FeedPeek.Preview` 包身份，并分配独立的托盘 GUID、通知 COM CLSID、单实例键和启动任务 ID。源码命名空间与 `BiliRadar.exe` 暂不改变。新包身份也意味着迁移版使用独立的 packaged 应用数据，不会与原版共享设置和 Cookie。

## 2. 项目与依赖方向

```text
FeedPeek.Core                 无宿主依赖
FeedPeek.PluginContracts      无宿主依赖
FeedPeek.PluginRuntime        → PluginContracts
FeedPeek.Rss                  → Core

BiliRadar                     → Core
                              → PluginContracts
                              → PluginRuntime
                              → Rss
```

`BiliRadar.csproj` 仍是解决方案中唯一带 `Deploy` 的 WinUI 启动项目。四个通用项目均为普通 `net9.0` 类库，不引用 WinUI、Windows App SDK、`ApplicationData` 或 BiliRadar 类型。

## 3. 通用合同

`FeedPeek.Core` 已定义：

- `FeedItem` 与复合身份 `FeedItemKey`。
- `FeedPage`。
- `FeedSubscription`。
- `SourceDefinition`、`FeedDefinition`、`CreatorDescriptor`。
- `FeedPageRequest` 与 `IFeedProvider`。
- 仅用于合同验证的分页 `MockFeedProvider`。

身份层次固定为：

```text
ProviderId + SourceId + FeedId + ItemId
```

核心模型不包含 `aid`、`mid`、直播房间号或 WinUI 类型。

`FeedPeek.PluginContracts` 已定义：

- `PluginRequestEnvelope<TPayload>`。
- `PluginResponseEnvelope<TResult>` 与结构化 `PluginError`。
- `PluginInvocationContext` 和 `FeedPagePayload`。
- `PluginManifest`、认证、登录流程、网络权限和视图描述 DTO。
- `ActionDescriptor`。
- API v1 常量以及 UTF-8/camelCase 兼容的 `PluginJson.SerializerOptions`。

这些类型只描述可 JSON 序列化数据，不向插件传递 C# 服务、WinUI 对象或数据库实体。

## 4. Legacy adapter 与默认路径

`LegacyBiliFeedProvider` 使用 `IBiliDataProvider`，把现有 `BiliVideoUpdate` 映射为 namespaced `FeedItem`：

```text
Id          → ItemId
CreatorMid  → CreatorKey（字符串）
CreatorName → AuthorName
Description → Summary
CoverUrl    → ImageUrl
AvatarUrl   → AuthorImageUrl
Aid/统计/P  → legacy.* metadata
```

适配器的临时身份为：

```text
ProviderId = legacy.biliradar
SourceId   = account-default
FeedId     = updates
```

它沿用 `IBiliDataProvider` 内部的状态化分页；`legacy:next` 只是绑定当前适配器实例的迁移期 opaque cursor，不是未来插件协议的正式游标格式，其他游标会被拒绝。旧接口没有在首页结果中暴露 `has_more`，因此首页非空时会保守地允许一次后续请求；旧 provider 也继续使用其固定页大小，不承诺满足通用请求的 `PageSize`。这些限制必须在 Phase 2 替换状态化分页时消除。

`FeedPeekCompositionRoot` 集中了 legacy provider 创建。现有 `MainPanelSession`、后台通知和通知设置页仍取得同一个 `BiliWebDataProvider` 实现，因此默认运行链路仍是：

```text
Tray/Flyout → MainPanelSession → UpdateMonitorService → BiliWebDataProvider
```

通用 `IFeedProvider` adapter 已可构造和测试，但没有接管 UI、轮询或通知。

## 5. 最小验证范围

本阶段没有重复 Phase 0 的 B 站 JSON、通知和托盘测试。新增验证仅包括：

- mock feed 的 opaque cursor 分页。
- JSON 请求 envelope 的 camelCase 序列化往返。
- `BiliVideoUpdate → FeedItem` 的命名空间和 metadata 映射。
- legacy provider 通过 `IFeedProvider` 返回现有更新并结束分页。
- 完整解决方案构建，确保项目图和 WinUI 宿主引用有效。

验证结果：

- `dotnet build BiliRadar.slnx -p:Platform=x64 --no-restore -m:1`：0 warning，0 error。
- `FeedPeek.Core.Tests`：2/2 通过。
- `FeedPeek.Compatibility.Tests`：14/14 通过（包含 Phase 0 基线）。
- 当前 Debug MSIX 注册路径指向本工作区产物；启动后进程持续存活且响应正常，启动检查窗口内没有新增应用崩溃记录。
- 名称隔离后，Windows 开始菜单同时显示 `BiliRadar` 与 `FeedPeek Preview`，对应两个状态正常、PackageFamilyName 不同的包；迁移版可独立启动并驻留。

## 6. 后续差距

Phase 2 才实现 RSS/Atom、持久化、通用 session/轮询和新 UI。Phase 3 才实现 manifest 校验、Host bridge、Jint、真实插件调用以及对应的 `PluginRuntime` 行为测试项目。当前不得删除 `IBiliDataProvider`、旧模型、三页 UI 或 legacy 通知激活参数。
