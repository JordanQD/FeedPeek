# .NET 10 dependency upgrade

Date: 2026-11-16. Branch: `chore/dependencies-net10`.

This branch upgrades all application and test projects to .NET 10 and all existing direct NuGet references to the latest stable versions returned by nuget.org on this date. Transitive packages remain managed by their parent packages.

| Dependency | Before | After |
| --- | --- | --- |
| .NET target | 9.0 | 10.0 |
| DesktopFlyouts.WinUI | 1.4.0 | 2.2.0 |
| Microsoft.WindowsAppSDK | 2.4.0 | 2.6.5 |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2526 | 10.0.28000.2957 |
| Microsoft.Web.WebView2 | 1.0.4129.50 | 1.0.4314.35 |
| Microsoft.Data.Sqlite | 10.0.11 | 11.0.0 |
| Jint (exact pin retained) | 4.16.1 | 4.18.0 |
| Microsoft.NET.Test.Sdk | 17.12.0 | 18.12.0 |
| xunit | 2.9.2 | 2.9.3 |
| xunit.runner.visualstudio | 2.8.2 | 4.0.1 |
| coverlet.collector | 6.0.2 | 10.1.1 |

CommunityToolkit.Mvvm 8.4.2 and the WinUI Toolkit group 8.2.251219 were already current. WinUIAnalyzer 0.8.0 is added as a private build dependency. `global.json` selects an installed stable .NET 10 SDK; both publishing profiles and CI use .NET 10.

DesktopFlyouts 2.2.0 removes the old `Placement` and `PopupDirection` properties. The app now uses `TryGetBounds` and `DesktopFlyoutPosition.ForTaskbarAnchor` to anchor the panel to its tray icon, with the existing click point as fallback. Other panel lifecycle behavior is preserved.

## Validation

- Debug x64 framework-dependent build passed.
- Release x64 self-contained signed MSIX bundle build passed. Bundled .NET runtime: 10.0.15.
- Core tests: 292 passed. Compatibility tests: 4 passed.
- Latest-version checks reported no outdated direct references in the app solution or either test project.
- NuGet vulnerability audit of application direct and transitive packages reported no known vulnerabilities.
- Generated Release manifest retains minimum OS 10.0.17763.0 and x64 architecture. This is configuration verification, not a Windows 10 installation test.
- Windows Sandbox smoke check passed: packaged activation, tray icon, opening the main panel, and switching between Following, History, and View Later with the expected empty-state content. All three screenshots were visually checked. Results and screenshots are in the ignored `src/FeedPeek.App/artifacts/dependency-upgrade` directory. The test app remains running in the guest.
- Deployed and activated the Release x64 development package on the user's local machine. Verified the executable comes from the `Release/net10.0-windows10.0.26100.0` layout; its diagnostic log reports runtime 10.0.15, tray initialization, and the main panel opening without a startup exception. The local app remains running. This machine and the Sandbox both run Windows 11; neither check is Windows 10 validation.
- Builds and tests were run in the existing working tree, which also contains earlier uncommitted diagnostics work. That work is excluded from the dependency-upgrade commit.

## Before merging

Actual Windows 10 installation and the full UI/lifecycle matrix remain to be verified: tray left/right clicks, repeated open/close, three-page navigation, settings, WebView2 login, notification activation, startup, single-instance redirection, Explorer restart, and display/DPI changes.

The new tooling reports WUI2011 binding-mode and CsWinRT1032 collection-expression warnings. The Release build also reports missing `mspdbcmf.exe` for symbol-package generation. These do not prevent the tested builds; no binding, trimming, AOT, or symbol-generation behavior was changed to suppress them.

## Tray controller simplification

The follow-up change uses `TransitionStateChanged` instead of `IsOpen` callbacks: content is created at `Opening`, activated at `Open`, and released at `Closed`. This also covers opening requests that are dismissed before ever reaching `Open`.

Removed repeated Show attempts, the attempt limit, the delayed-close retry loop, and the extra dispatcher callback that retried closing before showing the context menu. The library handles transition requests; the app retains only the pending context-menu position needed to coordinate the two windows. The existing 300 ms light-dismiss guard remains as a single delayed opening, and background-link monitoring remains in place.

Local verification exposed a close/reopen regression when a tray click first light-dismissed the panel into `Closing`. Treating that state as a close intent, as in the previous controller, fixed the regression. Debug and Release x64 builds passed; the final Release was deployed locally. Three consecutive tray open/close cycles passed, with logs confirming content detach/disposal each time. Reopening and switching History/View Later also passed; the captured layout was visually checked and the app remained responsive.

Right-click mouse automation was blocked by the local tool's foreground-window restriction, so the context-menu interaction is not marked as tested. Background-link behavior, rapid mixed clicks, and the broader lifecycle matrix still require hands-on verification before merging.
