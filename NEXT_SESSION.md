# Image2Studio Next Session

Project: `C:\Users\ASUS\Desktop\Image2Studio`
Repo: `Mycroftxrg/Image2Studio`
Tech: .NET MAUI, Windows + Android.

Hard rules from user:
- If sandbox blocks an important command, request escalation directly; do not route around it.
- Do not spend real image-generation quota unless explicitly allowed.
- Do not clear phone app data without explicit permission.
- Changelogs and update UI text should be Chinese.

Current release status:
- Latest public release is `v1.4`: `https://github.com/Mycroftxrg/Image2Studio/releases/tag/v1.4`
- `latest.json` on `main` points to `v1.4` and `Image2StudioSetup-1.4-win-x64.exe`.
- 1.4 installer: `C:\Users\ASUS\Desktop\Image2Studio\installer\Image2StudioSetup-1.4-win-x64.exe`
- 1.4 SHA256: `0A61E6BF9F8E78D6C65EB03870DB8955FB2637EA52B33C314DBB5C5995D434C4`
- GitHub release asset digest verified: `sha256:0a61e6bf9f8e78d6c65eb03870db8955fb2637ea52b33c314dbb5c5995d434c4`
- Remote release commit is `a771789c58010f451e12869b34e819d5ee89174c`.

1.4 work completed on 2026-05-31:
- Version bumped to `1.4` in `Image2Studio.csproj`; `ApplicationVersion` is `5`.
- Windows manifest bumped to `1.4.0.0`; Inno Setup `MyAppVersion` bumped to `1.4`.
- Auto-update no longer launches the installer silently and no longer quits the app itself. It opens the visible installer UI and lets Inno Setup handle closing the running app and overwriting files.
- Downloaded update installers are still scheduled for deletion after installer exit.
- Added `UpdatePromptPage.cs`, a modal update prompt that renders simple Markdown headings, bullets, and body text.
- `App.xaml.cs` and `SettingsPage.xaml.cs` now use `UpdatePromptPage.ShowAsync(...)` and call `LaunchInstallerForUpdate(...)`.
- Startup auto-check now checks when `AutoCheckUpdates` is enabled instead of being blocked by the previous 12-hour timestamp. It still only prompts when a newer version exists.
- `AppUpdateService.GetUpdateNotesAsync(...)` now fetches `CHANGELOG.md` but extracts only the target latest version's section, not the full history.
- `CHANGELOG.md`, `latest.json`, and `release-notes-v1.4.md` are Chinese and only describe the new 1.4 changes for release/update display.

Why 1.4 was needed:
- User reported that after auto-update downloaded an installer, there was no visible action. The silent installer path was confusing and not reliable enough.
- User wanted the app to open the installer UI; installer itself supports closing the app and updating.
- User also reported startup automatic update checks were not popping. The old local auto-check timestamp could suppress prompts after an early failed or hidden check.
- User wanted Markdown changelog rendering and only the new version's changelog in the update dialog.

1.4 verification done:
- `dotnet build Image2Studio.csproj -f net10.0-windows10.0.19041.0 -c Release -p:TargetFrameworks=net10.0-windows10.0.19041.0` passed with 0 warnings and 0 errors.
- `dotnet publish Image2Studio.csproj -f net10.0-windows10.0.19041.0 -c Release -r win-x64 -p:TargetFrameworks=net10.0-windows10.0.19041.0` passed.
- Published EXE/DLL file version verified as `1.4.0.0`; product version showed `1.4+...`.
- Inno Setup compile passed and produced `Image2StudioSetup-1.4-win-x64.exe`.
- `rg` confirmed no leftover `LaunchInstallerAndQuit`, `/SILENT`, or `SUPPRESSMSGBOXES` references.
- GitHub Release `v1.4` and remote `latest.json` were verified with GitHub CLI/API.

Git note from 1.4 release:
- Normal `git push` repeatedly failed with GitHub port 443 connection reset/timeouts, even after escalation.
- GitHub CLI API access worked, so the 1.4 files were committed to remote `main` via GitHub API and `refs/tags/v1.4` was created there.
- Local branch had an equivalent local 1.4 commit `02c20b1`; remote has API commit `a771789`. Differences were only line endings for `Image2Studio.csproj` and `Platforms/Windows/app.manifest`.
- Local branch was merged with `origin/main` using `git merge -s ours origin/main -m "Merge remote 1.4 API release"` before this document update, so future pushes can fast-forward from the remote release history.
- Remote `v1.4` tag points to `a771789`. Do not force-push or replace it unless the user explicitly asks.

Earlier release line:
- `v1.1`: commit `83856aa`, installer SHA256 `0CA6AEF92336F61B2C1D5C461ED3C9A9EB0AE3BCF014404049A86309103D065F`.
- `v1.2`: commit `6035da0`, release URL `https://github.com/Mycroftxrg/Image2Studio/releases/tag/v1.2`.
- `v1.3`: commit `b97c25d`, release URL `https://github.com/Mycroftxrg/Image2Studio/releases/tag/v1.3`.

Important prior fixes:
- 1.1 fixed Windows local reference image reads by preferring `FileResult.FullPath` before MAUI `OpenReadAsync`, preventing `windowsRuntimeFile` null failures.
- 1.2 added automatic installer cleanup, drag/drop reference images, and the optional `引用上一版本结果图` button inside the existing `基于此版本重新生图` editor page.
- 1.3 fixed Windows installed app still reporting/showing 1.0 by adding explicit assembly/file/informational versions and making `AppUpdateService.CurrentVersionText` prefer assembly version over `AppInfo`.

Useful release commands:
- Build: `dotnet build Image2Studio.csproj -f net10.0-windows10.0.19041.0 -c Release -p:TargetFrameworks=net10.0-windows10.0.19041.0`
- Publish: `dotnet publish Image2Studio.csproj -f net10.0-windows10.0.19041.0 -c Release -r win-x64 -p:TargetFrameworks=net10.0-windows10.0.19041.0`
- Installer: `& 'C:\Users\ASUS\AppData\Local\Programs\Inno Setup 6\ISCC.exe' 'C:\Users\ASUS\Desktop\Image2Studio\Image2StudioInstaller.iss'`
- Hash: `Get-FileHash -Algorithm SHA256 -LiteralPath <installer>`
- Release: `& 'C:\Program Files\GitHub CLI\gh.exe' release create vX.Y '<installer>' --repo Mycroftxrg/Image2Studio --title 'Image2 Studio X.Y' --notes-file '<notes-file>'`

Older Android context:
- Previous Android work added manual folder/image save fallbacks and all-files permission flow.
- Android Release artifact from 2026-05-25: `bin\Release\net10.0-android\android-arm64\publish\com.apinebula.image2studio-Signed.apk`.
