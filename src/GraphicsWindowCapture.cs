using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SteamVrTranslator;

// The system compositor supplies only the selected HWND, including when another window covers it.
// These interfaces are the Windows SDK's Windows.Graphics.Capture and Direct3D11 interop ABI.
// Using the small desktop ABI keeps the existing SDK target and introduces no WinRT deployment dependency.
internal static unsafe class GraphicsWindowCapture
{
    private static readonly Guid ItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid Item = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid PoolStatics2 = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    private static readonly Guid DxgiAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid Closable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");

    [DllImport("combase")] private static extern int RoInitialize(uint type);
    [DllImport("combase")] private static extern void RoUninitialize();
    [DllImport("combase", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, uint length, out nint handle);
    [DllImport("combase")] private static extern int WindowsDeleteString(nint handle);
    [DllImport("combase")] private static extern int RoGetActivationFactory(nint name, in Guid id, out nint factory);
    [DllImport("d3d11")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint projected);

    private sealed class NativeRef(nint value, bool close = false) : IDisposable
    {
        public nint Value { get; } = value;
        public nint Slot(int index) => (*(nint**)Value)[index];
        public NativeRef Query(Guid id)
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(Value, in id, out var result));
            return new(result);
        }
        public void Dispose()
        {
            if (close && Marshal.QueryInterface(Value, in Closable, out var closable) >= 0)
            {
                // IClosable.Close is after IInspectable; release independently of its HRESULT.
                ((delegate* unmanaged[Stdcall]<nint,int>)(*(nint**)closable)[6])(closable);
                Marshal.Release(closable);
            }
            Marshal.Release(Value);
        }
    }

    private static NativeRef Factory(string name, Guid id)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, (uint)name.Length, out var hstring));
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, in id, out var result));
            return new(result);
        }
        finally { WindowsDeleteString(hstring); }
    }

    internal static Bitmap Read(nint hwnd, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            throw new NotSupportedException("指定窗口截图需要 Windows 10 1903 或更新版本。");
        int hr = RoInitialize(1); // MTA worker; never wait for a frame on the UI thread.
        Marshal.ThrowExceptionForHR(hr);
        try { return ReadFrame(hwnd, token); }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Windows 无法读取所选窗口。请检查窗口是否允许截图，或还原窗口后重试。", ex);
        }
        finally { RoUninitialize(); }
    }

    private static Bitmap ReadFrame(nint hwnd, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var interop = Factory("Windows.Graphics.Capture.GraphicsCaptureItem", ItemInterop);
        nint ptr;
        var iid = Item;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint,nint,Guid*,nint*,int>)interop.Slot(3))(interop.Value, hwnd, &iid, &ptr));
        using var item = new NativeRef(ptr);
        var size = ReadSize(item, 7);
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        using var ownDevice = device;
        using var ownContext = context;
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out ptr));
        using var projected = new NativeRef(ptr);
        using var factory = Factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", PoolStatics2);
        // IDirect3D11CaptureFramePoolStatics2.CreateFreeThreaded(device,format,bufferCount,size)
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint,nint,int,int,Size,nint*,int>)factory.Slot(6))(
            factory.Value, projected.Value, (int)Format.B8G8R8A8_UNorm, 1, size, &ptr));
        using var pool = new NativeRef(ptr, close:true);
        // IDirect3D11CaptureFramePool.CreateCaptureSession / IGraphicsCaptureSession.StartCapture
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint,nint,nint*,int>)pool.Slot(10))(pool.Value,item.Value,&ptr));
        using var session = new NativeRef(ptr, close:true);
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint,int>)session.Slot(6))(session.Value));
        var elapsed = Stopwatch.StartNew();
        do
        {
            token.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint,nint*,int>)pool.Slot(7))(pool.Value,&ptr)); // TryGetNextFrame
            if (ptr != 0)
            {
                using var frame = new NativeRef(ptr, close:true);
                var contentSize = ReadSize(frame, 8);
                if (contentSize != size) throw new InvalidOperationException("截图时窗口大小发生变化，请重新截图。");
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint,nint*,int>)frame.Slot(6))(frame.Value,&ptr)); // Surface
                using var surface = new NativeRef(ptr);
                using var access = surface.Query(DxgiAccess);
                iid = Texture2D;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)access.Slot(3))(access.Value,&iid,&ptr));
                using var texture = new ID3D11Texture2D(ptr);
                return ReadPixels(device, context, texture, size, token);
            }
            token.WaitHandle.WaitOne(12);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(3));
        throw new TimeoutException("所选窗口未在 3 秒内返回画面，请还原窗口并确认应用允许截图。");
    }

    private static Size ReadSize(NativeRef obj, int slot)
    {
        Size size;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint,Size*,int>)obj.Slot(slot))(obj.Value,&size));
        if (size.Width is < 1 or > 16384 || size.Height is < 1 or > 16384 || (long)size.Width * size.Height > 48_000_000)
            throw new InvalidOperationException("所选窗口没有可读取的画面，或画面尺寸过大。");
        return size;
    }

    private static Bitmap ReadPixels(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D texture, Size size, CancellationToken token)
    {
        var desc = texture.Description;
        if (desc.Width != size.Width || desc.Height != size.Height || desc.Format != Format.B8G8R8A8_UNorm)
            throw new InvalidOperationException("Windows 返回的窗口图片尺寸或像素格式无效。");
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.MiscFlags = ResourceOptionFlags.None;
        using var staging = device.CreateTexture2D(desc);
        context.CopyResource(staging, texture);
        context.Flush();
        var wait = Stopwatch.StartNew();
        MappedSubresource mapped;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var result = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out mapped);
            if (result.Code != unchecked((int)0x887A000A)) // DXGI_ERROR_WAS_STILL_DRAWING
            {
                result.CheckError();
                break;
            }
            if (wait.Elapsed >= TimeSpan.FromSeconds(2)) throw new TimeoutException("显卡未及时返回所选窗口的画面，请重试。");
            token.WaitHandle.WaitOne(5);
        }
        try
        {
            var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            try
            {
                var data = bitmap.LockBits(new Rectangle(Point.Empty,size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                bool hasContent = false;
                try
                {
                    for (int y = 0; y < size.Height; y++)
                    {
                        token.ThrowIfCancellationRequested();
                        var source = (uint*)((byte*)mapped.DataPointer + y * (long)mapped.RowPitch);
                        var dest = (uint*)((byte*)data.Scan0 + y * data.Stride);
                        for (int x = 0; x < size.Width; x++)
                        {
                            uint rgb = source[x] >> 24 == 0 ? 0 : source[x] & 0xFFFFFF;
                            hasContent |= rgb != 0;
                            dest[x] = 0xFF000000 | rgb;
                        }
                    }
                }
                finally { bitmap.UnlockBits(data); }
                if (!hasContent) throw new InvalidOperationException("所选窗口返回全黑或透明画面，可能有内容保护。请检查窗口后重试。");
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }
        finally { context.Unmap(staging, 0); }
    }
}
