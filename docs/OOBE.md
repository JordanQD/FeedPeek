# 首次使用引导（OOBE）

使用 DevWinUI 10.9.1 的 `MorphingWizard`，由独立 `OobeWindow` 承载。官方示例：
https://ghost1372.github.io/DevWinUI/controls/morphingWizard/index

窗口不显示系统标题栏，不再保留顶部拖动行或关闭按钮；应用 Logo 只出现在欢迎步骤内。第一页左下角的「稍后再说」使用文字链接样式。

## 修改内容

- `src/FeedPeek.App/OobeWindow.xaml`：欢迎、添加来源、使用偏好、准备就绪四步内容与布局。
- `src/FeedPeek.App/OobeWindow.xaml.cs`：偏好保存及完成/稍后再说；文字链接直接加入 MorphingWizard 的原生底部操作行。
- `src/FeedPeek.App/Controls/OobeSourcesControl.xaml/.xaml.cs`：全宽来源卡片、RSS 名称、来源数量与插件直接登录，无子页面。
- `src/FeedPeek.App/Services/StartupSettingsService.cs`：与通用设置共用的开机自启逻辑，保留 Windows 的用户/策略限制。
- `src/FeedPeek.App/Strings/zh-CN/Resources.resw`、`zh-HK/Resources.resw`：`Oobe*` 文案。已有资源会覆盖 XAML 中的默认文字，修改时须同步资源。
- `AppSettings.HasCompletedOobe`：仅点击最后的「开始使用」时保存完成状态。「稍后再说」不标记完成；重看不会清除已有完成状态。来源、插件和偏好操作即时保存，退出引导不会回滚。

## 开发与反复预览

用 Visual Studio 的 x64 / Debug 启动，或在项目根目录运行：

```powershell
winapp run src/FeedPeek.App/FeedPeek.App.csproj -p Platform=x64 --arch x64 --detach --devtools off -- --oobe
```

`--oobe` 可直接重开引导，不重置完成状态或其他数据。首次普通启动会自动展示。完成后，从托盘右键「设置 → 通用 → 配置」的第一项「首次使用引导」反复打开，无需清空 RSS、登录状态或其他设置。多次打开复用同一窗口。通知激活不自动弹出引导；引导关闭后应用继续驻留托盘。

## 验收步骤

1. 首次启动：看到欢迎页，计数为第 1 步，共 4 步；顶部无预留标题行，没有关闭按钮。
2. 点击继续、上一步：内容与进度一致，快速切换无重叠残留。
3. 第二步直接添加 RSS/插件支持的地址，或从文件夹安装插件。RSS 直接显示来源名称；插件卡片显示来源数量和登录状态，不提供子页面或额外的「添加来源」按钮。有默认来源的插件沿用应用原有安装/登录接入流程，需要直播间地址的仍从地址栏添加。
4. 第三步修改开机自启及主弹窗高度（600/800/1000），返回再进入应显示保存值。Windows 禁止更改自启时应禁用开关并提示原因。第一步点击「稍后再说」只关闭引导，不退出托盘。
5. 点击「开始使用」：窗口关闭；退出并重启后不再自动显示。
6. 从通用配置首项重看：修改偏好并完成，通用设置应同步显示新值。关于页不再出现引导入口。
7. 引导显示时再次启动：激活已有引导窗口，不新建重复窗口。
8. 检查简体/繁体、明暗/高对比度、键盘 Tab/Shift+Tab/Enter 和 125%/150% 缩放；内容可滚动，操作按钮可到达。

需要完整的干净首次启动验证时，可在隔离 Windows 测试桌面运行：

```powershell
winapp run src/FeedPeek.App/FeedPeek.App.csproj -c ReleaseFull -p Platform=x64 --on sandbox --arch x64 --detach --json --devtools off
```

沙盒使用 ReleaseFull 全量自包含配置，以携带 .NET 和 Windows App Runtime，避免缺少调试运行库。当前项目清单沿用商店包身份；不能仅凭开发输出目录认定数据隔离。完整首次运行测试优先放在沙盒；不要为了预览引导更换商店身份或删除正式版数据。

## 本次验证（2026-11-17）

- x64 Debug 本机构建、打包启动通过（0 错误）。
- 本机欢迎页实测：「继续」中心 Y=667，「稍后再说」中心 Y=667.5，位于同一原生操作行。
- 本机来源页实测：来源数量 2，RSS「IT之家」名称直接可见；插件卡片显示「1 个来源 · 已登录」，没有额外添加按钮。
- 来源滚动视口与卡片宽度均为 590 像素；偏好两张卡片间距为 4 像素。
- 通过前进/后退验证来源页可重新加载，偏好使用设置卡片展示；现有订阅和登录状态保留。
- 未代替用户操作账号登录。繁体、高对比度、不同 DPI 和 ARM64 真机仍需单独验证。
