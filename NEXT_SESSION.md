# Image2Studio Next Session

Project: `C:\Users\ASUS\Desktop\Image2Studio`
App id: `com.apinebula.image2studio`
Tech: .NET MAUI, Android + Windows.

Hard rules: do not change Image2 API request semantics casually; do not spend real generation quota; do not clear phone app data without explicit permission.

This round:
- Added Android per-history manual task-folder fallback in history detail -> `生图结果` buttons: `保存图片` / disabled-or-enabled `打开文件夹` / `创建文件夹`.
- Automatic folder creation is still enabled through `Image2TaskFileService.EnsureTaskOutputFolderAsync`; manual creation is only a fallback for the selected history/task.
- Android manual creation requests `MANAGE_EXTERNAL_STORAGE`; without permission it opens the app all-files permission page and does not create anything. With permission it writes under public `Documents/Image2 Studio Library`, creating a normal task folder with prompt/params/task/manifest/index/references and result image when available.
- Android manual image save no longer asks every time; it saves images to system gallery `Pictures/Image2 Studio` via MediaStore and shows start/completion prompts.

Important files:
- UI and handlers: `MainPage.xaml.cs`
- Android manifest permissions: `Platforms\Android\AndroidManifest.xml`
- Android gallery/all-files helpers: `Platforms\Android\UserFileSaver.Android.cs`
- Shared saver contract/stubs: `Services\UserFileSaver.cs`, `Services\UserFileSaver.shared.cs`, `Platforms\Windows\UserFileSaver.Windows.cs`
- Folder writing: `Services\Image2TaskFileService.cs`, `Services\OutputFolderWriter.shared.cs`, `Platforms\Android\OutputFolderWriter.Android.cs`

Verified on 2026-05-25:
- Android Debug build passed.
- Android Release publish passed: `bin\Release\net10.0-android\android-arm64\publish\com.apinebula.image2studio-Signed.apk` (30,030,676 bytes).
- Windows Debug build passed.
- OPPO `CIVO55R4TS5LQ4ZL` (`PKC130`) installed with `adb install -r -d`; app launched, foreground activity is Image2Studio, filtered logcat showed no app crash.
- UI check on existing no-folder history `任务 102401 v1`: status shows `文件夹: 未记录`, result section shows `任务文件夹: 尚未创建`, and buttons show `保存图片`, disabled `打开文件夹`, enabled `创建文件夹`.

Not tested: actual manual folder creation after granting all-files permission, because permission/settings changes were not authorized this round.

Build handoff update 2026-05-25 12:04:
- Re-published Android arm64 Release with:
  `dotnet publish Image2Studio.csproj -f net10.0-android -c Release -r android-arm64`
- Current Android artifact:
  `bin\Release\net10.0-android\android-arm64\publish\com.apinebula.image2studio-Signed.apk`
  Size: 30,381,806 bytes. Timestamp: 2026-05-25 12:01:39.
- Android delivery copy:
  `C:\Users\ASUS\Desktop\image2\Image2Studio-android-arm64-20260525-120432.apk`
  SHA256: `852C4A08B83ECDF3713B52D5D7CFF0B97F882500E2D0002A4E507B639DFB3142`
- Re-published Windows win-x64 Release with:
  `dotnet publish Image2Studio.csproj -f net10.0-windows10.0.19041.0 -c Release -r win-x64 -p:TargetFrameworks=net10.0-windows10.0.19041.0`
- Important Windows note: plain Windows publish without `-p:TargetFrameworks=net10.0-windows10.0.19041.0` failed restore because it generated a `net10.0-android/win-x64` graph and requested nonexistent `Microsoft.NETCore.App.Runtime.Mono.win-x64 (= 10.0.8)`.
- Current Windows publish directory:
  `bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\`
  Main files: `Image2Studio.exe` (289,792 bytes), `Image2Studio.dll` (1,523,712 bytes).
- Windows delivery zip:
  `C:\Users\ASUS\Desktop\image2\Image2Studio-win-x64-20260525-120432.zip`
  SHA256: `F012546767B7AB0B93482AF998D6DED056BE5E73AC1ADAE133E25D2FCF888751`
- Windows delivery folder refreshed from the current publish output:
  `C:\Users\ASUS\Desktop\image2\Image2Studio-win-x64`
  Contains 335 top-level items including `Image2Studio.exe`, `Image2Studio.dll`, and `Image2Studio.runtimeconfig.json`.
