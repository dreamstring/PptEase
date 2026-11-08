using System;
using System.Web.Script.Serialization;
using PptEase.Shader;
class ShaderComponentTests
{
    static void Main()
    {
        var component = new ShaderComponent { Source = "float4 main():SV_TARGET{return 1;}" };
        component.Parameters.Add("roughness", .3f);
        component.Textures.Add(new ShaderTexture { Name = "albedo", Mime = "image/png", Data = "YWJj" });
        component.Validate();
        var serializer = new JavaScriptSerializer();
        var restored = serializer.Deserialize<ShaderComponent>(serializer.Serialize(component));
        restored.Validate();
        Check(restored.Source == component.Source && restored.Textures[0].Data == "YWJj" && restored.Parameters["roughness"] == .3f, "contract round trip");
        restored.Parameters["roughness"] = Single.NaN;
        Reject(restored, "nonfinite parameter");
        restored.Parameters["roughness"] = .3f; restored.Version = 2;
        Reject(restored, "unknown schema version");
        restored.Version = 1; restored.Width = 100000;
        Reject(restored, "oversized render target");
        restored.Width = 320; restored.Textures.Add(restored.Textures[0]);
        Reject(restored, "duplicate texture binding");
    }
    static void Reject(ShaderComponent c, string name) { bool rejected = false; try { c.Validate(); } catch { rejected = true; } Check(rejected, name); }
    static void Check(bool result, string name) { if (!result) throw new Exception(name); Console.WriteLine("PASS " + name); }
}
