using System.Text;

namespace Rawr.Develop;

/// <summary>
/// Builds a minimal, standards-conformant ICC v2 display profile for
/// Adobe RGB (1998) so exported files can be tagged with the colour space the
/// pixels were actually encoded in.
///
/// It is a classic three-component matrix/TRC profile: nine tags
/// (desc, cprt, wtpt, r/g/bXYZ, r/g/bTRC). The primaries are the standard
/// D50-Bradford-adapted Adobe RGB values (identical to Adobe's own
/// AdobeRGB1998.icc), the TRC a single 2.19921875 gamma. We synthesise it in
/// code rather than ship a binary blob so the develop engine stays a pure
/// source drop-in with no content files to carry across the RAWR merge.
/// </summary>
public static class IccProfiles
{
    // D50-adapted primaries (column XYZ for R, G, B) — the canonical Adobe RGB
    // (1998) profile values. White point is the ICC PCS illuminant, D50.
    private static readonly (double X, double Y, double Z) WhiteD50 = (0.9642, 1.0, 0.8249);
    private static readonly (double X, double Y, double Z) RedXYZ   = (0.60974, 0.31111, 0.01947);
    private static readonly (double X, double Y, double Z) GreenXYZ = (0.20528, 0.62567, 0.06087);
    private static readonly (double X, double Y, double Z) BlueXYZ  = (0.14919, 0.06322, 0.74457);
    private const double Gamma = 2.19921875; // 563/256, exact in u8Fixed8

    private static byte[]? _adobeRgb;

    /// <summary>The Adobe RGB (1998) profile bytes (built once, then cached).</summary>
    public static byte[] AdobeRgb1998 => _adobeRgb ??= Build();

    private static byte[] Build()
    {
        // Tag data elements, in the order they are laid down after the table.
        var tags = new List<(string sig, byte[] data)>
        {
            ("desc", TextDescription("Adobe RGB (1998) compatible")),
            ("cprt", Text("No copyright, use freely.")),
            ("wtpt", Xyz(WhiteD50)),
            ("rXYZ", Xyz(RedXYZ)),
            ("gXYZ", Xyz(GreenXYZ)),
            ("bXYZ", Xyz(BlueXYZ)),
            ("rTRC", Curve(Gamma)),
            ("gTRC", Curve(Gamma)),
            ("bTRC", Curve(Gamma)),
        };

        const int headerSize = 128;
        int tableSize = 4 + tags.Count * 12;
        int dataStart = Align4(headerSize + tableSize);

        // Place each tag, 4-byte aligned; remember (offset, unpadded size).
        var placed = new (string sig, int offset, byte[] data)[tags.Count];
        int cursor = dataStart;
        for (int i = 0; i < tags.Count; i++)
        {
            placed[i] = (tags[i].sig, cursor, tags[i].data);
            cursor = Align4(cursor + tags[i].data.Length);
        }
        int total = cursor;
        var buf = new byte[total];

        // ── Header ──
        WriteU32(buf, 0, (uint)total);                 // profile size
        WriteTag(buf, 4, "    ");                       // preferred CMM (none)
        WriteU32(buf, 8, 0x02100000);                   // version 2.1.0
        WriteTag(buf, 12, "mntr");                       // device class: display
        WriteTag(buf, 16, "RGB ");                       // data colour space
        WriteTag(buf, 20, "XYZ ");                       // PCS
        // 24: date/time (12 bytes) — left zero
        WriteTag(buf, 36, "acsp");                       // profile file signature
        // 40 platform, 44 flags, 48 manufacturer, 52 model, 56 attributes — zero
        WriteU32(buf, 64, 0);                            // rendering intent: perceptual
        WriteS15F16(buf, 68, WhiteD50.X);                // PCS illuminant (D50)
        WriteS15F16(buf, 72, WhiteD50.Y);
        WriteS15F16(buf, 76, WhiteD50.Z);
        // 80 creator, 84..127 reserved — zero

        // ── Tag table ──
        WriteU32(buf, headerSize, (uint)tags.Count);
        for (int i = 0; i < placed.Length; i++)
        {
            int e = headerSize + 4 + i * 12;
            WriteTag(buf, e, placed[i].sig);
            WriteU32(buf, e + 4, (uint)placed[i].offset);
            WriteU32(buf, e + 8, (uint)placed[i].data.Length);
        }

        // ── Tag data ──
        foreach (var (_, offset, data) in placed)
            Array.Copy(data, 0, buf, offset, data.Length);

        return buf;
    }

    // ── Tag element builders ──

    private static byte[] Xyz((double X, double Y, double Z) v)
    {
        var b = new byte[20];
        WriteTag(b, 0, "XYZ ");
        WriteS15F16(b, 8, v.X);
        WriteS15F16(b, 12, v.Y);
        WriteS15F16(b, 16, v.Z);
        return b;
    }

    private static byte[] Curve(double gamma)
    {
        // curveType with a single entry ⇒ the value is a u8Fixed8 gamma.
        var b = new byte[14];
        WriteTag(b, 0, "curv");
        WriteU32(b, 8, 1);                                   // entry count
        ushort g = (ushort)Math.Round(gamma * 256.0);
        b[12] = (byte)(g >> 8);
        b[13] = (byte)(g & 0xFF);
        return b;
    }

    private static byte[] Text(string ascii)
    {
        // textType: 'text' + reserved + null-terminated ASCII.
        byte[] s = Encoding.ASCII.GetBytes(ascii);
        var b = new byte[8 + s.Length + 1];
        WriteTag(b, 0, "text");
        Array.Copy(s, 0, b, 8, s.Length);
        return b;
    }

    private static byte[] TextDescription(string ascii)
    {
        // textDescriptionType (ICC v2): ASCII block + empty Unicode + empty
        // ScriptCode (a fixed 67-byte trailer that must be present).
        byte[] s = Encoding.ASCII.GetBytes(ascii);
        int asciiCount = s.Length + 1; // includes the null terminator
        int size = 8 + 4 + asciiCount + 4 + 4 + 2 + 1 + 67;
        var b = new byte[size];
        WriteTag(b, 0, "desc");
        WriteU32(b, 8, (uint)asciiCount);
        Array.Copy(s, 0, b, 12, s.Length);
        // remaining bytes (Unicode lang/count, ScriptCode) stay zero
        return b;
    }

    // ── Primitive writers (ICC is big-endian) ──

    private static int Align4(int n) => (n + 3) & ~3;

    private static void WriteU32(byte[] b, int o, uint v)
    {
        b[o]     = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }

    private static void WriteS15F16(byte[] b, int o, double v)
    {
        int fixed16 = (int)Math.Round(v * 65536.0);
        WriteU32(b, o, unchecked((uint)fixed16));
    }

    private static void WriteTag(byte[] b, int o, string sig)
    {
        for (int i = 0; i < 4; i++)
            b[o + i] = (byte)(i < sig.Length ? sig[i] : ' ');
    }
}
