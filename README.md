# FeedPeek

一个使用 **WinUI 3 原生开发**的 Windows 托盘通用更新查看器。
FeedPeek 原生支持 RSS/Atom，也可以通过用户安装的网站插件接入更新、历史记录、稍后再看、作者和直播内容。

<img src="./docs/assets/主界面.png" alt="主界面" width="400" />

## 功能

- 关注更新：聚合 RSS 与已安装网站插件的最新内容。

- 直播状态：支持该能力的网站插件可显示正在直播的作者。

    <img src="./docs/assets/关注.png" alt="关注&直播" width="400" />

- 历史记录：统一展示支持历史能力的网站内容。

    <img src="./docs/assets/历史.png" alt="历史记录" width="400" />

- 稍后再看：由插件能力提供添加、查看和移除操作。

    <img src="./docs/assets/稍后再看.png" alt="稍后再看" width="400" />

- 开机自启：可选择登录 Windows 后自动运行。
- 配置导入导出：导出或恢复通知、启动、登录状态等配置。
- 托盘驻留：从任务栏托盘快速打开主界面或设置。

    <img src="./docs/assets/设置-通用.png" alt="设置-通用" width="560" />

- 桌面通知：支持 RSS 更新、网站更新和开播通知，也可只关注指定作者。

    <img src="./docs/assets/设置-通知-所有.png" alt="通知-所有" width="560" />

    <img src="./docs/assets/设置-通知-自定义.png" alt="通知-指定UP" width="560" />

## 安装

详见 [安装教程](./docs/INSTALL.md)。

## 使用说明

RSS 可在“设置 → 来源”中直接添加。网站能力需要先从本地文件夹安装相应插件，再按插件要求决定是否登录，最后手动添加来源。FeedPeek 本体不随包附带视频网站插件。

## 账号与隐私

订阅、已读状态和插件配置保存在本机应用数据中；插件登录凭据保存在 Windows Credential Locker。FeedPeek 不运营自己的账号系统，网络请求由你的设备直接访问 RSS 地址、已安装插件声明的网站或你主动打开的链接。

“设置 → 通用”的配置导出包含通用选项、来源订阅、通知规则和自选作者，不包含插件程序、登录凭据、历史内容或通知基线。导入会按来源标识合并恢复，保留文件以外的来源以及已有历史、已读和稍后再看数据。换设备后需要重新安装对应插件并登录。旧版配置文件只恢复其中实际提供的字段。

更完整的隐私说明见 [PRIVACY.md](./docs/PRIVACY.md)。

## 开发环境

- Windows 10 1809 或更高版本。
- Visual Studio 2022，建议安装“.NET 桌面开发”和 Windows 应用 SDK / WinUI 相关工作负载。
- .NET SDK 9 或更高版本。
- Windows App SDK 2.4.0。
- WebView2 Runtime。

项目使用：

- WinUI 3
- Windows App SDK
- CommunityToolkit.Mvvm
- CommunityToolkit.WinUI
- DesktopFlyouts.WinUI
- Microsoft.Web.WebView2

## 编译

还原依赖：

```powershell
dotnet restore FeedPeek.slnx
```

编译 x64：

```powershell
dotnet build FeedPeek.slnx -c Release -p:Platform=x64 --no-restore
```

生成用于 Partner Center 的 Microsoft Store 上传包：

```powershell
.\scripts\Build-StorePackage.ps1
```

项目当前仅支持 x64。脚本会读取 `Package.appxmanifest` 中的版本，生成未签名的 x64 `.msixbundle`；Microsoft Store 要求四段版本号的最后一段保持为 `0`，例如 `1.0.1.0`。

## 贡献

欢迎提交 Issue 或 Pull Request。

## 许可证

本项目采用 **GNU General Public License v3.0（GPL-3.0）** 开源，完整条款见 [LICENSE](LICENSE)。

你可以在 GPL-3.0 许可条款下自由使用、复制、修改和分发本项目的源代码；如果分发修改后的版本或基于本项目的衍生作品，也需要继续以 GPL-3.0 兼容的方式公开相应源代码。

FeedPeek 使用到的第三方项目、库和服务可能适用各自的许可证或使用条款，请在使用和分发时一并遵守。

## 参考

- [microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK)
- [microsoft/WinUI-Gallery](https://github.com/microsoft/WinUI-Gallery)
- [microsoft/microsoft-ui-xaml](https://github.com/microsoft/microsoft-ui-xaml)
- [CommunityToolkit/Windows](https://github.com/CommunityToolkit/Windows)
- [0x5bfa/DesktopFlyouts](https://github.com/0x5bfa/DesktopFlyouts)
