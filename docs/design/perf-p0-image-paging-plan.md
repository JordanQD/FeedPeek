# FeedPeek 性能 P0 计划：图片解码、资源字典、首屏增量加载

> 状态：P0 已实施（构建与 Core 测试通过；待 VS 内存对比与手测滚动加载）  
> 背景：Debug + VS 下启动约 100–200MB，打开主弹窗约 500MB+。平台税（.NET + WinUI）之外，项目侧最大嫌疑是**全尺寸 BitmapImage 解码**、**行级 ResourceDictionary**、以及**一次加载过多条目**。  
> 参考：`docs/WINUI3_MEMORY_OPTIMIZATION.md`、`docs/design/phase5-memory-test-plan.md`

## 1. 目标

1. 打开主弹窗时图片解码内存显著下降（封面按显示宽度解码，不再解全尺寸）。
2. 列表行不再各自加载 `OverlayButtonResources.xaml`。
3. 三个列表页**首屏 30 条**，滚动接近底部时再加载下一页，并**增量 Append**（禁止 `Clear` + 全量重建 Row/图）。

非目标（P1/P2，见文末）：SQLite 批量写、N+1 查询、Jint 双栈合并、Acrylic 降级、working set trim 落地。

## 2. 设计

### 2.1 图片解码与有界缓存

新增 `Helpers/ImageSourceCache.cs`：

- Key：`url|kind|decodePixel`（kind：cover / icon / avatar）。
- Value：`BitmapImage`（UI 线程创建；跨线程调用时 `DispatcherQueue.TryEnqueue` 或约定仅 UI 构造 Row）。
- 构造时设置 `DecodePixelWidth`（或 Height），**禁止**裸 `new BitmapImage(uri)`。
- LRU 上限（默认 256 条）；超出淘汰最久未用。不主动 `UriSource=null` 破坏共享实例，仅移出缓存交给 GC。
- 提供 `Clear()`，供以后「最低内存」策略可选调用；P0 默认关闭后保留有界缓存（平衡策略）。

解码尺寸（按当前 UI）：

| 用途 | 显示 | DecodePixelWidth |
|------|------|------------------|
| 视频封面 | 128×72 | 256（2x DPI） |
| 来源/作者图标 | 20×20 | 40 |
| 直播头像 | 44×44 | 88 |
| 设置页头像/图标 | 40 或 32 | 80 |

调用点：

- `Models/FeedItemRow.cs` — cover / source icon / attribution icon
- `Models/FeedLiveItemRow.cs` — avatar
- `Models/FeedSourceRow.cs`、`Models/AddedSourceEntry.cs`
- `Models/NotificationCreatorSubscription.cs`
- `Pages/SourceDetailPage`、`PluginDetailPage`、`NotificationSettingsPage` 的 `CreateImageSource` / `new BitmapImage`

### 2.2 行级 ResourceDictionary 提升

三页 ItemTemplate 内：

```xml
<Button.Resources>
  <ResourceDictionary Source="/Styles/OverlayButtonResources.xaml" />
</Button.Resources>
```

改为在 `App.xaml` 的 `MergedDictionaries` 合并一次，或在各 Page 的 `Page.Resources` 合并一次。行内 Button 不再自带 `ResourceDictionary`。

因 ThemeDictionaries 的 `Button*` 键会**局部覆盖按钮样式**，若提到 App 级会影响所有 Button，则改为：

- **方案 A（采用）**：保留字典文件，但在三页 `Page.Resources` 各 `MergedDictionaries` 合并**一份**，行内 Button 用 `Style` 显式指向 overlay 样式；或  
- **方案 B（采用、更简单）**：把 overlay 效果做成 `Style x:Key="OverlayIconButtonStyle"`，放进 `SharedResources.xaml` / 单独字典，行内 `Button Style="{StaticResource OverlayIconButtonStyle}"`，删除行级 `ResourceDictionary`。

P0 采用 **方案 B**：一次样式定义，行内只引用 Style，不重复加载源文件。

### 2.3 首屏 30 条 + 滚动增量加载

#### 数据层

`FeedQuery` 增加分页字段（兼容现有测试默认 Limit）：

```csharp
public sealed record FeedQuery(
    bool UnreadOnly = false,
    string? SourceId = null,
    int Limit = 500,
    bool ReadOnly = false,
    bool SavedOnly = false,
    string? ProviderId = null,
    string? FeedId = null,
    int Offset = 0);   // 新增
```

`SqliteFeedStore.GetItemsAsync`：`LIMIT @limit OFFSET @offset`。

面板查询默认页大小：

```csharp
internal const int PanelPageSize = 30;
```

#### Session 层

- `GetFollowingPageAsync` / `GetHistoryPageAsync` / `GetViewLaterPageAsync`：首屏 `Limit=30, Offset=0`。
- `LoadMoreSectionItemsAsync` / `LoadMoreFollowingItemsAsync` 等：
  1. 先对缺页的插件 feed `LoadMoreAsync`（拉网络下一页并入库）；
  2. 再 `QueryItemsAsync` 时传入当前已加载条数作为 `Offset`，只取**下一页 30 条**；
  3. 返回 `FeedItemLoadResult(newItems, hasMore)`——**仅新条目**，不再整表。

`HasMoreFeedItemsCoreAsync` 逻辑保持：插件 `NextCursor` 或本地 `offset+pageSize < 总量`（可用 `Count` 查询或 `Limit=offset+pageSize` 后比较数量；P0 用「再取一页是否为空 / 是否取满」判断）。

#### UI 层 `IncrementalFeedItemCollection`

```csharp
public void Reset(IReadOnlyList<FeedItemRow> items, bool hasMore)
{
    // 仅首屏/刷新时调用
    HasMoreItems = hasMore;
    _loadedCount = items.Count;
    Clear();
    foreach (var item in items) Add(item);
}

public void Append(IReadOnlyList<FeedItemRow> items, bool hasMore)
{
    // LoadMore：只加新行，不 Clear
    HasMoreItems = hasMore;
    foreach (var item in items)
    {
        if (_keys.Add(item.Key)) Add(item);
    }
    _loadedCount = Count;
}
```

- `LoadMoreCoreAsync` 调用 page 的 loadMore 后 `Append`，返回新增数量。
- `CancellationToken`：从 `MainPanelControl` 的 `_flyoutCts` 注入（P0 尽量接上；若接不动至少不用 `CancellationToken.None` 静默失败）。
- 滚动触发：继续使用 `ISupportIncrementalLoading`（ListView 在接近底部时会调用 `LoadMoreItemsAsync`）；若阈值不够灵敏，可在页面上监听 `ScrollViewer.ViewChanged`，当 `VerticalOffset + ViewportHeight > ScrollableHeight - 2*ViewportHeight` 时手动 `LoadMoreItemsAsync`。P0 优先验证 `ISupportIncrementalLoading` 是否满足「快到底自动加载」。

#### 刷新语义

`ActivateAsync` / 手动刷新：仍 `Reset` 首屏 30 条（以最新为准）。`LoadMore` 只追加。避免「加载更多时整表 Replace 导致图片全部重建」。

## 3. 任务拆分

| 任务 | 内容 | 验收 |
|------|------|------|
| T7 | `ImageSourceCache` + 全站 `BitmapImage` 带 DecodePixel* | 打开弹窗后图片相关 native 增量下降；封面清晰 |
| T8 | Overlay 按钮样式提升，删除行级 ResourceDictionary | 行模板无 `ResourceDictionary Source` |
| T9 | FeedQuery.Offset + Session 页查询 + Collection Append | 首屏 30，滚到底自动加载，不闪全列表 |
| T10 | `dotnet build` Debug/Release x64 无错 + 基础自测 | 构建通过 |

## 4. 风险与取舍

| 风险 | 处理 |
|------|------|
| `DecodePixelWidth` 过小导致封面糊 | 封面用 256（显示 128）；高 DPI 已覆盖 2x |
| 共享 `BitmapImage` 被绑定后不可变 | 缓存只读共享，不修改 `UriSource` |
| Offset 分页在刷新后错位 | LoadMore 仅追加、按 Key 去重；刷新用 Reset |
| 历史页双查（History + ReadOnly） | P0 先减默认 Limit 到 30；合并查询留 P1 |
| `ISupportIncrementalLoading` 不够积极 | 页面 ViewChanged 兜底 |

## 5. 验证方法

1. Debug x64 下启动 → 开主弹窗 → 切三页 → 滚到底触发加载 → 关闭。  
2. VS 诊断工具：开弹窗前后 Managed/Native heap 差值对比改前基线（`docs/design/phase5-memory-test-plan.md` 模板）。  
3. 肉眼：封面、头像不糊；滚动到底出现加载且不整列表闪烁。  
4. 连续开关 5 次：内存不单调暴涨。

## 6. 后续（非 P0）

- P1：`UpsertItemsAsync` 批量、`GetSourceSummariesAsync` N+1、`GetItemsAsync` GROUP BY 收窄、历史页合并查询。  
- P1：`RefreshAllAsync` 有限并行；插件 JSON/脚本复用。  
- P2：Panel/Notification 共享一套 Runtime；Jint 256MB 下限复议；Acrylic 策略；GC + working set trim 落地或删除文档承诺。
