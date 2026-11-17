# FeedPeek 安装包

## 在 Visual Studio 中生成精简包

1. 打开或重新加载 `FeedPeek.slnx`。
2. 选择解决方案配置 **ReleaseLite**。项目支持 **x64** 和 **ARM64**。
3. 右键 **FeedPeek.App** → **打包和发布 / 发布** → **创建应用程序包**。
4. 选择 **旁加载**，使用现有自签名证书，保留当前应用身份和版本。
5. 在向导的包配置页面勾选 **x64** 和 **ARM64**，两行均选择对应的 **ReleaseLite**，然后创建。仅需一种架构时只勾选该行；不要选择 Neutral / Any CPU。

输出到 `src/FeedPeek.App/AppPackages/ReleaseLite/` 下的版本目录。自签名证书仍需在目标电脑上受信任；不要向用户分发包含私钥的 PFX。

## 配置区别

| 配置 | .NET 10 | Windows App Runtime | 用途 |
| --- | --- | --- | --- |
| Release / ReleaseFull | 随包携带 | 随包携带 | 完整自包含包 |
| ReleaseLite | 随包携带 | 使用共享框架 | 较小的 MSIX bundle |

ReleaseLite 保持发布优化、ReadyToRun 和不裁剪设置。核心与插件项目映射到 Release，不改动应用代码或插件版本。`win-x64-lite.pubxml` 是对应发布配置。

精简包声明最低 Windows App Runtime **2.6.5.0**。已安装兼容 x64 共享框架的电脑会复用它；缺少时需要安装流程能获取对应的微软框架包。旁加载、离线或依赖下载源不可用时，可能需要先安装 Windows App Runtime。不能把“本机启动通过”视为已验证干净电脑自动下载依赖。

使用 Windows App Runtime 的共享机制，不代表所有应用库都可以移出。Jint、SQLite、DesktopFlyouts、托管 API 桥接程序集，以及当前 SDK 引入的其他组件仍按依赖图随包携带。

## 验证记录（2026-11-16）

- 完整 2.0.1 x64 bundle：113.10 MiB。
- ReleaseLite 2.0.1 x64 bundle：84.99 MiB，约减少 25%。
- 解包确认保留 `coreclr.dll` 和 .NET 10.0.15 的 `includedFrameworks`，移出 `Microsoft.ui.xaml.dll`；清单包含 `Microsoft.WindowsAppRuntime.2` 的 2.6.5.0 框架依赖。
- 自签名证书为现有 `Jordan_QD` 证书，SignTool 校验通过。
- 本机已安装所需共享框架；ReleaseLite 启动和托盘主面板打开通过。模块路径确认 `coreclr.dll` 来自应用目录，`Microsoft.UI.Xaml.dll` 来自系统 `Microsoft.WindowsAppRuntime.2_2.6.5.0_x64` 框架目录。
- 原完整包保留在原输出目录，精简包不会覆盖它。

## 命令行等效操作

在项目根目录运行：

```powershell
dotnet restore FeedPeek.slnx -p:Configuration=ReleaseLite -p:Platform=x64
dotnet build FeedPeek.slnx -c ReleaseLite -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true --no-restore
```

微软参考：[共享运行时打包部署](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-packaged-apps)。

## Microsoft Store 上传

VS 向导选择 **Microsoft Store，作为已有应用**，架构勾选 **x64** 和 **ARM64**；两行分别选择 **ReleaseLite (x64)**、**ReleaseLite (ARM64)**。上传生成的双架构 `.msixupload`，不要上传旧的侧载包。商店按设备架构下发对应包，用户不会同时下载两份应用。

商店身份使用 `JordanQD.BiliRadar`，发布者必须是 Partner Center 分配的 `CN=55B82F9D-39B5-4EC7-8696-862731CE35AA`。应用显示名称可以使用已预留的 FeedPeek；包系列名称应为 `JordanQD.BiliRadar_xfrpvmqehn4h2`。

项目在真正的 Store upload 构建中禁用侧载签名和临时签名证书，保留商店发布者；Microsoft Store 会在发布时签名。普通旁加载仍使用现有自签名证书。源清单正确不代表上传包一定正确，应检查 `.msixupload` 内 bundle 和应用包的最终清单；自签名证书的 `CN=Jordan_QD` 会生成不同的包系列名称，不能用于替换旧商店应用。

## ARM64 托盘库

DesktopFlyouts.WinUI 2.2.0 官方 NuGet 中的程序集固定为 AMD64。x64 继续使用官方包；ARM64 使用官方相同 commit 的源码重编译版本，位于 `third_party/DesktopFlyouts.WinUI/2.2.0/arm64/`，附有许可证和来源说明。可通过 `scripts/Build-DesktopFlyoutsArm64.ps1` 重新生成，不修改上游功能代码。

ARM64 的构建和包内容检查不能代替 ARM64 真机验证；托盘交互、通知及 WebView2 登录仍需在 ARM64 设备上验收。

## 双架构商店包验证（2026-11-16）

- ReleaseLite 双架构 Store upload 构建通过：0 错误、106 条警告；警告包括现有 XAML/WinRT 分析提示及未生成符号包。
- 输出：`src/FeedPeek.App/artifacts/StoreUpload-x64-arm64-2.0.1.0/FeedPeek.App_2.0.1.0_x64_arm64_bundle_ReleaseLite.msixupload`，173,407,629 字节。
- 逐层检查上传包内 bundle、x64 MSIX 和 ARM64 MSIX：发布者均为商店 GUID，包系列名称均为 `JordanQD.BiliRadar_xfrpvmqehn4h2`，两个应用包显示名称均为 FeedPeek。
- 两个应用包内的 EXE、coreclr、DesktopFlyouts、WebView2Loader、SQLite 原生库均与所属架构一致；DesktopFlyouts PRI 和许可证齐全。
- 此记录不表示 Partner Center 已接受上传，也不表示完成 ARM64 真机运行验证。
