using System;
using System.Collections.Generic;
using System.IO;
using PptEase.Shader;

class ShaderPayloadTests
{
    static void Main()
    {
        var tags = new Dictionary<string, string>();
        Func<string, string> read = k => tags.ContainsKey(k) ? tags[k] : "";
        Action<string, string> write = (k, v) => tags[k.ToUpperInvariant()] = v.ToUpperInvariant();
        string source = "{\"source\":\"float4 main() { return float4(0,1,0,1); } // 中文\",\"texture\":\"aBcDeF==\"}";
        ShaderPayload.Write(source, read, write);
        Check(ShaderPayload.Read(read) == source, "case-sensitive shader and texture survive uppercase tags");
        string original = tags[ShaderPayload.ManifestKey];
        try { ShaderPayload.Write("replacement", read, (k,v) => { throw new IOException("simulated write failure"); }); }
        catch (IOException) { }
        Check(tags[ShaderPayload.ManifestKey] == original && ShaderPayload.Read(read) == source, "failed update preserves committed payload");
        var copy = new Dictionary<string,string>(tags);
        Check(ShaderPayload.Read(k => copy.ContainsKey(k) ? copy[k] : "") == source, "copied shape payload is self-contained");
        var updated = new Dictionary<string,string>(tags);
        Func<string,string> readUpdated = k => updated.ContainsKey(k) ? updated[k] : "";
        for(int i=0;i<20;i++) ShaderPayload.Write(source + i,readUpdated,(k,v)=>updated[k]=v,k=>updated.Remove(k));
        int chunks = Int32.Parse(updated[ShaderPayload.ManifestKey].Split(':')[2]);
        Check(updated.Count == chunks + 1 && ShaderPayload.Read(readUpdated) == source + 19, "repeated saves clean obsolete chunks");
        ShaderPayload.Write(source,readUpdated,(k,v)=>updated[k]=v,k=>{throw new IOException("delete blocked");});
        Check(ShaderPayload.Read(readUpdated)==source,"cleanup failure does not invalidate committed data");
        string[] manifest = original.Split(':');
        tags[ShaderPayload.ManifestKey] = "1:" + manifest[1] + ":" + manifest[2] + ":" + new string('0',64);
        bool rejected = false;
        try { ShaderPayload.Read(read); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "checksum corruption rejected");
        tags[ShaderPayload.ManifestKey] = original;
        tags.Remove("PES_" + manifest[1] + "_00000");
        rejected = false;
        try { ShaderPayload.Read(read); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "missing chunk rejected");
    }
    static void Check(bool result, string message) { if (!result) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
