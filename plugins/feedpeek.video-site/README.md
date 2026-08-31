# FeedPeek 内置视频站点适配器

此插件是 Phase 4 的仓库内置、项目维护插件。更新列表解析逻辑由本仓库原有
`BiliWebDataProvider` / `BiliLegacyJsonMapper` 行为重新实现，没有复制来源未知的
第三方插件代码，也不包含站点图标、Logo、截图或其他品牌资产。

当前已实现：

- `plugin.handshake`
- `plugin.getStatus`
- `feed.getUpdates`
- `feed.getMoreUpdates`
- archive、PGC 和 opus 更新条目映射
- opaque cursor、结构化站点错误和取消传播
- manifest 驱动登录、账户资料、历史记录和稍后再看
- `creator.getFollowing`
- `feed.getLive`
- `creator.getActions` / `creator.executeAction`

插件随应用构建输出，并作为当前内置视频站点来源运行。后台通知及自定义作者
通知规则仍在后续 Phase 4.7 迁移。
