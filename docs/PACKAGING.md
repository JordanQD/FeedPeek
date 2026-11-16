# FeedPeek 安装包

## 在 Visual Studio 中生成精简包

1. 打开或重新加载 `FeedPeek.slnx`。
2. 选择解决方案配置 **ReleaseLite**、平台 **x64**。
3. 右键 **FeedPeek.App** → **打包和发布 / 发布** → **创建应用程序包**。
4. 选择 **旁加载**，使用现有自签名证书，保留当前应用身份和版本。
5. 在向导的包配置页面确认 x64 对应 **ReleaseLite**，然后创建。

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
