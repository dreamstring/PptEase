using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace PptEase.Shader;

/// <summary>Hardware D3D11 renderer with direct DXGI presentation and separate static-cover readback.</summary>
public sealed partial class D3DShaderRenderer : IDisposable
{
    public const string DefaultSource = """
        cbuffer PreviewParameters : register(b0) {
            float time; float roughness; float2 resolution;
        };
        float4 main(float4 pos : SV_POSITION) : SV_TARGET {
            float2 uv = pos.xy / resolution;
            float checker = fmod(floor(uv.x * 10) + floor(uv.y * 10), 2);
            float3 base = lerp(float3(0.08,0.16,0.25), float3(0.92,0.48,0.18), checker);
            float glow = 0.55 + 0.45 * sin(time + uv.x * 6.2831853);
            return float4(base * glow * (1 - roughness * 0.55), 1);
        }
        """;
    private const string VertexSource = """
        float4 main(uint id : SV_VertexID) : SV_POSITION {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2,-2) + float2(-1,1), 0, 1);
        }
        """;
    private IntPtr _device, _context, _vertexShader, _pixelShader, _texture, _staging, _renderTarget, _constants, _rasterizer;
    private bool _disposed;
    internal static int LiveInstances;
    internal static uint LastDeviceReleaseCount, LastContextReleaseCount;
    internal static bool ForceWarpForTest;
    private IntPtr _customConstants, _sampler;
    private IntPtr _swapChain;
    public long PresentedFrames { get; private set; }
    public long ReadbackFrames { get; private set; }
    private string[] _scalarNames = Array.Empty<string>();
    private readonly List<IntPtr> _views = new();
    public int Width { get; }
    public int Height { get; }
    public uint FeatureLevel { get; private set; }
    public string DeviceKind { get; private set; } = "uninitialized";

    public D3DShaderRenderer(int width = 320, int height = 240)
    {
        System.Threading.Interlocked.Increment(ref LiveInstances);
        Width = width; Height = height;
        try
        {
            if (!TryRentDevice()) {
            int deviceResult = D3D11CreateDevice(IntPtr.Zero, ForceWarpForTest ? 5u : 1u, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7,
                out _device, out uint level, out _context);
            DeviceKind = ForceWarpForTest ? "WARP test" : "Hardware";
            if (deviceResult < 0) {
                Release(ref _context); Release(ref _device);
                deviceResult = D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7,
                    out _device, out level, out _context);
                DeviceKind = "WARP fallback";
            }
            Check(deviceResult, "D3D11 device");
            FeatureLevel = level;
            }
            var descriptor = new TextureDesc { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = 28, SampleCount = 1, BindFlags = 0x20 };
            Check(Method<CreateTexture>(_device, 5)(_device, ref descriptor, IntPtr.Zero, out _texture), "render texture");
            Check(Method<CreateView>(_device, 9)(_device, _texture, IntPtr.Zero, out _renderTarget), "render target view");
            descriptor.BindFlags = 0; descriptor.Usage = 3; descriptor.CpuAccessFlags = 0x20000;
            Check(Method<CreateTexture>(_device, 5)(_device, ref descriptor, IntPtr.Zero, out _staging), "readback texture");
            var buffer = new BufferDesc { ByteWidth = 16, BindFlags = 4 };
            Check(Method<CreateBuffer>(_device, 3)(_device, ref buffer, IntPtr.Zero, out _constants), "constant buffer");
            var raster = new RasterizerDesc { FillMode = 3, CullMode = 1, DepthClipEnable = 1 };
            Check(Method<CreateRasterizer>(_device, 22)(_device, ref raster, out _rasterizer), "rasterizer");
            byte[] vs = Compile(VertexSource, "vs_5_0");
            Check(Method<CreateShader>(_device, 12)(_device, vs, (UIntPtr)vs.Length, IntPtr.Zero, out _vertexShader), "vertex shader");
            CompilePixelShader(DefaultSource);
        }
        catch { Dispose(); throw; }
    }

    public void CompilePixelShader(string source)
    {
        ThrowIfDisposed();
        byte[] bytes = Compile(source, "ps_5_0");
        Check(Method<CreateShader>(_device, 15)(_device, bytes, (UIntPtr)bytes.Length, IntPtr.Zero, out IntPtr shader), "pixel shader");
        // A rejected replacement preserves the last working shader.
        Release(ref _pixelShader); _pixelShader = shader;
    }

    private void DrawFrame(float time, float roughness)
    {
        ThrowIfDisposed();
        IntPtr values = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.Copy(new[] { time, roughness, (float)Width, (float)Height }, 0, values, 4);
            Method<Update>(_context, 48)(_context, _constants, 0, IntPtr.Zero, values, 0, 0);
        }
        finally { Marshal.FreeHGlobal(values); }
        Method<SetShader>(_context, 11)(_context, _vertexShader, IntPtr.Zero, 0);
        Method<SetShader>(_context, 9)(_context, _pixelShader, IntPtr.Zero, 0);
        Method<SetBuffers>(_context, 16)(_context, 0, 1, new[] { _constants });
        Method<SetBuffers>(_context, 16)(_context, 1, 1, new[] { _customConstants });
        var views = new IntPtr[8]; _views.CopyTo(views);
        Method<SetBuffers>(_context, 8)(_context, 0, 8, views);
        Method<SetBuffers>(_context, 10)(_context, 0, 1, new[] { _sampler });
        Method<SetTopology>(_context, 24)(_context, 4);
        Method<SetRasterizer>(_context, 43)(_context, _rasterizer);
        Method<SetTargets>(_context, 33)(_context, 1, new[] { _renderTarget }, IntPtr.Zero);
        var viewport = new Viewport { Width = Width, Height = Height, MaxDepth = 1 };
        Method<SetViewports>(_context, 44)(_context, 1, ref viewport);
        Method<ClearTarget>(_context, 50)(_context, _renderTarget, new[] { 0f, 0f, 0f, 0f });
        Method<Draw>(_context, 13)(_context, 3, 0);
    }

    public byte[] Render(float time, float roughness)
    {
        if (_swapChain != IntPtr.Zero) throw new InvalidOperationException("Direct presentation does not read frames back. Use PresentFrame.");
        DrawFrame(time, roughness); ReadbackFrames++;
        Method<CopyResource>(_context, 47)(_context, _staging, _texture);
        Check(Method<Map>(_context, 14)(_context, _staging, 0, 1, 0, out Mapped mapped), "staging map");
        try
        {
            byte[] rgba = new byte[Width * Height * 4];
            for (int y = 0; y < Height; y++) Marshal.Copy(IntPtr.Add(mapped.Data, checked((int)mapped.RowPitch * y)), rgba, y * Width * 4, Width * 4);
            for (int p = 0; p < rgba.Length; p += 4) (rgba[p], rgba[p + 2]) = (rgba[p + 2], rgba[p]);
            return rgba; // BGRA for WPF BitmapSource.
        }
        finally { Method<Unmap>(_context, 15)(_context, _staging, 0); }
    }

    public void AttachPresentationWindow(IntPtr hwnd)
    {
        ThrowIfDisposed();
        if (hwnd == IntPtr.Zero || _swapChain != IntPtr.Zero) throw new ArgumentException("A new valid presentation window is required.");
        if (DeviceKind != "Hardware") throw new InvalidOperationException("GPU presentation requires a hardware device.");
        IntPtr factory=IntPtr.Zero, chain=IntPtr.Zero, texture=IntPtr.Zero, target=IntPtr.Zero;
        try {
            var factoryId=new Guid("50C83A1C-E072-4C48-87B0-3630FA36A6D0");
            Check(CreateDXGIFactory1(ref factoryId,out factory),"DXGI factory2");
            var desc=new SwapChainDesc { Width=(uint)Width, Height=(uint)Height, Format=28, SampleCount=1, BufferUsage=0x20, BufferCount=2, SwapEffect=4, AlphaMode=3 };
            Check(Method<CreateSwapChainForWindow>(factory,15)(factory,_device,hwnd,ref desc,IntPtr.Zero,IntPtr.Zero,out chain),"flip-model swap chain");
            var textureId=new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
            Check(Method<GetSwapBuffer>(chain,9)(chain,0,ref textureId,out texture),"swap-chain buffer");
            Check(Method<CreateView>(_device,9)(_device,texture,IntPtr.Zero,out target),"swap-chain render target");
            Method<SetTargets>(_context,33)(_context,0,Array.Empty<IntPtr>(),IntPtr.Zero);
            Release(ref _renderTarget);Release(ref _texture);Release(ref _staging);
            _renderTarget=target;target=IntPtr.Zero;_texture=texture;texture=IntPtr.Zero;_swapChain=chain;chain=IntPtr.Zero;
        } finally {Release(ref target);Release(ref texture);Release(ref chain);Release(ref factory);}
    }

    public int PresentFrame(float time,float roughness,bool vsync=true)
    {
        ThrowIfDisposed();
        if(_swapChain==IntPtr.Zero)throw new InvalidOperationException("No presentation target.");
        DrawFrame(time,roughness);
        int result=Method<PresentSwapChain>(_swapChain,8)(_swapChain,vsync?1u:0u,0);
        Check(result,"DXGI Present");PresentedFrames++;return result;
    }

    public void UpdateParameters(IDictionary<string,float> values)
    {
        ThrowIfDisposed();
        if(_scalarNames.Length==0)return;
        int count=(_scalarNames.Length+3)/4*4;
        var data=new float[count];
        for(int i=0;i<_scalarNames.Length;i++){
            if(!values.TryGetValue(_scalarNames[i],out float value)||!float.IsFinite(value))throw new ArgumentException("Missing or invalid parameter: "+_scalarNames[i]);
            data[i]=value;
        }
        IntPtr memory=Marshal.AllocHGlobal(count*4);
        try{Marshal.Copy(data,0,memory,count);Method<Update>(_context,48)(_context,_customConstants,0,IntPtr.Zero,memory,0,0);}
        finally{Marshal.FreeHGlobal(memory);}
    }

    public sealed class InputTexture
    {
        public string Name { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public byte[] Rgba { get; set; } = Array.Empty<byte>();
    }

    // b0 remains the documented time/roughness/resolution block. Custom scalars are
    // generated into b1 with explicit float offsets; textures use t0..t7 and peSampler s0.
    public void CompileComponent(string source, IDictionary<string,float> parameters, IList<InputTexture> textures)
    {
        ThrowIfDisposed();
        var scalars = parameters.Where(p => p.Key != "roughness").OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        if (scalars.Length > 64 || textures.Count > 8) throw new ArgumentException("Too many bindings.");
        var names = new HashSet<string>();
        bool Valid(string name) => !string.IsNullOrEmpty(name) && name.Length <= 64 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '_')) && name != "time" && name != "resolution" && name != "peSampler";
        var header = new StringBuilder();
        if (scalars.Length > 0) {
            header.AppendLine("cbuffer PptEaseCustomParameters : register(b1) {");
            for (int i = 0; i < scalars.Length; i++) {
                if (!Valid(scalars[i].Key) || !names.Add(scalars[i].Key) || !float.IsFinite(scalars[i].Value)) throw new ArgumentException("Invalid scalar binding.");
                header.AppendLine($"float {scalars[i].Key} : packoffset(c{i / 4}.{"xyzw"[i % 4]});");
            }
            header.AppendLine("};");
        }
        for (int i = 0; i < textures.Count; i++) {
            var t = textures[i];
            if (!Valid(t.Name) || !names.Add(t.Name) || t.Width < 1 || t.Height < 1 || t.Width > 2048 || t.Height > 2048 || t.Rgba.Length != t.Width * t.Height * 4) throw new ArgumentException("Invalid texture binding.");
            header.AppendLine($"Texture2D {t.Name} : register(t{i});");
        }
        if (textures.Count > 0) header.AppendLine("SamplerState peSampler : register(s0);");
        byte[] compiled = Compile(header + "\n#line 1\n" + source.TrimStart('\uFEFF'), "ps_5_0");
        IntPtr nextShader = IntPtr.Zero, nextConstants = IntPtr.Zero, nextSampler = IntPtr.Zero;
        var nextViews = new List<IntPtr>();
        try {
            Check(Method<CreateShader>(_device, 15)(_device, compiled, (UIntPtr)compiled.Length, IntPtr.Zero, out nextShader), "component pixel shader");
            if (scalars.Length > 0) {
                int count = (scalars.Length + 3) / 4 * 4;
                var desc = new BufferDesc { ByteWidth = (uint)count * 4, BindFlags = 4 };
                Check(Method<CreateBuffer>(_device, 3)(_device, ref desc, IntPtr.Zero, out nextConstants), "custom constants");
                var values = new float[count]; for (int i = 0; i < scalars.Length; i++) values[i] = scalars[i].Value;
                IntPtr memory = Marshal.AllocHGlobal(count * 4);
                try { Marshal.Copy(values, 0, memory, count); Method<Update>(_context, 48)(_context, nextConstants, 0, IntPtr.Zero, memory, 0, 0); }
                finally { Marshal.FreeHGlobal(memory); }
            }
            foreach (var input in textures) {
                IntPtr texture = IntPtr.Zero, view = IntPtr.Zero;
                var desc = new TextureDesc { Width = (uint)input.Width, Height = (uint)input.Height, MipLevels = 1, ArraySize = 1, Format = 28, SampleCount = 1, BindFlags = 8 };
                try {
                    Check(Method<CreateTexture>(_device, 5)(_device, ref desc, IntPtr.Zero, out texture), "input texture");
                    var pin = GCHandle.Alloc(input.Rgba, GCHandleType.Pinned);
                    try { Method<Update>(_context, 48)(_context, texture, 0, IntPtr.Zero, pin.AddrOfPinnedObject(), (uint)input.Width * 4, 0); } finally { pin.Free(); }
                    Check(Method<CreateView>(_device, 7)(_device, texture, IntPtr.Zero, out view), "input texture view");
                    nextViews.Add(view); view = IntPtr.Zero;
                } finally { Release(ref view); Release(ref texture); }
            }
            if (textures.Count > 0) {
                var desc = new SamplerDesc { Filter = 0x15, AddressU = 1, AddressV = 1, AddressW = 1, MaxAnisotropy = 1, ComparisonFunc = 1, MaxLod = float.MaxValue };
                Check(Method<CreateSampler>(_device, 23)(_device, ref desc, out nextSampler), "input sampler");
            }
            Release(ref _pixelShader); _pixelShader = nextShader; nextShader = IntPtr.Zero;
            Release(ref _customConstants); _customConstants = nextConstants; nextConstants = IntPtr.Zero;
            _scalarNames=scalars.Select(p=>p.Key).ToArray();
            Release(ref _sampler); _sampler = nextSampler; nextSampler = IntPtr.Zero;
            foreach (var old in _views) { var value = old; Release(ref value); } _views.Clear(); _views.AddRange(nextViews); nextViews.Clear();
        } finally {
            Release(ref nextShader); Release(ref nextConstants); Release(ref nextSampler);
            foreach (var next in nextViews) { var value = next; Release(ref value); }
        }
    }

    private static readonly Dictionary<string,byte[]> BytecodeCache=new();
    public static byte[] Compile(string source, string profile)
    {
        string key=profile+"\n"+source;
        lock(BytecodeCache)if(BytecodeCache.TryGetValue(key,out var cached))return cached;
        byte[] utf8 = Encoding.UTF8.GetBytes(source);
        int hr = D3DCompile(utf8, (UIntPtr)utf8.Length, "PptEase-preview", IntPtr.Zero, IntPtr.Zero,
            "main", profile, 1 << 11, 0, out IntPtr blob, out IntPtr errors);
        string detail = "";
        try
        {
            if (errors != IntPtr.Zero) detail = Encoding.UTF8.GetString(ReadBlob(errors)).TrimEnd('\0');
            if (hr < 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? $"HLSL 编译失败：0x{hr:X8}" : detail);
            var bytes=ReadBlob(blob);
            lock(BytecodeCache){if(BytecodeCache.Count>=24)BytecodeCache.Clear();BytecodeCache[key]=bytes;}
            return bytes;
        }
        finally { Release(ref blob); Release(ref errors); }
    }
    private static byte[] ReadBlob(IntPtr blob)
    {
        if (blob == IntPtr.Zero) return Array.Empty<byte>();
        IntPtr pointer = Method<GetPointer>(blob, 3)(blob);
        int length = checked((int)Method<GetLength>(blob, 4)(blob).ToUInt64());
        byte[] bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length); return bytes;
    }
    private static T Method<T>(IntPtr obj, int index) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size));
    private static void Check(int hr, string operation) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(D3DShaderRenderer)); }
    private static uint Release(ref IntPtr pointer) { if (pointer == IntPtr.Zero) return 0; uint remaining=Method<ReleaseDelegate>(pointer, 2)(pointer); pointer=IntPtr.Zero; return remaining; }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        System.Threading.Interlocked.Decrement(ref LiveInstances);
        if(_context!=IntPtr.Zero)Method<ContextVoid>(_context,110)(_context);
        Release(ref _customConstants); Release(ref _sampler);
        foreach (var view in _views) { var value = view; Release(ref value); } _views.Clear();
        Release(ref _pixelShader); Release(ref _vertexShader); Release(ref _renderTarget); Release(ref _texture);
        Release(ref _staging); Release(ref _constants); Release(ref _rasterizer); Release(ref _swapChain);
        // Flush after releasing resources so deferred destruction includes those resources.
        if(_context!=IntPtr.Zero)Method<ContextVoid>(_context,111)(_context);
        ReturnDevice();
    }
    [StructLayout(LayoutKind.Sequential)] private struct TextureDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct SwapChainDesc { public uint Width,Height,Format,Stereo,SampleCount,SampleQuality,BufferUsage,BufferCount,Scaling,SwapEffect,AlphaMode,Flags; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateSwapChainForWindow(IntPtr self,IntPtr device,IntPtr hwnd,ref SwapChainDesc desc,IntPtr fullscreen,IntPtr output,out IntPtr chain);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSwapBuffer(IntPtr self,uint index,ref Guid id,out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PresentSwapChain(IntPtr self,uint interval,uint flags);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid id,out IntPtr factory);
    [StructLayout(LayoutKind.Sequential)] private struct BufferDesc { public uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride; }
    [StructLayout(LayoutKind.Sequential)] private struct RasterizerDesc { public uint FillMode, CullMode; public int FrontCounterClockwise, DepthBias; public float DepthBiasClamp, SlopeScaledDepthBias; public int DepthClipEnable, ScissorEnable, MultisampleEnable, AntialiasedLineEnable; }
    [StructLayout(LayoutKind.Sequential)] private struct Viewport { public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth; }
    [StructLayout(LayoutKind.Sequential)] private struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }
    [StructLayout(LayoutKind.Sequential)] private struct SamplerDesc { public uint Filter, AddressU, AddressV, AddressW; public float MipLodBias; public uint MaxAnisotropy, ComparisonFunc; public float Border0, Border1, Border2, Border3, MinLod, MaxLod; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateSampler(IntPtr self, ref SamplerDesc desc, out IntPtr sampler);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseDelegate(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ContextVoid(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetPointer(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate UIntPtr GetLength(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture(IntPtr self, ref TextureDesc desc, IntPtr data, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateBuffer(IntPtr self, ref BufferDesc desc, IntPtr data, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateView(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateRasterizer(IntPtr self, ref RasterizerDesc desc, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateShader(IntPtr self, byte[] bytecode, UIntPtr length, IntPtr linkage, out IntPtr shader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetShader(IntPtr self, IntPtr shader, IntPtr classes, uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetBuffers(IntPtr self, uint start, uint count, IntPtr[] buffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetTopology(IntPtr self, uint topology);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetRasterizer(IntPtr self, IntPtr state);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetTargets(IntPtr self, uint count, IntPtr[] targets, IntPtr depth);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SetViewports(IntPtr self, uint count, ref Viewport viewport);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ClearTarget(IntPtr self, IntPtr target, float[] color);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Draw(IntPtr self, uint vertexCount, uint startVertex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyResource(IntPtr self, IntPtr destination, IntPtr source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Update(IntPtr self, IntPtr destination, uint subresource, IntPtr box, IntPtr source, uint rowPitch, uint depthPitch);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Map(IntPtr self, IntPtr resource, uint subresource, uint mapType, uint flags, out Mapped mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Unmap(IntPtr self, IntPtr resource, uint subresource);
    [DllImport("d3d11.dll", CallingConvention = CallingConvention.StdCall)] private static extern int D3D11CreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, IntPtr levels, uint levelCount, uint sdkVersion, out IntPtr device, out uint featureLevel, out IntPtr context);
    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)] private static extern int D3DCompile(byte[] data, UIntPtr size, string sourceName, IntPtr defines, IntPtr include, string entryPoint, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);
}
