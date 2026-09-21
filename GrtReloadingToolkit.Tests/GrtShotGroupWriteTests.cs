using System.Text.RegularExpressions;
using GrtPluginKit.Grt;
using Xunit;

namespace GrtReloadingToolkit.Tests;

public class GrtShotGroupWriteTests
{
    /// <summary>A 4x4 PNG, so AddShotGroup has a real IHDR to read width and height from.</summary>
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAFUlEQVR4nGP8z8DwnwEPYMKn" +
        "eBQMKgAAtM8BvQ3Y7HAAAAAASUVORK5CYII=");

    private static GrtLoadDoc NewDoc() =>
        GrtLoadDoc.CreateMinimal("test", Path.Combine(Path.GetTempPath(), "test.grtload"));

    [Fact]
    public void WritesATabThatItsOwnReaderUnderstands()
    {
        var doc = NewDoc();
        var set = new GrtShotGroupSet { Name = "41.5gr" };
        set.Points.Add(new GrtShotPoint(0.25, 0.25, false, false));
        set.Points.Add(new GrtShotPoint(0.75, 0.60, true, false));
        set.Points.Add(new GrtShotPoint(0.50, 0.50, false, true));

        doc.AddShotGroup("ShotMarker — M6 R1", TinyPng(),
            new ShotGroupGeometry(0.1, 0.5, 0.9, 0.5, 254, 914.4), new[] { set });

        GrtShotGroup tab = Assert.Single(doc.ShotGroups());
        Assert.Equal("ShotMarker — M6 R1", tab.Title);
        Assert.Equal(0.1, tab.RefP1X, 6);
        Assert.Equal(0.9, tab.RefP2X, 6);
        Assert.Equal(254, tab.RefDistance, 6);
        Assert.Equal(914.4, tab.ShootDistance, 6);
        Assert.Equal(4, tab.ImageWidth);
        Assert.Equal(4, tab.ImageHeight);

        GrtShotGroupSet back = Assert.Single(tab.Groups);
        Assert.Equal("41.5gr", back.Name);
        Assert.Equal(3, back.Points.Count);
        Assert.Contains(back.Points, p => p.Flyer);
        Assert.Contains(back.Points, p => p.PointOfAim);
    }

    [Fact]
    public void SurvivesASaveAndReload()
    {
        var doc = NewDoc();
        var set = new GrtShotGroupSet { Name = "g" };
        set.Points.Add(new GrtShotPoint(0.3125, 0.6875, false, false));
        doc.AddShotGroup("Group", TinyPng(),
            new ShotGroupGeometry(0, 0.5, 1, 0.5, 254, 914.4), new[] { set });

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".grtload");
        try
        {
            doc.Save(path);
            GrtShotGroup tab = Assert.Single(GrtLoadDoc.Load(path).ShotGroups());
            GrtShotPoint p = Assert.Single(Assert.Single(tab.Groups).Points);
            Assert.Equal(0.3125, p.X, 6);
            Assert.Equal(0.6875, p.Y, 6);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Two things at once, both about <see cref="GrtLoadDoc.SaveSibling"/>:
    ///
    /// 1. A "shotmarker" family sibling must never be pruned by an unrelated "toolkit" family
    ///    save (the reason <c>family</c> exists at all -- importing four strings used to funnel
    ///    every plugin's output into one shared, 3-deep "_toolkit_" family).
    ///
    /// 2. Saving a sibling FROM a sibling's own path must reuse the same stem rather than
    ///    compounding it. Before <c>StemRx</c>'s second alternative was widened from
    ///    <c>\d{4}</c> to <c>\d{4,6}</c>, a family stem (e.g. "shotmarker") with the 6-digit
    ///    HHmmss stamp <c>SaveSibling</c> actually writes never matched, so the stem was never
    ///    stripped and re-saving grew the filename without bound.
    ///
    /// The "toolkit" family half is proven with hand-authored files at fixed, explicit
    /// timestamps (<see cref="File.SetLastWriteTimeUtc"/>) rather than by looping real
    /// <c>SaveSibling</c> calls: those calls embed <c>DateTime.Now</c> at second precision in
    /// the filename itself, so a tight loop can collide on the same name and silently produce
    /// only one file. Explicit timestamps make the "keep the 3 newest" prune deterministic
    /// however fast the test runs.
    /// </summary>
    [Fact]
    public void SiblingFamiliesDoNotPruneEachOther()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string src = Path.Combine(dir, "load.grtload");
            GrtLoadDoc.CreateMinimal("t", src).Save(src);

            // Pre-seed the "toolkit" family (the default every existing call site still gets)
            // with 4 siblings at fixed timestamps well in the past, so the real SaveSibling
            // call below is unambiguously "newest" and pruning-to-3 is observable without
            // depending on wall-clock timing.
            var anchor = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var toolkitFiles = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                DateTime t = anchor.AddMinutes(i);
                string p = Path.Combine(dir, $"load_toolkit_{t:yyyyMMdd_HHmmss}.grtload");
                File.WriteAllText(p, "<x/>");
                File.SetLastWriteTimeUtc(p, t);
                toolkitFiles.Add(p);
            }

            // A "shotmarker" sibling: a distinct family, must survive the toolkit family's own pruning.
            string a = GrtLoadDoc.Load(src).SaveSibling("import", "shotmarker");

            // Save a sibling FROM the sibling's own path -- must not compound the stem.
            string b = GrtLoadDoc.Load(a).SaveSibling("import", "shotmarker");

            static string StemOf(string path)
            {
                Match m = Regex.Match(Path.GetFileNameWithoutExtension(path), @"^(.*)_shotmarker_\d{8}_\d{6}$");
                Assert.True(m.Success, $"unexpected sibling filename shape: {Path.GetFileName(path)}");
                return m.Groups[1].Value;
            }

            Assert.Equal("load", StemOf(a));
            Assert.Equal(StemOf(a), StemOf(b));
            Assert.True(File.Exists(a), "a shotmarker sibling was pruned by the toolkit family");
            Assert.True(File.Exists(b), "a shotmarker sibling was pruned by its own family");
            Assert.Contains("_shotmarker_", Path.GetFileName(a));

            // One real toolkit-family save: must keep only the 3 newest of THAT family (the two
            // newest pre-seeded files plus the file just written), and must not touch the
            // shotmarker family at all.
            string newest = GrtLoadDoc.Load(src).SaveSibling("ladder");

            Assert.True(File.Exists(newest));
            Assert.True(File.Exists(toolkitFiles[3]), "newest pre-seeded toolkit sibling was pruned");
            Assert.True(File.Exists(toolkitFiles[2]), "2nd-newest pre-seeded toolkit sibling was pruned");
            Assert.False(File.Exists(toolkitFiles[1]), "toolkit family was not pruned to its 3 newest");
            Assert.False(File.Exists(toolkitFiles[0]), "toolkit family was not pruned to its 3 newest");
            Assert.True(File.Exists(a), "shotmarker sibling was pruned by an unrelated toolkit save");
            Assert.True(File.Exists(b), "shotmarker sibling was pruned by an unrelated toolkit save");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
