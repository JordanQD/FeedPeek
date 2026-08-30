# FeedPeek 内置视频站点适配器

此插件是 Phase 4 的仓库内置、项目维护插件。更新列表解析逻辑由本仓库原有
`BiliWebDataProvider` / `BiliLegacyJsonMapper` 行为重新实现，没有复制来源未知的
第三方插件代码，也不包含站点图标、Logo、截图或其他品牌资产。

当前检查点只实现：

- `plugin.handshake`
- `plugin.getStatus`
- `feed.getUpdates`
- `feed.getMoreUpdates`
- archive、PGC 和 opus 更新条目映射
- opaque cursor、结构化站点错误和取消传播

登录、历史、稍后再看、直播和作者关系将在后续 Phase 4 检查点实现。插件当前会
随应用构建输出，但 4.2 不把它注册为默认来源，也不切换 WinUI 数据路径。
