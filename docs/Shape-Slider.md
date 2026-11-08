# 交互控件

通过“PPT 增强 → 插入交互控件”选择滑块、圆盘、方形坐标或复选框。创建的是分组的普通形状，编辑时整体移动缩放；预览/放映时由 PptEase 提供交互。

Shader Studio 中选择控件类型及参数，选中 PPT 控件后点绑定。滑块与复选框只绑定一个参数；圆盘、方形坐标分别绑定 X/Y。可选数值框显示一位小数，并可输入数值反向控制。样式有浅色/深色等预设。

不需要 ActiveX、VBA 或 PPTM；保存到普通 PPTX 即可。未加载 PptEase 时显示静态形状。跨页复制、删除、重组导致 ShapeId 变化时需检查或重新绑定。运行时值与文稿默认值不同，需使用保存/写入动作持久化。

实现与限制见 [技术实现详解](Architecture.md)。

---

# English

Use PPT 增强 → 插入交互控件 to insert a slider, disk, square coordinate pad, or checkbox. Each control consists of grouped ordinary shapes, allowing movement and resizing in edit mode. PptEase supplies interaction during preview and slideshow.

In Shader Studio, choose the control type and parameters, select the PowerPoint control, and bind it. Sliders and checkboxes bind one parameter; disks and square pads bind X/Y separately. Optional value fields show one decimal place and accept typed values. Light and dark style presets are available.

ActiveX, VBA, and PPTM are not required; save as ordinary PPTX. Without PptEase, controls appear as static shapes. Copying between slides, deleting, or regrouping can change ShapeIds; check or recreate bindings when necessary. Runtime values differ from document defaults and require an explicit save/write action to persist.

See [Architecture](Architecture.md#english) for implementation details and limitations.
