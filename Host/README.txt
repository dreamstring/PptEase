PptEase.Host 0.1.0
ProgID: PptEase.Connect
CLSID: {84A31892-2FDB-4B79-A832-B0DA779BD41D}
Assembly: read AssemblyName.FullName from the compiled DLL. AssemblyVersion("0.1.*")
assigns a unique build identity, preventing Office CLR caching an earlier rebuilt DLL.
Runtime: .NET Framework 4.x / x64 Office
IDTExtensibility2 and IRibbonExtensibility are dual interfaces, matching the installed
MSADDNDR/MSO typelibs. See interface-abi-evidence.json and test-com-abi.ps1. The latter
directly exercises isolated CCW vtable slots, not just managed method calls.
Early development builds with IDispatch-only declarations must not be installed.

Build: powershell -File .\build-host.ps1
Register only the explicit class with per-user COM registration in HKCU\Software\Classes.
InprocServer32 = mscoree.dll, ThreadingModel=Both, Class=PptEase.Host.Connect,
Assembly=<above>, RuntimeVersion=v4.0.30319, CodeBase=<file URI to installed DLL>.
ProgID key PptEase.Connect\CLSID = <above CLSID>.
Office key HKCU\Software\Microsoft\Office\PowerPoint\Addins\PptEase.Connect:
FriendlyName=PPT 增强; Description=对象锁定与右键管理; LoadBehavior DWORD 3; CommandLineSafe DWORD 0.
The parent WPF installer owns registration, backup, connect and removal.

Pipe: PptEase-<PowerPoint process ID>, current SID ACL only, UTF8 newline JSON.
Request: {"id":"unique","op":"status","payload":{}}
Operations: status/snapshot, lock/unlock payload {keys:[...]}, select payload {key:"..."}, capture,
pickColor/applyColor payload {keys:[...]} (empty keys uses native PPT selection), cancelColor.
Response: {id,ok,result,error}. Lock/unlock returns fresh status. Keys include host session,
document COM identity, SlideID and hierarchy path. Changed page/deck/session rejects stale keys.

Ribbon: PPT 增强; native right-click menus extended using valid Microsoft control IDs.
Right click keeps Cancel unchanged; event targets are collected. Dynamic menu lists event
targets first when available, then remaining locked objects on current slide. No bounding-box
fallback is presented as precise hit testing. LastRightClick status records event count and IDs.

Static QAT-capable custom Ribbon command IDs:
peLock, peUnlock, pePickColor, peApplyColor, peCancelColor.
These remain stable across builds; actual QAT add/use persistence requires user manual test.
peUnlock first unlocks currently selected locked objects. If none are selectable, it opens
a named current-slide chooser instead. Opening or dismissing the chooser changes nothing;
only the user-selected menu item is unlocked. It never defaults to unlocking a whole slide.
Recent matching right-click event targets are prioritized for five seconds. Menus dispose on close.

Color directions:
- pickColor captures stable targets, shows an in-memory screen snapshot overlay,
  and waits for the user's own click. It applies the sampled color to that captured selection.
  Text selection uses exact Start/Length; textbox shape uses font; line uses line color;
  other supported shape uses solid fill. Esc/right-click cancels. No screenshot saved.
- applyColor reads source selection color and arms a one-shot selection-change handler.
  Next different target selection receives that color. Same source does not consume it.
  Page/deck change, 30-second timeout, or cancelColor exits mode. No global mouse hook.
- Locked targets and locked parent groups are not unlocked by color tools.
- Gradient/picture/pattern fills and mixed-color text ranges are refused rather than
  being silently flattened. Use native formatting for those until richer state rollback exists.
- ColorMode, CurrentColor and LastColorOperation in status expose observed mode/outcome.

Manager launch location is read from HKCU\Software\PptEase\ManagerPath.
The parent installer stores DLLs in version/hash-specific directories.

Source for context menu and image identifiers:
https://github.com/OfficeDev/office-fluent-ui-command-identifiers
Microsoft 365/Current Channel/powerpointcontrols.xlsx (downloaded in identifiers/ for review).

No raw-memory patching, global mouse hook, macro security changes, or PowerPoint restart.
ProtocolTests.cs checks batch mutation validation, stale context refusal and rollback using
fake COM objects. ColorProtocolTests.cs checks color mode and context dispatch using fakes.
These tests do not validate native PowerPoint mouse handling or QAT behavior.
Actual click/marquee/right-click/undo, screen-picker DPI and QAT verification belongs to
the user's manual fixture test. Do not automate mouse/keyboard input for this task.
