# Image2Studio Next Session

Project: `C:\Users\ASUS\Desktop\Image2Studio`
Repo: `Mycroftxrg/Image2Studio`
Tech: .NET MAUI, Windows + Android.

Hard rules from user:
- If sandbox blocks an important command, request escalation directly; do not route around it.
- Do not spend real image-generation quota unless explicitly allowed.
- Do not clear phone app data without explicit permission.

Current release line:
- `v1.1` is already on GitHub: commit `83856aa`, installer SHA256 `0CA6AEF92336F61B2C1D5C461ED3C9A9EB0AE3BCF014404049A86309103D065F`.
- `v1.2` is on GitHub: commit `6035da0`, tag `v1.2`, release URL `https://github.com/Mycroftxrg/Image2Studio/releases/tag/v1.2`.
- `v1.3` is on GitHub: commit `b97c25d`, tag `v1.3`, release URL `https://github.com/Mycroftxrg/Image2Studio/releases/tag/v1.3`.

1.3 hotfix made on 2026-05-31:
- User reported that after installing 1.2, the app still opened/showed 1.0.
- Root cause: Windows unpackaged MAUI could still report `AppInfo.Current.VersionString` as 1.0 even though the 1.2 exe/dll file version was 1.2.
- Fixed by adding explicit `<Version>`, `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>` in `Image2Studio.csproj`, updating `Platforms\Windows\app.manifest` to `1.3.0.0`, and making `AppUpdateService.CurrentVersionText` prefer a non-default assembly version before `AppInfo`.
- 1.3 installer: `C:\Users\ASUS\Desktop\Image2Studio\installer\Image2StudioSetup-1.3-win-x64.exe`
- 1.3 SHA256: `0C14D680DC5B090938ED40BEB644F6A054C8E945E274DFEF7B6F7F2582C95F17`
- 1.3 release asset verified with GitHub CLI digest `sha256:0c14d680dc5b090938ed40beb644f6a054c8e945e274dfef7b6f7f2582c95f17`.

1.2 changes made on 2026-05-30:
- Version bumped to `1.2` / app version `3` in `Image2Studio.csproj`; installer version bumped in `Image2StudioInstaller.iss`.
- Fixed updater workflow: after download, app launches installer with `/SILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART`, schedules downloaded installer cleanup, then quits the app.
- Installer also attempts to delete its own downloaded `{srcexe}` when launched from an `updates` cache path.
- Update manifest now supports `changelogUrl`; update dialogs fetch and show the full Chinese `CHANGELOG.md`.
- `CHANGELOG.md` converted to Chinese and includes 1.2, 1.1, 1.0 entries.
- Added drag/drop local images into the home reference panel and the task editor reference section.
- Added `引用上一版本结果图` button inside the existing `基于此版本重新生图` editor page only. It optionally adds the selected history version's local result image as a reference and switches to reference edit mode. No separate rerun mode or extra history shortcut should exist.
- Reference image file reads now prefer `FileResult.FullPath` before MAUI `OpenReadAsync`, preventing Windows `windowsRuntimeFile` null failures for saved/dragged/local result references.

Important files changed for 1.2:
- `Services\Image2ApiClient.cs`: earlier 1.1 fix for local reference reads.
- `Services\Image2TaskFileService.cs`: copy references via local path when available.
- `Services\AppUpdateService.cs`: changelog fetch, silent installer launch, quit, cleanup script.
- `App.xaml.cs`, `SettingsPage.xaml.cs`: show changelog and call `LaunchInstallerAndQuit`.
- `MainPage.xaml`, `MainPage.xaml.cs`: drag/drop and rerun editor optional previous-result reference button.
- `CHANGELOG.md`, `latest.json`, `Image2StudioInstaller.iss`, `Image2Studio.csproj`.

1.2 build artifacts after final rebuild:
- Windows publish command:
  `dotnet publish Image2Studio.csproj -f net10.0-windows10.0.19041.0 -c Release -r win-x64 -p:TargetFrameworks=net10.0-windows10.0.19041.0`
- Installer compile command:
  `& 'C:\Users\ASUS\AppData\Local\Programs\Inno Setup 6\ISCC.exe' 'C:\Users\ASUS\Desktop\Image2Studio\Image2StudioInstaller.iss'`
- Final installer:
  `C:\Users\ASUS\Desktop\Image2Studio\installer\Image2StudioSetup-1.2-win-x64.exe`
- Final installer SHA256:
  `C853C83BF915BC03EB159AC45BD5DE0B772E81651616F937820E9979B462A586`
- `latest.json` should point to:
  `https://github.com/Mycroftxrg/Image2Studio/releases/download/v1.2/Image2StudioSetup-1.2-win-x64.exe`

Verification already done:
- `dotnet build Image2Studio.csproj -f net10.0-windows10.0.19041.0 -c Release -p:TargetFrameworks=net10.0-windows10.0.19041.0` passed.
- `dotnet publish ... win-x64 ...` passed.
- Inno Setup compile passed.
- `rg` confirmed no leftover separate reference-rerun symbols: `HistoryReferenceRerunRequest`, `OnHistoryReferenceRerunClicked`, `作参考再生`, `用结果图作参考再生图`, `CreateReferenceRerun`, `CopyReferenceFileAsync`.

Release steps completed:
- Committed `6035da0 Release 1.2 update workflow improvements`.
- Tagged `v1.2`.
- Pushed `main` and `v1.2`.
- Created GitHub Release `Image2 Studio 1.2` and uploaded `Image2StudioSetup-1.2-win-x64.exe`.

Useful release commands:
- `git add App.xaml.cs CHANGELOG.md Image2Studio.csproj Image2StudioInstaller.iss MainPage.xaml MainPage.xaml.cs Services\AppUpdateService.cs Services\Image2TaskFileService.cs SettingsPage.xaml.cs latest.json NEXT_SESSION.md`
- `git commit -m "Release 1.2 update workflow improvements"`
- `git tag -a v1.2 -m "Image2 Studio 1.2"`
- `git push origin main`
- `git push origin v1.2`
- `& 'C:\Program Files\GitHub CLI\gh.exe' release create v1.2 'C:\Users\ASUS\Desktop\Image2Studio\installer\Image2StudioSetup-1.2-win-x64.exe' --repo Mycroftxrg/Image2Studio --title 'Image2 Studio 1.2' --notes-file <temp-notes-file>`

Older Android context kept for continuity:
- Previous Android work added manual folder/image save fallbacks and all-files permission flow.
- Android Release artifact from 2026-05-25: `bin\Release\net10.0-android\android-arm64\publish\com.apinebula.image2studio-Signed.apk`.
