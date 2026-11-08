# PptEase

Windows PowerPoint 增强工具：排版辅助、原生取色、自由画布、GPU Shader 与放映交互控件。

## 使用

下载发行版 standalone ZIP，完整解压，运行 `PptEase.exe` → 安装 / 修复。保存文稿并重启 PowerPoint，打开“PPT 增强”。需要 Windows x64、64 位桌面 PowerPoint；GLSL 后端还需要 WebView2 Runtime。自包含包携带 .NET 9 运行时，但不包含 Office。

## 构建

安装 .NET 9 SDK；Windows 自带的 .NET Framework 4.x 编译器用于 COM 宿主。

```powershell
./build.ps1
./Host/test-ribbon.ps1
```

输出 `publish/PptEase.exe`。源码与发行包是同一工程，没有另一套 standalone 源码。

## 功能与边界

- 文字/形状/线条上下文识别，调用 PowerPoint 原生吸管。
- 文本框收紧、本文档字体、文档颜色写入主题、实时模板样式。
- 母版/版式标记对象生成普通页面图形；目前限不透明纯色普通形状。
- 自由画布：原生内部接口配合输入接管；仅支持经过哈希校验的 Office 构建，极端边界与辅助线同步仍有限制。
- Shader Studio：HLSL / GLSL、草稿预览、贴图、参数、保存到 PPTX。
- 分组的滑块、圆盘、方形 X/Y、复选框；放映/预览时交互，编辑时可排版。不依赖 VBA / ActiveX。
- 用户已验证 Shader 编辑与放映播放、控件拖动；复杂遮挡/旋转/动画/多屏仍受限制，不宣称完全等价原生对象。
- 锁定与解锁入口保留；锁定对象点击穿透尚未完成。

## 文档

- [技术实现详解](docs/Architecture.md)
- [Shader Studio](docs/Shader-Studio.md)
- [交互控件](docs/Shape-Slider.md)

本仓库不包含 Office 二进制、本机日志、旧测试输出或凭据。尚未指定开源许可证，不等于授予第三方开源使用许可。

