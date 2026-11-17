# DesktopFlyouts.WinUI 2.2.0 — ARM64

- Upstream: https://github.com/0x5bfa/DesktopFlyouts
- Source commit: `8678485b42e8da21e12ff938274dec9d7a910a69`, as recorded in the official 2.2.0 NuGet package metadata.
- License: MIT; the unmodified upstream license is included as `LICENSE`.
- No upstream source files were modified. The library was rebuilt for `net10.0-windows10.0.26100.0`, `win-arm64`, `Platform=arm64`, `PlatformTarget=ARM64`, in Release mode. `WindowsSdkPackageVersion=10.0.26100.57` is pinned to match the application. Library AOT publishing and automatic NuGet packing were disabled for this managed-library build.
- Use these assets only in ARM64 application builds. The x64 application continues to use the official NuGet package.
- The official NuGet 2.2.0 DLL has PE machine AMD64. This DLL has PE machine ARM64 (`0xAA64`), IL-only CorFlags, and assembly version `2.2.0.0`.
- Both this DLL and the official x64 DLL reference `Microsoft.Windows.SDK.NET, Version=10.0.26100.38` and `WinRT.Runtime, Version=2.3.1.0`.
- Only the DLL and PRI runtime assets are included. The PRI is byte-identical to the official 2.2.0 NuGet PRI, so no additional loose XAML/XBF files are needed. PDBs, API XML, and transitive dependencies are excluded.
- DLL SHA256: `B8903A9935A7D8DB1844BE48743A70957DCD9C1AAF3150CD330A696735F12D7B`.
- PRI SHA256: `AA7960B8BCAA2A6DBDC79EBE155015E407CCF2F85F64E9611A12936BAECCD975`.

Rebuild from the repository root with `scripts/Build-DesktopFlyoutsArm64.ps1`. The script downloads the pinned upstream archive into ignored temporary directories, builds the library, verifies the ARM64 PE header, and copies only these runtime assets and the license. It does not modify the upstream source, install runtimes/certificates, or delete directories. Rebuilt DLL hashes can vary with the installed compiler and generated-source paths; the source commit and architecture are fixed.

Validation on 2026-11-16: the full rebuild script, including download, restore, build, PE verification, and asset copying, passed with zero build warnings and errors. ARM64 device execution has not been verified on this x64 development machine.
