# Phase 5：FeedPeek 品牌、包身份与用户数据迁移

更新日期：2026-08-31

## 结论

Phase 5 属于中等工作量。用户可见品牌已在迁移期间部分改为 `FeedPeek Preview`，因此品牌收口较小；主要风险来自包身份、可执行文件/命名空间重命名和跨 identity 数据搬迁。

本阶段采用**独立应用发布**路径：

- FeedPeek 不使用旧 BiliRadar 的 `JordanQD.BiliRadar` identity，不作为 BiliRadar 的原位升级。
- 本地开发继续使用 `JordanQD.FeedPeek.Preview`，正式包使用独立 production identity。当前侧载默认值为 `JordanQD.FeedPeek`；若进入 Microsoft Store，构建时必须替换为 Partner Center 实际分配的 identity。
- 用户可见名称统一为 `FeedPeek`；开发和正式 identity 都不继续暴露为产品名称。
- 保留现有 StartupTask ID、通知 CLSID、Credential Locker resource name、单实例键和数据库文件名，避免已有状态失联。
- 不自动跨包读取旧 `JordanQD.BiliRadar` 或预览包数据。若以后确需导入，应实现用户主动选择的显式导入流程。
- 不自动复制明文 Cookie。旧 Cookie 兼容入口只在用户安装对应网站插件后使用；最终清理必须与用户确认和登录态验证绑定。

## 检查点

### 5.0 身份与数据边界（已完成）

- 当前宿主仍为 packaged WinUI 3 单项目 MSIX。
- 当前开发 identity 为 `JordanQD.FeedPeek.Preview`；正式 FeedPeek 和旧 `JordanQD.BiliRadar` 使用不同 identity，数据目录互不共享。
- 构建属性 `UseProductionPackageIdentity=true` 会选择 `ProductionPackageIdentityName`；Store 发布时必须传入 Partner Center 的精确值。
- Core SQLite 使用 `schema_migrations`，当前 schema version 为 2。
- 插件凭据保存在 Windows Credential Locker，resource name 为 `FeedPeek.PluginCredentialProfile.v1`。
- LocalSettings、`feedpeek.db`、用户插件目录均依赖当前 package identity。

### 5.1 用户可见品牌（已完成）

- Manifest、窗口标题、托盘提示、通知注册名和启动项显示名统一为 `FeedPeek`。
- 关于页和稍后再看提示移除站点专属文案。
- 仓库链接、问题链接和导出文件名改为 FeedPeek。
- 新安装的自定义启动网页默认值改为 FeedPeek 项目页；不覆盖已有用户选择。

### 5.2 版本化本地设置迁移（已完成）

- 使用 `FeedPeek.DataSchemaVersion` 记录宿主 LocalSettings schema。
- v1 迁移把旧换行通知基线、作者通知标识和重复记录通过当前序列化格式重写。
- 版本号最后写入；中断或失败后可在下次启动重试。
- SQLite 和 Credential Locker 继续使用各自独立的版本/安全边界。

### 5.3 宿主工程机械重命名（待完成）

- `BiliRadar.csproj`、输出程序集和 `BiliRadar.exe` 改为 FeedPeek。
- `BiliRadar.*` C#/XAML 默认命名空间改为 `FeedPeek.*`。
- 更新 manifest executable、桌面扩展和构建后的 manifest 修正规则。
- 这是约 60 个文件的机械修改，必须单独提交并验证 XAML 编译、通知激活和开机启动。

### 5.4 正式 identity、兼容键清理与人工验收（待完成）

- 在 Partner Center 保留名称后写入正式 identity 和 publisher；若仅侧载，则使用项目定义的独立 identity 和匹配证书。
- 对已成功迁入 Credential Locker 的旧 Cookie 提供确认后清理。
- 只在确认无需回退后删除旧通知基线键和站点兼容标识。
- 验证正式版新装、重复启动迁移、插件/来源/已读状态、通知、开机启动和单实例行为；不把 BiliRadar 原位升级作为发布门槛。

## 工作量估计

- 5.0–5.2：小到中，已完成代码落地。
- 5.3：中等，主要是 XAML 命名空间和打包入口联动；预计一个独立开发批次。
- 5.4：中等且以人工验证为主；跨 BiliRadar/Preview identity 的自动迁移不纳入当前路径。

剩余代码量不大，但 5.3/5.4 的风险高于行数。额度有限时可以安全停在 5.2；当前应用仍可构建运行，用户可见品牌已经是 FeedPeek，内部 `BiliRadar` 名称不会影响功能。
