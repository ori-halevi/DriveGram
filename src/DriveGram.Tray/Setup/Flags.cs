namespace DriveGram.Tray.Setup;

/// <summary>
/// The little country flags of the phone field, cut out of one picture (assets/flags.png) that holds all
/// of them in a grid, in the order below. Windows does not draw flag emoji at all, so a picture is the
/// only way to show a real flag.
///
/// The pictures are FlagKit (https://github.com/madebybowtie/FlagKit), MIT licence, © 2016 Bowtie AB.
/// </summary>
static class Flags
{
    /// <summary>One flag in the sheet (the sheet holds them at twice this, for a sharp smaller copy).</summary>
    const int W = 42, H = 30, Columns = 16;

    /// <summary>Every flag in the sheet, in the order it sits there (two letters each).</summary>
    const string Order =
        "ADAEAFAGAIALAMAOARASATAUAWAXAZBABBBDBEBFBGBHBIBJBLBMBNBOBRBSBTBVBWBYBZ" +
        "CACCCDCFCGCHCICKCLCMCNCOCRCUCVCWCXCYCZDEDJDKDMDODZECEEEGERESETEU" +
        "FIFJFKFMFOFRGAGBGDGEGFGGGHGIGLGMGNGPGQGRGSGTGUGWGYHKHMHNHRHTHU" +
        "IDIEILIMINIOIQIRISITJEJMJOJPKEKGKHKIKMKNKPKRKWKYKZ" +
        "LALBLCLILKLRLSLTLULVLYMAMCMDMEMFMGMHMKMLMMMNMOMPMQMRMSMTMUMVMWMXMYMZ" +
        "NANCNENFNGNINLNONPNRNUNZOMPAPEPFPGPHPKPLPMPNPRPSPTPWPYQARERORSRURW" +
        "SASBSCSDSESGSHSISJSKSLSMSNSOSRSSSTSVSXSYSZTCTDTFTGTHTJTKTLTMTNTOTRTTTVTWTZ" +
        "UAUGUMUSUYUZVAVCVEVGVIVNVUWFWSXKYEYTZAZMZW";

    static Bitmap? _sheet;
    static bool _tried;
    static readonly Dictionary<(string Iso, int Height), Image?> Cut = [];
    static readonly object Gate = new();

    static Bitmap? Sheet()
    {
        if (_tried) return _sheet;
        _tried = true;
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++) // next to the program; while developing, the repo's assets
        {
            foreach (var p in new[] { Path.Combine(dir, "flags.png"), Path.Combine(dir, "assets", "flags.png") })
                if (File.Exists(p))
                {
                    try { return _sheet = new Bitmap(p); }
                    catch (Exception ex) { Log.Warn($"flags: {ex.Message}"); return null; }
                }
            dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
        }
        Log.Debug("flags: flags.png was not found; the phone field shows country codes without flags");
        return null;
    }

    /// <summary>Is there a flag for this country at all?</summary>
    public static bool Has(string iso) => IndexOf(iso) >= 0;

    static int IndexOf(string iso)
    {
        if (iso.Length != 2) return -1;
        int at = Order.IndexOf(iso.ToUpperInvariant(), StringComparison.Ordinal);
        return at >= 0 && at % 2 == 0 ? at / 2 : -1;
    }

    /// <summary>The country's flag, `height` pixels tall (null when there is none).</summary>
    public static Image? Of(string iso, int height)
    {
        lock (Gate)
        {
            if (Cut.TryGetValue((iso, height), out var ready)) return ready;
            Image? flag = null;
            if (Sheet() is { } sheet && IndexOf(iso) is var i and >= 0)
            {
                int width = (int)Math.Round(height * (double)W / H);
                var bmp = new Bitmap(width, height);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(sheet, new Rectangle(0, 0, width, height),
                        new Rectangle(i % Columns * W, i / Columns * H, W, H), GraphicsUnit.Pixel);
                }
                flag = bmp;
            }
            if (Cut.Count < 600) Cut[(iso, height)] = flag;
            return flag;
        }
    }
}
