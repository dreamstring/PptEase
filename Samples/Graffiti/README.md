# 单页交互示例

[下载 Graffiti-Sample.pptx](Graffiti-Sample.pptx?raw=true)（1 页）。保留最终双控制器页面及内嵌 Shader、贴图和绑定，无需 VBA / ActiveX。安装 PptEase 后进入放映即可拖动；左圆盘选择风格，右方形控制流动与滴液。

# 涂鸦材质双控演示

从 Release 的 standalone 包 Samples 目录取得 `Graffiti-Dual-Control.pptx` 后打开，安装 PptEase 后在放映模式交互。

- 左圆盘：材质风格。圆心为基础笔触，向外逐渐呈现喷漆、飞白、水墨、湿漆、厚涂油彩。
- 右方形：横向流动速度，纵向下淌量；相同参数根据风格呈现不同响应。
- `Graffiti-Dual-Control.hlsl` 为最终双控源码；`brush-mask.png` 为基础叉形的距离场，内部纹理、流动和液滴由 Shader 生成。

这是一种艺术化实时材质，不是物理流体模拟。没有加载项时文稿显示静态封面。

更新文稿中的源码（不会覆盖输入）：

```powershell
python Tools/UpdateGraffitiResponse.py --output updated-demo.pptx
```

生成器处理 UTF-8 BOM，并重新计算分块数据的 SHA-256；更新源码不会重新生成封面。
