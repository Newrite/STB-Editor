using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace StbEditor.App.Graphics;

/// <summary>D3D11 device and the window swap chain. Feature level 11.0 — what the game itself needs.</summary>
internal sealed class GraphicsDevice : IDisposable
{
    private readonly IDXGISwapChain1 _swapChain;
    private ID3D11RenderTargetView? _backBufferView;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string AdapterName { get; }

    public ID3D11RenderTargetView BackBufferView => _backBufferView!;

    public GraphicsDevice(nint hwnd, int width, int height)
    {
        var flags = DeviceCreationFlags.BgraSupport;
#if DEBUG
        if (D3D11.SdkLayersAvailable()) flags |= DeviceCreationFlags.Debug;
#endif

        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            flags,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out ID3D11Device? device,
            out ID3D11DeviceContext? context).CheckError();

        Device = device!;
        Context = context!;

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        AdapterName = adapter.Description.Description;
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var description = new SwapChainDescription1
        {
            Width = (uint)Math.Max(width, 1),
            Height = (uint)Math.Max(height, 1),
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipDiscard,
            Scaling = Scaling.Stretch,
            AlphaMode = AlphaMode.Ignore,
        };

        _swapChain = factory.CreateSwapChainForHwnd(Device, hwnd, description);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

        Width = (int)description.Width;
        Height = (int)description.Height;
        CreateBackBufferView();
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width == Width && height == Height)) return;

        Context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
        _backBufferView?.Dispose();
        _backBufferView = null;

        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
        Width = width;
        Height = height;
        CreateBackBufferView();
    }

    public void BeginFrame(Color4 clear)
    {
        Context.OMSetRenderTargets(BackBufferView);
        Context.ClearRenderTargetView(BackBufferView, clear);
        Context.RSSetViewport(0, 0, Width, Height);
    }

    public void Present(bool vsync) => _swapChain.Present(vsync ? 1u : 0u, PresentFlags.None);

    /// <summary>Copies the back buffer to CPU memory as BGRA rows (for --screenshot checks).</summary>
    /// <summary>Copies a 4-byte-per-pixel texture to the CPU (BGRA order when <paramref name="swapRedBlue"/> on RGBA).</summary>
    public byte[] ReadTexture(ID3D11Texture2D texture, int width, int height, bool swapRedBlue)
    {
        var desc = texture.Description;
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.MiscFlags = ResourceOptionFlags.None;

        using var staging = Device.CreateTexture2D(desc);
        Context.CopyResource(staging, texture);
        var mapped = Context.Map(staging, 0, MapMode.Read);
        try
        {
            var pixels = new byte[width * height * 4];
            unsafe
            {
                for (var y = 0; y < height; y++)
                    new ReadOnlySpan<byte>((byte*)mapped.DataPointer + y * mapped.RowPitch, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
            }
            if (swapRedBlue)
                for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            return pixels;
        }
        finally
        {
            Context.Unmap(staging, 0);
        }
    }

    public byte[] ReadBackBuffer()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        var desc = backBuffer.Description;
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.MiscFlags = ResourceOptionFlags.None;

        using var staging = Device.CreateTexture2D(desc);
        Context.CopyResource(staging, backBuffer);

        var mapped = Context.Map(staging, 0, MapMode.Read);
        try
        {
            var pixels = new byte[Width * Height * 4];
            unsafe
            {
                for (var y = 0; y < Height; y++)
                {
                    var src = new ReadOnlySpan<byte>((byte*)mapped.DataPointer + y * mapped.RowPitch, Width * 4);
                    src.CopyTo(pixels.AsSpan(y * Width * 4));
                }
            }
            return pixels;
        }
        finally
        {
            Context.Unmap(staging, 0);
        }
    }

    private void CreateBackBufferView()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _backBufferView = Device.CreateRenderTargetView(backBuffer);
    }

    public void Dispose()
    {
        _backBufferView?.Dispose();
        _swapChain.Dispose();
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
    }
}
