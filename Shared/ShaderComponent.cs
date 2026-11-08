using System;
using System.Collections.Generic;
using System.IO;

namespace PptEase.Shader
{
    // JSON contract intentionally uses only Framework-compatible CLR types.
    public sealed class ShaderComponent
    {
        public int Version { get; set; }
        public string Backend { get; set; }
        public string Source { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int FramesPerSecond { get; set; }
        public bool AutoPlay { get; set; }
        public string Reentry { get; set; }
        public Dictionary<string, float> Parameters { get; set; }
        public List<ShaderTexture> Textures { get; set; }
        public List<ShaderBinding> Bindings { get; set; }

        public ShaderComponent()
        {
            Version = 1; Backend = "HLSL"; Source = ""; Width = 320; Height = 240;
            FramesPerSecond = 15; Reentry = "restart";
            Parameters = new Dictionary<string, float>(); Textures = new List<ShaderTexture>();
            Bindings = new List<ShaderBinding>();
        }

        public void Validate()
        {
            if (Version != 1) throw new InvalidDataException(global::PptEase.Localization.L.T("不支持的 Shader 组件版本。"));
            if (Backend != "HLSL" && Backend != "GLSL") throw new InvalidDataException(global::PptEase.Localization.L.T("Shader 后端必须为 HLSL 或 GLSL。"));
            if (String.IsNullOrWhiteSpace(Source) || Source.Length > 262144) throw new InvalidDataException(global::PptEase.Localization.L.T("Shader 源码为空或超过限制。"));
            if (Width < 16 || Height < 16 || Width > 2048 || Height > 2048 || FramesPerSecond < 1 || FramesPerSecond > 60)
                throw new InvalidDataException(global::PptEase.Localization.L.T("Shader 分辨率或帧率超出范围。"));
            if (Reentry != "restart" && Reentry != "resume") throw new InvalidDataException(global::PptEase.Localization.L.T("未知的再次进入行为。"));
            if (Parameters == null || Parameters.Count > 64 || Textures == null || Textures.Count > 8) throw new InvalidDataException(global::PptEase.Localization.L.T("Shader 参数或纹理数量超出限制。"));
            foreach (var parameter in Parameters)
                if (!Identifier(parameter.Key) || Single.IsNaN(parameter.Value) || Single.IsInfinity(parameter.Value)) throw new InvalidDataException(global::PptEase.Localization.L.T("Shader 参数名或值无效。"));
            var names = new HashSet<string>();
            if(Bindings==null||Bindings.Count>64)throw new InvalidDataException(global::PptEase.Localization.L.T("参数绑定数量无效。"));
            foreach(var binding in Bindings)if(binding==null||!Parameters.ContainsKey(binding.Parameter)||binding.ShapeId<=0||Single.IsNaN(binding.Minimum)||Single.IsNaN(binding.Maximum)||Single.IsInfinity(binding.Minimum)||Single.IsInfinity(binding.Maximum)||binding.Minimum>=binding.Maximum)throw new InvalidDataException(global::PptEase.Localization.L.T("参数绑定无效。"));
            long textureBytes = 0;
            foreach (var texture in Textures)
            {
                if (texture == null || !Identifier(texture.Name) || !names.Add(texture.Name) || Parameters.ContainsKey(texture.Name)) throw new InvalidDataException(global::PptEase.Localization.L.T("Shader 纹理名称无效或重复。"));
                if (texture.Mime != "image/png" && texture.Mime != "image/jpeg") throw new InvalidDataException(global::PptEase.Localization.L.T("纹理仅支持嵌入 PNG/JPEG。"));
                if (String.IsNullOrEmpty(texture.Data) || texture.Data.Length > ShaderPayload.MaximumBytes) throw new InvalidDataException(global::PptEase.Localization.L.T("纹理数据为空或超限。"));
                textureBytes += Convert.FromBase64String(texture.Data).Length;
                if (textureBytes > 2 * 1024 * 1024) throw new InvalidDataException(global::PptEase.Localization.L.T("嵌入纹理总量超过 2 MiB。"));
            }
        }

        private static bool Identifier(string name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > 64) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!(c == '_' || c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || i > 0 && c >= '0' && c <= '9')) return false;
            }
            return name != "time" && name != "resolution";
        }
    }
    public sealed class ShaderTexture
    {
        public string Name { get; set; }
        public string Mime { get; set; }
        public string Data { get; set; }
    }
    public sealed class ShaderBinding
    {
        public string Parameter {get;set;}
        public ShaderBinding(){Parameter="";Kind="ActiveX";}
        public int ShapeId {get;set;}
        public string Kind {get;set;}
        public int ThumbShapeId {get;set;}
        public bool Circular {get;set;}
        public bool Checkbox {get;set;}
        public int StylePreset {get;set;}
        public bool ShowValue {get;set;}
        public float Minimum {get;set;}
        public float Maximum {get;set;}
    }
}
