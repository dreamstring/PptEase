# Shader Studio

支持 HLSL / GLSL、贴图资源、声明参数、草稿预览和 PPT 控件绑定。

```hlsl
// @param gain 0 2 1
```

依次为名称、最小值、最大值、默认值。刷新参数后生成面板；HLSL 的自定义 float 参数由后端注入；GLSL 可声明同名 uniform float。粗糙度不是所有 Shader 必须显示的通用控制项。

先编译草稿预览，调试满意后编译并写入幻灯片。预览保持比例；保存草稿不等于写入 PPT。参数更新不需要重新编译；纹理随组件数据保存。

交互类型在集中绑定区选择：滑块/复选框一个参数，圆盘/方形 X/Y 两个参数；选中 PPT 控件再绑定。当前新建路径使用普通形状，不依赖 ActiveX。详见 [交互控件](Shape-Slider.md) 与 [技术实现](Architecture.md)。
