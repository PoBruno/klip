using System.Diagnostics;
using System.Runtime.InteropServices;
using Klip.Core.Recording;
using Vortice.MediaFoundation;

namespace Klip.Interop.Recording;

/// <summary>
/// Which video codecs have a hardware encoder MFT on this machine (NVENC, AMF, QSV
/// through the driver). Asked once per process; drivers do not come and go mid session.
/// </summary>
public static class VideoEncoderCatalog
{
    private const uint MFT_ENUM_FLAG_HARDWARE = 0x00000004;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;

    private static readonly object Lock = new();
    private static IReadOnlyList<VideoCodec>? _hardware;

    /// <summary>Codecs with at least one hardware encoder, in h264, hevc, av1 order.</summary>
    public static IReadOnlyList<VideoCodec> GetHardwareCodecs()
    {
        lock (Lock)
        {
            if (_hardware is not null)
                return _hardware;

            var found = new List<VideoCodec>();
            try
            {
                MediaFoundationRuntime.Acquire();
                try
                {
                    foreach (var codec in new[] { VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1 })
                    {
                        if (HasHardwareEncoder(SubtypeOf(codec)))
                            found.Add(codec);
                    }
                }
                finally
                {
                    MediaFoundationRuntime.Release();
                }
            }
            catch (Exception ex)
            {
                // no media foundation (N editions) or enumeration failed: report nothing,
                // the recorder then goes with h264 and whatever MF picks
                Trace.WriteLine($"VideoEncoderCatalog: enumeration failed. ({ex.Message})");
            }

            _hardware = found;
            return found;
        }
    }

    /// <summary>MF video subtype: FOURCC in the first dword of the base media GUID.</summary>
    internal static Guid SubtypeOf(VideoCodec codec) => codec switch
    {
        VideoCodec.Hevc => FourCc('H', 'E', 'V', 'C'),
        VideoCodec.Av1 => FourCc('A', 'V', '0', '1'),
        _ => FourCc('H', '2', '6', '4'),
    };

    internal static Guid Nv12Subtype { get; } = FourCc('N', 'V', '1', '2');

    private static Guid FourCc(char a, char b, char c, char d)
    {
        int value = a | (b << 8) | (c << 16) | (d << 24);
        return new Guid(value, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    }

    private static bool HasHardwareEncoder(Guid subtype)
    {
        var output = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = subtype,
        };

        nint activates;
        uint count;
        try
        {
            MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
                null,
                output,
                out activates,
                out count);
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            for (int i = 0; i < count; i++)
            {
                nint activate = Marshal.ReadIntPtr(activates, i * IntPtr.Size);
                if (activate != 0)
                    Marshal.Release(activate);
            }
        }
        finally
        {
            if (activates != 0)
                Marshal.FreeCoTaskMem(activates);
        }

        return count > 0;
    }
}
