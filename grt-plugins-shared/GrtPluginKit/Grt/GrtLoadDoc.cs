using System.Globalization;
using System.Text;
using System.Xml;

namespace GrtPluginKit.Grt;

/// <summary>A shot inside an imported/analysed Measurement charge.</summary>
public sealed record GrtShot(double VelocityMps, string? Note);

/// <summary>One &lt;charge&gt; of an appendix &lt;Measurement&gt;.</summary>
public sealed class GrtCharge
{
    public string Name { get; init; } = "";
    /// <summary>The charge's <c>value</c> attribute (kg of propellant in practice, 0 if unset).</summary>
    public double ValueKg { get; init; }
    public string Note { get; init; } = "";
    public List<GrtShot> Shots { get; } = new();

    /// <summary>Charge weight in grains parsed from Name ("39.2 gr @ …") or ValueKg.</summary>
    public double? ChargeGrains
    {
        get
        {
            var m = System.Text.RegularExpressions.Regex.Match(Name, @"(-?\d+(?:[.,]\d+)?)\s*gr");
            if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double g))
                return g;
            return ValueKg > 0 ? ValueKg / 0.00006479891 : null;
        }
    }
}

public sealed class GrtMeasurement
{
    public string Title { get; init; } = "";
    public List<GrtCharge> Charges { get; } = new();
}

/// <summary>One hit in a GRT shot-group, as a fraction (0..1) of the target image.</summary>
public sealed record GrtShotPoint(double X, double Y, bool Flyer, bool PointOfAim);

/// <summary>One &lt;group&gt; inside a &lt;ShotGroup&gt; tab.</summary>
public sealed class GrtShotGroupSet
{
    public string Name { get; init; } = "";
    public List<GrtShotPoint> Points { get; } = new();
}

/// <summary>
/// A GRT "Shot group analysis" tab stored in the load. Points are image fractions;
/// two reference points a known <see cref="RefDistance"/> apart calibrate the scale,
/// and <see cref="ImageWidth"/>/<see cref="ImageHeight"/> give the pixel aspect ratio.
/// </summary>
public sealed class GrtShotGroup
{
    public string Title { get; init; } = "";
    public double RefP1X { get; init; }
    public double RefP1Y { get; init; }
    public double RefP2X { get; init; }
    public double RefP2Y { get; init; }
    /// <summary>Real-world distance the two reference points span, in GRT's configured unit (usually mm).</summary>
    public double RefDistance { get; init; }
    /// <summary>Shooting distance in GRT's configured unit (usually m).</summary>
    public double ShootDistance { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public List<GrtShotGroupSet> Groups { get; } = new();
}

/// <summary>
/// The calibration GRT needs to recover real-world scale from a shot-group picture:
/// two reference points as image fractions, the real distance between them, and the
/// shooting distance. Units are as GRT stores them — see the writer's remarks.
/// </summary>
public sealed record ShotGroupGeometry(
    double RefP1X, double RefP1Y, double RefP2X, double RefP2Y,
    double RefDistance, double ShootDistance);

/// <summary>
/// Read + edit a GRT <c>.grtload</c> file. Editing preserves the rest of the file
/// verbatim (only whitespace/declaration cosmetics may change) and writes to a
/// sibling <c>&lt;stem&gt;_&lt;suffix&gt;_&lt;stamp&gt;.grtload</c>.
/// </summary>
public sealed class GrtLoadDoc
{
    private readonly XmlDocument _doc = new() { PreserveWhitespace = true };
    private readonly XmlElement _inner;
    private readonly XmlElement _appendix;

    public string SourcePath { get; }

    private GrtLoadDoc(string path, XmlDocument doc, XmlElement inner, XmlElement appendix)
    {
        SourcePath = path;
        _doc = doc;
        _inner = inner;
        _appendix = appendix;
    }

    public static GrtLoadDoc Load(string path)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(path);
        XmlElement? inner = doc.DocumentElement?["InnerBallistikInput"];
        if (doc.DocumentElement?.Name != "GordonsReloadingTool" || inner == null)
            throw new InvalidDataException("Not a recognisable .grtload (no <InnerBallistikInput>).");

        XmlElement appendix = inner["appendix"] ?? CreateAppendix(doc, inner);
        return new GrtLoadDoc(path, doc, inner, appendix);
    }

    /// <summary>A stand-alone .grtload with a Generic caliber/gun/projectile/propellant and an empty appendix.</summary>
    public static GrtLoadDoc CreateMinimal(string title, string sourcePathForNaming)
    {
        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>\n" +
            "<GordonsReloadingTool version=\"2021.2030-NIGHTLY\">\n  <InnerBallistikInput>\n" +
            $"    <title>{Uri.EscapeDataString(title)}</title>\n" +
            "    <caliber><input name=\"CaliberName\" value=\"Generic\" /></caliber>\n" +
            "    <gun><input name=\"GunName\" value=\"Generic\" /></gun>\n" +
            "    <projectile><input name=\"ProjectileName\" value=\"Generic\" /></projectile>\n" +
            "    <propellant><input name=\"pname\" value=\"Generic\" /></propellant>\n" +
            "    <appendix>\n    </appendix>\n  </InnerBallistikInput>\n</GordonsReloadingTool>");
        var inner = (XmlElement)doc.SelectSingleNode("//InnerBallistikInput")!;
        var appendix = (XmlElement)doc.SelectSingleNode("//appendix")!;
        return new GrtLoadDoc(sourcePathForNaming, doc, inner, appendix);
    }

    private static XmlElement CreateAppendix(XmlDocument doc, XmlElement inner)
    {
        var a = doc.CreateElement("appendix");
        inner.AppendChild(doc.CreateWhitespace("\n    "));
        inner.AppendChild(a);
        inner.AppendChild(doc.CreateWhitespace("\n  "));
        return a;
    }

    // ---- read ---------------------------------------------------------------

    public string CaliberName => InputValue("caliber", "CaliberName");
    public string GunName => InputValue("gun", "GunName");
    public string ProjectileName => InputValue("projectile", "ProjectileName");
    public string PropellantName => InputValue("propellant", "pname");
    public double? PropellantChargeGr => ParseInputG("propellant", "mc");
    /// <summary>Projectile weight in grains (from projectile <c>mp</c>, stored in g).</summary>
    public double? BulletMassGr => ParseInputG("projectile", "mp");
    /// <summary>Cartridge overall length L6/OAL in mm.</summary>
    public double? CoalMm => ParseInput("caliber", "oal");
    /// <summary>Case length L3/CL in mm.</summary>
    public double? CaseLenMm => ParseInput("caliber", "caselen");
    /// <summary>Bullet seating depth in mm.</summary>
    public double? SeatingDepthMm => ParseInput("projectile", "gdepth");
    /// <summary>Projectile overall length in mm.</summary>
    public double? BulletLengthMm => ParseInput("projectile", "glen");

    /// <summary>
    /// COAL implied by the current case length, bullet length and seating depth, or null if any of
    /// the three is missing. GRT documents oal as "calculated based on actual case length, the
    /// length of the projectile minus the seating depth", and the identity holds exactly in the
    /// loads GRT itself writes - so a tool that moves <c>gdepth</c> in a file must move <c>oal</c>
    /// with it. GRT recomputes oal when the user edits seating depth in its own UI, but a load
    /// opened from disk keeps whatever oal the file carries.
    /// </summary>
    public static double? CoalFrom(double? caseLenMm, double? bulletLenMm, double? seatingDepthMm) =>
        caseLenMm is { } cl && bulletLenMm is { } gl && seatingDepthMm is { } gd ? cl + gl - gd : null;
    public double? LadderStepGr => ParseInputG("propellant", "laddermc");
    public int? LadderCount => (int?)ParseInput("propellant", "laddercnt");
    public double? PropellantBa => ParseInput("propellant", "Ba");
    public double? PropellantSebert => ParseInput("caliber", "sebert");

    /// <summary>Sets an existing <c>&lt;input name="..."&gt;</c> value (and optionally its unit) in a section. Returns false if not found.</summary>
    public bool SetInput(string section, string name, string value, string? unit = null)
    {
        foreach (XmlElement e in _inner.SelectNodes($"{section}/input")!.OfType<XmlElement>())
            if (e.GetAttribute("name") == name)
            {
                e.SetAttribute("value", value);
                if (unit != null) e.SetAttribute("unit", unit);
                return true;
            }
        return false;
    }

    public IEnumerable<GrtMeasurement> Measurements()
    {
        foreach (XmlElement me in _appendix.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "Measurement"))
        {
            var m = new GrtMeasurement { Title = Uri.UnescapeDataString(me.GetAttribute("title")) };
            foreach (XmlElement ce in me.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "charge"))
            {
                var c = new GrtCharge
                {
                    Name = Uri.UnescapeDataString(ce.GetAttribute("name")),
                    Note = Uri.UnescapeDataString(ce.GetAttribute("note")),
                    ValueKg = double.TryParse(ce.GetAttribute("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0,
                };
                foreach (XmlElement se in ce.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "shot"))
                {
                    double vel = double.TryParse(se.GetAttribute("velocity"), NumberStyles.Float, CultureInfo.InvariantCulture, out double vv) ? vv : 0;
                    string? note = se.HasAttribute("note") ? Uri.UnescapeDataString(se.GetAttribute("note")) : null;
                    c.Shots.Add(new GrtShot(vel, note));
                }
                m.Charges.Add(c);
            }
            yield return m;
        }
    }

    /// <summary>The GRT "Shot group analysis" tabs stored in this load (image + reference points + hits).</summary>
    public IEnumerable<GrtShotGroup> ShotGroups()
    {
        double A(XmlElement e, string n) => double.TryParse(e.GetAttribute(n), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

        foreach (XmlElement sg in _appendix.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "ShotGroup"))
        {
            var pic = sg.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.Name == "picture");
            var g = new GrtShotGroup
            {
                Title = Uri.UnescapeDataString(sg.GetAttribute("title")),
                RefP1X = A(sg, "refPoint1X"), RefP1Y = A(sg, "refPoint1Y"),
                RefP2X = A(sg, "refPoint2X"), RefP2Y = A(sg, "refPoint2Y"),
                RefDistance = A(sg, "refDistance"),
                ShootDistance = A(sg, "shootDistance"),
                ImageWidth = pic != null ? (int)A(pic, "width") : 0,
                ImageHeight = pic != null ? (int)A(pic, "height") : 0,
            };
            foreach (XmlElement ge in sg.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "group"))
            {
                var set = new GrtShotGroupSet { Name = Uri.UnescapeDataString(ge.GetAttribute("name")) };
                foreach (XmlElement pe in ge.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "point"))
                    set.Points.Add(new GrtShotPoint(
                        A(pe, "x"), A(pe, "y"),
                        pe.GetAttribute("flyer") == "true",
                        pe.GetAttribute("PointOfAim") == "true"));
                g.Groups.Add(set);
            }
            yield return g;
        }
    }

    // ---- edit -------------------------------------------------------------

    /// <summary>Removes existing appendix children whose (decoded) title starts with <paramref name="titlePrefix"/>.</summary>
    public int RemoveByTitlePrefix(string titlePrefix)
    {
        string enc = Enc(titlePrefix);
        int n = 0;
        foreach (XmlElement e in _appendix.ChildNodes.OfType<XmlElement>()
                     .Where(e => e.GetAttribute("title").StartsWith(enc, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            while (e.PreviousSibling is XmlWhitespace ws) ws.ParentNode!.RemoveChild(ws);
            _appendix.RemoveChild(e);
            n++;
        }
        return n;
    }

    public void AddNote(string title, string text, bool showInReport = true)
    {
        int idx = NextIndex();
        var note = _doc.CreateElement("note");
        note.SetAttribute("index", idx.ToString(CultureInfo.InvariantCulture));
        note.SetAttribute("hasfocus", "false");
        note.SetAttribute("showinreport", showInReport ? "true" : "false");
        note.SetAttribute("reporttemplate", "false");
        note.SetAttribute("title", UniqueTitle(Enc(title)));
        note.SetAttribute("text", Enc(text));
        note.SetAttribute("selstart", "0");
        note.SetAttribute("sellength", "0");
        note.SetAttribute("scrollposition", "0");
        note.SetAttribute("config", "%7Bedit%2C17%2C6%2C-1%2C-1%2C-1%2C-1%2C0%2C0%7D");
        _appendix.AppendChild(_doc.CreateWhitespace("  "));
        _appendix.AppendChild(note);
        _appendix.AppendChild(_doc.CreateWhitespace("\n    "));
    }

    /// <summary>
    /// Adds a one-picture gallery to the appendix. GRT names gallery pictures with a file
    /// extension ("chart.png"); a report embeds it with <c>~~result.picture.&lt;name&gt;.png~~</c>.
    /// Pass <paramref name="pictureName"/> without the extension — ".png" is appended.
    /// </summary>
    /// <summary>Adds a gallery picture, reading its pixel size straight from the PNG header
    /// so the <c>&lt;picture&gt;</c> width/height always match the image.</summary>
    public void AddGalleryPicture(string galleryTitle, string pictureName, byte[] png, bool showInReport = true)
    {
        var (w, h) = PngSize(png);
        AddGalleryPicture(galleryTitle, pictureName, png, w, h, showInReport);
    }

    /// <summary>(width, height) from a PNG's IHDR chunk; (0, 0) if it doesn't look like a PNG.</summary>
    private static (int w, int h) PngSize(byte[] png)
    {
        if (png.Length < 24 || png[0] != 0x89 || png[1] != 0x50) return (0, 0);
        int R(int o) => (png[o] << 24) | (png[o + 1] << 16) | (png[o + 2] << 8) | png[o + 3];
        return (R(16), R(20));
    }

    public void AddGalleryPicture(string galleryTitle, string pictureName, byte[] png, int width, int height, bool showInReport = true)
    {
        if (!pictureName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) pictureName += ".png";

        int idx = NextIndex();
        var gal = _doc.CreateElement("gallery");
        gal.SetAttribute("index", idx.ToString(CultureInfo.InvariantCulture));
        gal.SetAttribute("hasfocus", "false");
        gal.SetAttribute("showinreport", showInReport ? "true" : "false");
        gal.SetAttribute("title", UniqueTitle(Enc(galleryTitle)));

        var pic = _doc.CreateElement("picture");
        pic.SetAttribute("name", Enc(pictureName));
        pic.SetAttribute("type", "png");
        pic.SetAttribute("width", width.ToString(CultureInfo.InvariantCulture));
        pic.SetAttribute("height", height.ToString(CultureInfo.InvariantCulture));
        pic.SetAttribute("data", Convert.ToBase64String(png));
        gal.AppendChild(_doc.CreateWhitespace("\n        "));
        gal.AppendChild(pic);
        gal.AppendChild(_doc.CreateWhitespace("\n      "));

        _appendix.AppendChild(_doc.CreateWhitespace("  "));
        _appendix.AppendChild(gal);
        _appendix.AppendChild(_doc.CreateWhitespace("\n    "));
    }

    /// <summary>
    /// Adds a GRT "Shot group analysis" tab: the picture, the two reference points that
    /// give it scale, and one &lt;group&gt; per set of hits. Point coordinates are image
    /// fractions 0..1 with y down, exactly as <see cref="ShotGroups"/> reads them back.
    /// </summary>
    /// <param name="pointSize">Hit marker diameter as a fraction of the image width.
    /// GRT's own default for a 1600 px picture is about 0.032.</param>
    public void AddShotGroup(string title, byte[] png, ShotGroupGeometry geom,
                             IEnumerable<GrtShotGroupSet> groups, double pointSize = 0.03)
    {
        var (w, h) = PngSize(png);
        string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        int idx = NextIndex();
        var sg = _doc.CreateElement("ShotGroup");
        sg.SetAttribute("index", idx.ToString(CultureInfo.InvariantCulture));
        sg.SetAttribute("hasfocus", "false");
        sg.SetAttribute("title", UniqueTitle(Enc(title)));
        sg.SetAttribute("zoom", "0.25");
        sg.SetAttribute("scrollPositionX", "0");
        sg.SetAttribute("scrollPositionY", "0");
        sg.SetAttribute("refPoint1X", F(geom.RefP1X));
        sg.SetAttribute("refPoint1Y", F(geom.RefP1Y));
        sg.SetAttribute("refPoint2X", F(geom.RefP2X));
        sg.SetAttribute("refPoint2Y", F(geom.RefP2Y));
        sg.SetAttribute("refDistance", F(geom.RefDistance));
        sg.SetAttribute("shootDistance", F(geom.ShootDistance));
        sg.SetAttribute("pointSize", F(pointSize));
        sg.SetAttribute("statisticSize", "1");
        sg.SetAttribute("darkenImageAlpha", "0.9");
        sg.SetAttribute("showQuickHelp", "false");

        var pic = _doc.CreateElement("picture");
        pic.SetAttribute("name", Enc(SafeName(title) + ".png"));
        pic.SetAttribute("type", "png");
        pic.SetAttribute("width", w.ToString(CultureInfo.InvariantCulture));
        pic.SetAttribute("height", h.ToString(CultureInfo.InvariantCulture));
        pic.SetAttribute("data", Convert.ToBase64String(png));
        sg.AppendChild(_doc.CreateWhitespace("\n        "));
        sg.AppendChild(pic);

        foreach (GrtShotGroupSet set in groups)
        {
            var ge = _doc.CreateElement("group");
            ge.SetAttribute("name", Enc(set.Name));
            foreach (GrtShotPoint p in set.Points)
            {
                var pe = _doc.CreateElement("point");
                pe.SetAttribute("x", F(p.X));
                pe.SetAttribute("y", F(p.Y));
                if (p.Flyer) pe.SetAttribute("flyer", "true");
                if (p.PointOfAim) pe.SetAttribute("PointOfAim", "true");
                ge.AppendChild(_doc.CreateWhitespace("\n            "));
                ge.AppendChild(pe);
            }
            ge.AppendChild(_doc.CreateWhitespace("\n        "));
            sg.AppendChild(_doc.CreateWhitespace("\n        "));
            sg.AppendChild(ge);
        }

        sg.AppendChild(_doc.CreateWhitespace("\n      "));
        _appendix.AppendChild(_doc.CreateWhitespace("  "));
        _appendix.AppendChild(sg);
        _appendix.AppendChild(_doc.CreateWhitespace("\n    "));
    }

    private static string SafeName(string title) =>
        string.Concat(title.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    public void AddMeasurement(string title, IEnumerable<GrtCharge> charges)
    {
        int idx = NextIndex();
        var me = _doc.CreateElement("Measurement");
        me.SetAttribute("index", idx.ToString(CultureInfo.InvariantCulture));
        me.SetAttribute("showinreport", "true");
        me.SetAttribute("title", UniqueTitle(Enc(title)));
        const string ind = "        ";
        foreach (GrtCharge c in charges)
        {
            var ce = _doc.CreateElement("charge");
            ce.SetAttribute("name", Enc(c.Name));
            ce.SetAttribute("showinreport", "true");
            ce.SetAttribute("expanded", "true");
            ce.SetAttribute("expandedstats", "true");
            ce.SetAttribute("note", Enc(c.Note));
            ce.SetAttribute("value", c.ValueKg.ToString("0.000000000000", CultureInfo.InvariantCulture));
            ce.SetAttribute("menu", "");
            ce.SetAttribute("menuvalue", "");
            ce.SetAttribute("menuunit", "m");
            ce.SetAttribute("source", Enc("Plugin Import"));
            ce.SetAttribute("sourceoptions", "%7B%7D");
            foreach (GrtShot s in c.Shots)
            {
                ce.AppendChild(_doc.CreateWhitespace("\n" + ind + "  "));
                var se = _doc.CreateElement("shot");
                se.SetAttribute("name", "");
                se.SetAttribute("velocity", s.VelocityMps.ToString("0.0", CultureInfo.InvariantCulture));
                se.SetAttribute("pressure", "0");
                if (!string.IsNullOrEmpty(s.Note)) se.SetAttribute("note", Enc(s.Note));
                ce.AppendChild(se);
            }
            ce.AppendChild(_doc.CreateWhitespace("\n" + ind));
            me.AppendChild(_doc.CreateWhitespace("\n" + ind));
            me.AppendChild(ce);
        }
        me.AppendChild(_doc.CreateWhitespace("\n      "));
        _appendix.AppendChild(_doc.CreateWhitespace("  "));
        _appendix.AppendChild(me);
        _appendix.AppendChild(_doc.CreateWhitespace("\n    "));
    }

    // The one toolkit "family" per load: <stem>_toolkit_<yyyyMMdd_HHmm>.grtload. Minute-stamped so
    // rapid re-runs reuse the file, but each write gets a fresh name — GRT's Load_File only reloads
    // a path it doesn't already have open, so a stable name would show stale content on the 2nd write.
    private const string ToolkitTag = "_toolkit_";
    // The second alternative's time group must allow 4-6 digits, same as the first: SaveSibling
    // always writes HHmmss (6 digits), so a 4-digit-only pattern never strips a non-"toolkit"
    // family's stem (e.g. "shotmarker"). Left at \d{4} that stem never strips, so saving a
    // sibling of a sibling compounds the name instead of reusing it, and the prune glob (which
    // matches on the stripped stem) stops matching the earlier files - the family grows unbounded.
    private static readonly System.Text.RegularExpressions.Regex StemRx = new(
        @"_toolkit(_\d{8}_\d{4,6})?$|_[a-z]+_\d{8}_\d{4,6}$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string StripToolkitStem(string path) =>
        StemRx.Replace(Path.GetFileNameWithoutExtension(path), "");

    /// <summary>The most recent <c>&lt;stem&gt;_toolkit_*.grtload</c> next to the load, or null.</summary>
    public static string? NewestToolkitSibling(string sourcePath)
    {
        string dir = Path.GetDirectoryName(sourcePath) ?? Path.GetTempPath();
        string stem = StripToolkitStem(sourcePath);
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, $"{stem}{ToolkitTag}*.grtload")
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    /// <summary>The user's original load (all toolkit / legacy suffixes stripped).</summary>
    public static string PristineBasePath(string path)
        => Path.Combine(Path.GetDirectoryName(path) ?? "", $"{StripToolkitStem(path)}.grtload");

    /// <summary>
    /// Saves a generated sibling and returns its path, keeping the 3 newest of its family.
    /// <paramref name="family"/> names the family: each plugin keeps its own, because pruning
    /// is per-family and a shared one would let one plugin delete another's output.
    /// <paramref name="suffix"/> only documents which tool wrote it.
    /// </summary>
    public string SaveSibling(string suffix, string family = "toolkit")
    {
        _ = suffix;
        string dir = Path.GetDirectoryName(SourcePath) ?? Path.GetTempPath();
        string stem = StripToolkitStem(SourcePath);
        string tag = $"_{family}_";
        string outPath = Path.Combine(dir, $"{stem}{tag}{DateTime.Now:yyyyMMdd_HHmmss}.grtload");
        Save(outPath);

        try
        {
            foreach (var old in Directory.EnumerateFiles(dir, $"{stem}{tag}*.grtload")
                         .Where(f => !string.Equals(f, outPath, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(File.GetLastWriteTimeUtc).Skip(2))
                File.Delete(old);
        }
        catch { /* a snapshot GRT still has open — leave it */ }

        return outPath;
    }

    public void Save(string path)
    {
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        using var w = XmlWriter.Create(path, settings);
        _doc.Save(w);
    }

    public static bool LooksLikeGeneratedSibling(string? path)
        => !string.IsNullOrEmpty(path) &&
           System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path!),
               @"_(?!toolkit_)[a-z]+_\d{8}_\d{4,6}\.grtload$|^Athlon_Import_",
               System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The file to READ toolkit data from: the newest toolkit-family sibling, else the active tab.</summary>
    public static string EffectiveReadPath(string activeTabFile)
        => NewestToolkitSibling(activeTabFile) ?? activeTabFile;

    /// <summary>
    /// Loads the document a toolkit tool should add to: the newest toolkit-family sibling if one
    /// exists (so earlier notes/edits are kept), otherwise <paramref name="activeTabFile"/> itself.
    /// </summary>
    public static GrtLoadDoc OpenForToolkitEdit(string activeTabFile)
        => Load(NewestToolkitSibling(activeTabFile) ?? activeTabFile);

    // ---- internals ------------------------------------------------------

    private int NextIndex()
    {
        int max = 0;
        foreach (XmlElement e in _appendix.ChildNodes.OfType<XmlElement>())
            if (int.TryParse(e.GetAttribute("index"), out int i) && i > max) max = i;
        return max + 1;
    }

    private string UniqueTitle(string enc)
    {
        bool Exists(string t) => _appendix.ChildNodes.OfType<XmlElement>().Any(e => e.GetAttribute("title") == t);
        if (!Exists(enc)) return enc;
        for (int i = 1; i < 100; i++) if (!Exists($"{enc}-{i}")) return $"{enc}-{i}";
        return $"{enc}-{DateTime.Now:HHmmss}";
    }

    private string InputValue(string section, string name)
    {
        foreach (XmlElement e in _inner.SelectNodes($"{section}/input")!.OfType<XmlElement>())
            if (e.GetAttribute("name") == name) return Uri.UnescapeDataString(e.GetAttribute("value"));
        return "";
    }

    /// <summary>The <c>unit</c> attribute of an input (e.g. "cm3", "gr", "mm"), or "" if the input isn't there.</summary>
    public string InputUnit(string section, string name)
    {
        foreach (XmlElement e in _inner.SelectNodes($"{section}/input")!.OfType<XmlElement>())
            if (e.GetAttribute("name") == name) return e.GetAttribute("unit");
        return "";
    }

    /// <summary>Raw numeric value of an input, in whatever unit the file stores it (see <see cref="InputUnit"/>).</summary>
    public double? InputNumber(string section, string name) => ParseInput(section, name);

    private double? ParseInput(string section, string name)
        => double.TryParse(InputValue(section, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    /// <summary>Reads a mass input in grams, converting kg→g when the unit attribute says so.</summary>
    private double? ParseInputG(string section, string name)
    {
        foreach (XmlElement e in _inner.SelectNodes($"{section}/input")!.OfType<XmlElement>())
            if (e.GetAttribute("name") == name)
            {
                if (!double.TryParse(e.GetAttribute("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
                double g = e.GetAttribute("unit").ToLowerInvariant() == "kg" ? v * 1000 : v;
                return g / 0.06479891; // g -> grains
            }
        return null;
    }

    private static string Enc(string s) => Uri.EscapeDataString(s ?? string.Empty);
}
