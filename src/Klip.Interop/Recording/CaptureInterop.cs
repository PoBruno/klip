using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Klip.Interop.Recording;

/// <summary>
/// COM interop para criar GraphicsCaptureItem sem picker (app Win32, spec F2).
/// Padrao canonico de robmikh/Win32CaptureSample (MIT).
/// </summary>
[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    nint CreateForWindow([In] nint window, [In] ref Guid iid);
    nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
}

/// <summary>Acesso ao recurso DXGI por tras de um IDirect3DSurface WinRT.</summary>
[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    nint GetInterface([In] ref Guid iid);
}

/// <summary>Helpers de interop WinRT &lt;-&gt; D3D11 (Vortice) do motor de captura.</summary>
internal static class CaptureInterop
{
    private static Guid _graphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static Guid _d3d11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    /// <summary>Cria o item de captura para um HMONITOR, sem picker (RF-F2.01).</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(nint hMonitor)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        nint abi = interop.CreateForMonitor(hMonitor, ref _graphicsCaptureItemIid);
        try
        {
            return GraphicsCaptureItem.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    /// <summary>Embrulha o device Vortice num IDirect3DDevice WinRT para o frame pool.</summary>
    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        int hr = NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out nint abi);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    /// <summary>
    /// Extrai a ID3D11Texture2D por tras da surface do frame WGC.
    /// O ponteiro retornado por GetInterface ja vem com AddRef; o wrapper Vortice
    /// assume a posse dessa referencia (dispose obrigatorio).
    /// </summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        nint ptr = access.GetInterface(ref _d3d11Texture2DIid);
        return new ID3D11Texture2D(ptr);
    }
}
