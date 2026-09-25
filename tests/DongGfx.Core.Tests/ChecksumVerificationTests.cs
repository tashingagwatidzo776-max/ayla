using DongGfx.Core.Update;
using Xunit;

namespace DongGfx.Core.Tests;

/// <summary>
/// Parsing of the announced SHA-256 from release notes: the sha256sum
/// line and the labeled form are accepted, hex lines naming a different
/// asset are ignored (multi-asset releases cannot poison the verdict),
/// and damaged/absent hex yields null → the download falls back to the
/// size check alone. The download-side verdict (delete on mismatch) is
/// covered in AutoUpdaterTests against the loopback listener.
/// </summary>
[Trait("Category", "Unit")]
public class ChecksumVerificationTests
{
    private const string SumHash = "AABBCCDD00112233445566778899FFEEDDCCBBAA99887766554433221100FFEE";

    [Fact]
    public void Extract_Sha256sum_Format_Takes_The_Assets_Line()
    {
        const string notes =
            "DON G FX 1.2.0\n\n" +
            "## Verify\n" +
            SumHash + "  tf-1.2.0-win-x64.zip\n";

        var hash = AutoUpdater.ExtractChecksumFromNotes(notes, "tf-1.2.0-win-x64.zip");
        Assert.Equal(SumHash, hash);
    }

    [Fact]
    public void Extract_Labeled_Format_Also_Works()
    {
        Assert.Equal(SumHash,
            AutoUpdater.ExtractChecksumFromNotes("sha256: " + SumHash, "tf-1.2.0-win-x64.zip"));
        Assert.Equal(SumHash,
            AutoUpdater.ExtractChecksumFromNotes("SHA256 = " + SumHash, "tf.zip"));
    }

    [Fact]
    public void Extract_Ignores_Hex_Lines_Naming_A_Different_Asset()
    {
        // Multi-asset release: the first sha256sum line belongs to the
        // linux tarball — it must NOT be taken as the win-x64 zip's verdict.
        const string notes =
            "0011223344556677889900112233445566778899001122334455667788990011  tf-1.2.0-linux.tar.gz\n" +
            SumHash + "  tf-1.2.0-win-x64.zip\n";

        var hash = AutoUpdater.ExtractChecksumFromNotes(notes, "tf-1.2.0-win-x64.zip");
        Assert.Equal(SumHash, hash);
    }

    [Fact]
    public void Extract_No_Hex_At_All_Returns_Null()
    {
        Assert.Null(AutoUpdater.ExtractChecksumFromNotes("just release notes", "tf.zip"));
        Assert.Null(AutoUpdater.ExtractChecksumFromNotes("", "tf.zip"));
    }

    [Fact]
    public void Extract_Damaged_64_Char_Blob_Is_Ignored()
    {
        // 63 hex chars: not a checksum, must not be matched.
        Assert.Null(AutoUpdater.ExtractChecksumFromNotes(
            "AABBCCDD00112233445566778899FFEEDDCCBBAA99887766554433221100FF", "tf.zip"));
    }

    [Fact]
    public void Extract_Blank_Asset_Name_Returns_Null()
    {
        Assert.Null(AutoUpdater.ExtractChecksumFromNotes(SumHash + "  tf.zip", ""));
    }
}
