# FeedPeek 哔哩哔哩插件

此插件是由项目维护、需要用户手动安装的网站插件。更新列表解析逻辑由本仓库原有
`BiliWebDataProvider` / `BiliLegacyJsonMapper` 行为重新实现，没有复制来源未知的
第三方插件代码，也不包含站点图标、Logo、截图或其他品牌资产。

安装方式：打开 FeedPeek 的“设置 → 来源”，点击“从文件夹安装插件”，选择本
`feedpeek.video-site` 文件夹。重新打开设置后，先在插件卡片中完成账户登录，再点击
“添加来源”。安装、登录本身都不会自动创建来源。

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

插件不会随 FeedPeek 应用包发布，也不会由应用自动安装。停用插件会停止加载和
刷新，但保留已创建的来源与本地缓存；卸载插件会清除该插件的来源、缓存和登录状态。
