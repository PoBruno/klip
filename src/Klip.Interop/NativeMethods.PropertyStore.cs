using System.Runtime.InteropServices;

namespace Klip.Interop;

/// <summary>
/// P/Invoke do IPropertyStore em memoria (propsys.dll) usado pelo
/// MF_SINK_WRITER_ENCODER_CONFIG do encoder H.264 (RF-M2.06): as propriedades
/// CODECAPI_* viram pares PROPERTYKEY/PROPVARIANT aplicados em lote na criacao
/// do SinkWriter.
/// </summary>
public static partial class NativeMethods
{
    /// <summary>PROPERTYKEY do shell/propsys: fmtid + pid. Para CODECAPI_*, pid = 0 (RF-M2.06).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>
    /// PROPVARIANT minimo para os tipos usados no ENCODER_CONFIG (VT_UI4).
    /// Layout explicito de 16 bytes (header de 8 + uniao de 8) igual ao nativo.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint ulVal;
        [FieldOffset(8)] public ulong _union; // forca os 8 bytes da uniao em x86 e x64
    }

    /// <summary>VARENUM.VT_UI4 (uint de 32 bits).</summary>
    public const ushort VT_UI4 = 19;

    /// <summary>
    /// IPropertyStore COM classico (RCW via ComImport - LibraryImport nao
    /// marshalla interfaces COM, dai a excecao a convencao do projeto).
    /// </summary>
    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(in PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(in PROPERTYKEY key, in PROPVARIANT propvar);
        void Commit();
    }

    /// <summary>
    /// Cria um property store em memoria (RF-M2.06). PreserveSig false: HRESULT
    /// de falha vira excecao (tratada pelo chamador - o ENCODER_CONFIG e
    /// tolerante a falhas e nunca impede o start da gravacao).
    /// </summary>
    [DllImport("propsys.dll", ExactSpelling = true, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    public static extern IPropertyStore PSCreateMemoryPropertyStore(in Guid riid);
}
