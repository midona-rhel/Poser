using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace Crystarium.Capture;

internal sealed class Dx11Renderer : IDisposable
{
    private const string ShaderSource = """
        cbuffer vertexBuffer : register(b0) { float4x4 ProjectionMatrix; };
        struct VS_INPUT { float2 pos : POSITION; float2 uv : TEXCOORD0; float4 col : COLOR0; };
        struct PS_INPUT { float4 pos : SV_POSITION; float4 col : COLOR0; float2 uv : TEXCOORD0; };
        PS_INPUT VSMain(VS_INPUT input) {
            PS_INPUT output;
            output.pos = mul(ProjectionMatrix, float4(input.pos.xy, 0.f, 1.f));
            output.col = input.col;
            output.uv = input.uv;
            return output;
        }
        Texture2D texture0 : register(t0);
        SamplerState sampler0 : register(s0);
        float4 PSMain(PS_INPUT input) : SV_Target {
            return input.col * texture0.Sample(sampler0, input.uv);
        }
        """;

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGISwapChain1 _swapChain = null!;
    private ID3D11RenderTargetView? _renderTarget;
    private ID3D11VertexShader _vertexShader = null!;
    private ID3D11PixelShader _pixelShader = null!;
    private ID3D11InputLayout _inputLayout = null!;
    private ID3D11Buffer _constantBuffer = null!;
    private ID3D11BlendState _blendState = null!;
    private ID3D11RasterizerState _rasterizerState = null!;
    private ID3D11DepthStencilState _depthStencilState = null!;
    private ID3D11SamplerState _sampler = null!;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;
    private int _vertexCapacity = 8192;
    private int _indexCapacity = 16384;
    private readonly Dictionary<ulong, ID3D11ShaderResourceView> _textures = [];

    public void Initialize(nint window, int width, int height)
    {
        var result = D3D11.D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out _device, out _context);
        if (result.Failure)
        {
            result = D3D11.D3D11CreateDevice(
                null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out _device, out _context);
        }
        result.CheckError();

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        _swapChain = factory.CreateSwapChainForHwnd(
            _device,
            window,
            new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                SwapEffect = SwapEffect.FlipDiscard,
            });
        CreateRenderTarget();
        CreateDeviceObjects();
    }

    private void CreateRenderTarget()
    {
        using var backbuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTarget = _device.CreateRenderTargetView(backbuffer);
    }

    private void CreateDeviceObjects()
    {
        var vertexBytecode = Compiler.Compile(
            ShaderSource, "VSMain", "uiconformance-vs", "vs_4_0");
        var pixelBytecode = Compiler.Compile(
            ShaderSource, "PSMain", "uiconformance-ps", "ps_4_0");
        _vertexShader = _device.CreateVertexShader(vertexBytecode.Span);
        _pixelShader = _device.CreatePixelShader(pixelBytecode.Span);
        _inputLayout = _device.CreateInputLayout(
            [
                new("POSITION", 0, Format.R32G32_Float, 0, 0),
                new("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
                new("COLOR", 0, Format.R8G8B8A8_UNorm, 16, 0),
            ],
            vertexBytecode.Span);
        _constantBuffer = _device.CreateBuffer(new BufferDescription(
            64, BindFlags.ConstantBuffer, ResourceUsage.Dynamic,
            CpuAccessFlags.Write));

        var blend = new BlendDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.SourceAlpha,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blendState = _device.CreateBlendState(blend);
        _rasterizerState = _device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            ScissorEnable = true,
            DepthClipEnable = true,
        });
        _depthStencilState = _device.CreateDepthStencilState(
            new DepthStencilDescription
            {
                DepthEnable = false,
                StencilEnable = false,
            });
        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunc = ComparisonFunction.Always,
        });
    }

    public unsafe nint CreateTexture(byte* pixels, int width, int height)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
        };
        using var texture = _device.CreateTexture2D(
            in description,
            [new SubresourceData((nint)pixels, (uint)(width * 4))]);
        var view = _device.CreateShaderResourceView(texture);
        _textures[(ulong)view.NativePointer] = view;
        return view.NativePointer;
    }

    public void DestroyTexture(nint id)
    {
        if (_textures.Remove((ulong)id, out var texture))
            texture.Dispose();
    }

    public void BeginFrame(Vector4 clear)
    {
        _context.OMSetRenderTargets(_renderTarget!);
        _context.ClearRenderTargetView(
            _renderTarget,
            new Color4(clear.X, clear.Y, clear.Z, clear.W));
    }

    public void Present() => _swapChain.Present(0, PresentFlags.None);

    public unsafe void Render(ImDrawDataPtr drawData)
    {
        if (drawData.IsNull || drawData.CmdListsCount == 0)
            return;

        EnsureBuffers(drawData.TotalVtxCount, drawData.TotalIdxCount);
        var vertices = _context.Map(_vertexBuffer!, 0, MapMode.WriteDiscard);
        var indices = _context.Map(_indexBuffer!, 0, MapMode.WriteDiscard);
        var vertexTarget = (ImDrawVert*)vertices.DataPointer;
        var indexTarget = (ushort*)indices.DataPointer;
        for (int i = 0; i < drawData.CmdListsCount; i++)
        {
            var list = new ImDrawListPtr(drawData.CmdLists[i]);
            long vertexBytes = list.VtxBuffer.Size * sizeof(ImDrawVert);
            long indexBytes = list.IdxBuffer.Size * sizeof(ushort);
            Buffer.MemoryCopy(
                list.VtxBuffer.Data, vertexTarget, vertexBytes, vertexBytes);
            Buffer.MemoryCopy(
                list.IdxBuffer.Data, indexTarget, indexBytes, indexBytes);
            vertexTarget += list.VtxBuffer.Size;
            indexTarget += list.IdxBuffer.Size;
        }
        _context.Unmap(_vertexBuffer!, 0);
        _context.Unmap(_indexBuffer!, 0);

        float left = drawData.DisplayPos.X;
        float right = left + drawData.DisplaySize.X;
        float top = drawData.DisplayPos.Y;
        float bottom = top + drawData.DisplaySize.Y;
        var projection = new Matrix4x4(
            2f / (right - left), 0, 0, 0,
            0, 2f / (top - bottom), 0, 0,
            0, 0, 0.5f, 0,
            (right + left) / (left - right),
            (top + bottom) / (bottom - top),
            0.5f, 1);
        var mapped = _context.Map(
            _constantBuffer, 0, MapMode.WriteDiscard);
        Buffer.MemoryCopy(
            &projection, (void*)mapped.DataPointer, 64, 64);
        _context.Unmap(_constantBuffer, 0);

        _context.IASetInputLayout(_inputLayout);
        _context.IASetVertexBuffer(
            0, _vertexBuffer!, (uint)sizeof(ImDrawVert));
        _context.IASetIndexBuffer(
            _indexBuffer, Format.R16_UInt, 0);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShader(_pixelShader);
        _context.PSSetSampler(0, _sampler);
        _context.OMSetBlendState(
            _blendState, new Color4(0, 0, 0, 0));
        _context.OMSetDepthStencilState(_depthStencilState);
        _context.RSSetState(_rasterizerState);
        _context.RSSetViewport(
            0, 0, drawData.DisplaySize.X, drawData.DisplaySize.Y);

        int vertexOffset = 0;
        uint indexOffset = 0;
        for (int i = 0; i < drawData.CmdListsCount; i++)
        {
            var list = new ImDrawListPtr(drawData.CmdLists[i]);
            for (int commandIndex = 0;
                 commandIndex < list.CmdBuffer.Size;
                 commandIndex++)
            {
                var command = list.CmdBuffer.Data[commandIndex];
                if (command.UserCallback != null)
                    continue;
                int x = (int)(command.ClipRect.X - drawData.DisplayPos.X);
                int y = (int)(command.ClipRect.Y - drawData.DisplayPos.Y);
                int width = (int)(command.ClipRect.Z
                    - drawData.DisplayPos.X) - x;
                int height = (int)(command.ClipRect.W
                    - drawData.DisplayPos.Y) - y;
                if (width <= 0 || height <= 0)
                    continue;
                _context.RSSetScissorRect(x, y, width, height);
                if (_textures.TryGetValue(
                    command.TextureId.Handle, out var texture))
                    _context.PSSetShaderResource(0, texture);
                _context.DrawIndexed(
                    command.ElemCount,
                    indexOffset + command.IdxOffset,
                    vertexOffset + (int)command.VtxOffset);
            }
            vertexOffset += list.VtxBuffer.Size;
            indexOffset += (uint)list.IdxBuffer.Size;
        }
    }

    private unsafe void EnsureBuffers(int vertices, int indices)
    {
        if (_vertexBuffer == null || _vertexCapacity < vertices)
        {
            _vertexBuffer?.Dispose();
            _vertexCapacity = vertices + 5000;
            _vertexBuffer = _device.CreateBuffer(new BufferDescription(
                (uint)(_vertexCapacity * sizeof(ImDrawVert)),
                BindFlags.VertexBuffer, ResourceUsage.Dynamic,
                CpuAccessFlags.Write));
        }
        if (_indexBuffer == null || _indexCapacity < indices)
        {
            _indexBuffer?.Dispose();
            _indexCapacity = indices + 10000;
            _indexBuffer = _device.CreateBuffer(new BufferDescription(
                (uint)(_indexCapacity * sizeof(ushort)),
                BindFlags.IndexBuffer, ResourceUsage.Dynamic,
                CpuAccessFlags.Write));
        }
    }

    public unsafe void SaveBackbuffer(string path)
    {
        using var source = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        var description = source.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = _device.CreateTexture2D(in description);
        _context.CopyResource(staging, source);
        var mapped = _context.Map(staging, 0);
        try
        {
            using var bitmap = new Bitmap(
                (int)description.Width,
                (int)description.Height,
                PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly,
                bitmap.PixelFormat);
            try
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    var sourceRow = (byte*)mapped.DataPointer
                        + y * mapped.RowPitch;
                    var targetRow = (byte*)data.Scan0
                        + y * data.Stride;
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        targetRow[x * 4] = sourceRow[x * 4 + 2];
                        targetRow[x * 4 + 1] = sourceRow[x * 4 + 1];
                        targetRow[x * 4 + 2] = sourceRow[x * 4];
                        targetRow[x * 4 + 3] = 255;
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            Directory.CreateDirectory(
                Path.GetDirectoryName(Path.GetFullPath(path))!);
            bitmap.Save(path, ImageFormat.Png);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    public void Dispose()
    {
        foreach (var texture in _textures.Values)
            texture.Dispose();
        _textures.Clear();
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _sampler?.Dispose();
        _depthStencilState?.Dispose();
        _rasterizerState?.Dispose();
        _blendState?.Dispose();
        _constantBuffer?.Dispose();
        _inputLayout?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _renderTarget?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
