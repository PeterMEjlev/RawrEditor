using System.Runtime.InteropServices;

namespace Rawr.Raw;

/// <summary>
/// Decodes a RAW file into a 16-bit linear RGB <see cref="LinearRawImage"/> via
/// LibRaw. This is the exact, proven decode path from RAWR's
/// <c>LibRawExtractor.ExtractLinearRgb</c> — same LibRaw flags, the same
/// camera-WB plumbing through user_mul, the same processed-image struct offsets —
/// lifted out so the editor has no dependency on Rawr.Core.
///
/// The only addition is the <c>halfSize</c> switch:
///   • halfSize=true  → params.half_size=1: each 2×2 Bayer cell averaged to one
///     RGB sample. ~4× faster, quarter the pixels. Used for the editing preview,
///     which is box-averaged down further anyway.
///   • halfSize=false → full demosaic at sensor resolution. Used for export so
///     the saved JPEG carries every pixel the sensor recorded.
///
/// When RAWR absorbs this project, delete this file and call RAWR's
/// <c>LibRawExtractor.ExtractLinearRgb</c> instead — it produces the identical
/// <see cref="LinearRawImage"/> type the develop engine consumes.
/// </summary>
public static class RawDecoder
{
    /// <summary>True if the LibRaw native library loaded and initialised.</summary>
    public static bool IsAvailable { get; } = CheckAvailability();

    /// <summary>
    /// Decode <paramref name="filePath"/> to 16-bit linear RGB (camera WB applied,
    /// sRGB primaries, gamma=1.0; 0 = sensor black, 65535 = sensor clip). Returns
    /// null on any decode failure rather than throwing.
    /// </summary>
    public static LinearRawImage? DecodeLinearRgb(string filePath, bool halfSize)
    {
        if (!IsAvailable) return null;

        nint handle = 0;
        nint imagePtr = 0;
        try
        {
            handle = LibRawInterop.Init(0);
            if (handle == 0) return null;

            int ret = LibRawInterop.OpenFile(handle, filePath);
            if (ret != 0) return null;

            // 16-bit, linear (gamma=1.0), no auto-bright, sRGB primaries.
            LibRawInterop.SetOutputBps(handle, 16);
            LibRawInterop.SetNoAutoBright(handle, 1);
            LibRawInterop.SetGamma(handle, 0, 1.0f);
            LibRawInterop.SetGamma(handle, 1, 1.0f);
            LibRawInterop.SetOutputColor(handle, 1);
            LibRawInterop.SetDemosaic(handle, 0);
            if (halfSize)
            {
                try { LibRawInterop.SetHalfSize(handle, 1); }
                catch (EntryPointNotFoundException) { /* pre-0.18 LibRaw — full demosaic */ }
            }

            ret = LibRawInterop.Unpack(handle);
            if (ret != 0) return null;

            // Apply camera WB through user_mul (libraw_set_use_camera_wb isn't
            // exported on the 0.22.1 build we ship). cam_mul first; fall back to
            // pre_mul (populated by identify()) when Canon leaves cam_mul partial;
            // finally neutral (1,1,1,1) rather than a Bayer-zeroed render.
            float wbR  = LibRawInterop.GetCamMul(handle, 0);
            float wbG1 = LibRawInterop.GetCamMul(handle, 1);
            float wbB  = LibRawInterop.GetCamMul(handle, 2);
            float wbG2 = LibRawInterop.GetCamMul(handle, 3);
            bool camMulOk = wbR > 0 && wbG1 > 0 && wbB > 0 && wbG2 > 0;
            if (!camMulOk)
            {
                float pR  = LibRawInterop.GetPreMul(handle, 0);
                float pG1 = LibRawInterop.GetPreMul(handle, 1);
                float pB  = LibRawInterop.GetPreMul(handle, 2);
                float pG2 = LibRawInterop.GetPreMul(handle, 3);
                if (pG2 <= 0) pG2 = pG1;
                if (pR > 0 && pG1 > 0 && pB > 0)
                {
                    wbR = pR; wbG1 = pG1; wbB = pB; wbG2 = pG2;
                    camMulOk = true;
                }
            }
            LibRawInterop.SetUserMul(handle, 0, camMulOk ? wbR  : 1.0f);
            LibRawInterop.SetUserMul(handle, 1, camMulOk ? wbG1 : 1.0f);
            LibRawInterop.SetUserMul(handle, 2, camMulOk ? wbB  : 1.0f);
            LibRawInterop.SetUserMul(handle, 3, camMulOk ? wbG2 : 1.0f);

            ret = LibRawInterop.DcrawProcess(handle);
            if (ret != 0) return null;

            imagePtr = LibRawInterop.MakeMemImage(handle, out int errCode);
            if (imagePtr == 0 || errCode != 0) return null;

            // libraw_processed_image_t (MSVC/Windows layout):
            //   int type(0) ushort height(4) width(6) colors(8) bits(10)
            //   int data_size(12) byte[] data(16)
            int type = Marshal.ReadInt32(imagePtr, 0);
            ushort height = (ushort)Marshal.ReadInt16(imagePtr, 4);
            ushort width = (ushort)Marshal.ReadInt16(imagePtr, 6);
            ushort colors = (ushort)Marshal.ReadInt16(imagePtr, 8);
            ushort bits = (ushort)Marshal.ReadInt16(imagePtr, 10);
            int dataSize = Marshal.ReadInt32(imagePtr, 12);
            if (type != 2) return null;
            if (colors != 3 || bits != 16 || dataSize <= 0) return null;

            int pixelCount = width * height * 3;
            if (dataSize != pixelCount * 2) return null;

            var pixels = new ushort[pixelCount];
            unsafe
            {
                fixed (ushort* dst = pixels)
                {
                    Buffer.MemoryCopy((void*)(imagePtr + 16), dst, dataSize, dataSize);
                }
            }

            return new LinearRawImage(width, height, pixels);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (imagePtr != 0) LibRawInterop.ClearMem(imagePtr);
            if (handle != 0)
            {
                LibRawInterop.Recycle(handle);
                LibRawInterop.Close(handle);
            }
        }
    }

    private static bool CheckAvailability()
    {
        try
        {
            var handle = LibRawInterop.Init(0);
            if (handle != 0)
            {
                LibRawInterop.Close(handle);
                return true;
            }
            return false;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}
