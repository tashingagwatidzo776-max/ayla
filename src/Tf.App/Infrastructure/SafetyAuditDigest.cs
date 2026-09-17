using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Tf.App.Infrastructure;

/// <summary>
/// Support for the safety-audit digest leg: the coverage table of
/// <c>docs/real-money-safety-audit.md</c> (which trade paths are guarded by
/// which rails) is hashed and posted to the webhook whenever it changes —
/// so monitoring sees a rail change after each release even if nobody reads
/// the doc. The last-posted hash is persisted next to the app's settings so
/// a quiet doc produces no posts.
/// </summary>
public static class SafetyAuditDigest
{
    /// <summary>Docs path of the audit inside the repo checkout the app runs
    /// from, or null when there is none (e.g. an installed copy).</summary>
    public static string? FindAuditPath(string startDirectory)
    {
        for (var dir = Path.GetFullPath(startDirectory); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var docs = Path.Combine(dir, "docs");
            if (Directory.Exists(docs) && Directory.Exists(Path.Combine(dir, ".git")))
            {
                return Path.Combine(docs, "real-money-safety-audit.md");
            }
        }

        return null;
    }

    /// <summary>Extracts the coverage-table section: from the "## The rails"
    /// heading through every "## Path N" table, up to (excluding) the first
    /// later heading. Returns null when the doc does not contain the section
    /// — a structurally broken audit must not post a misleading digest.</summary>
    public static string? ExtractCoverageTable(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var lines = markdown.Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimStart().StartsWith("## The rails", StringComparison.Ordinal));
        if (start < 0)
        {
            return null;
        }

        var end = start + 1;
        while (end < lines.Length && !lines[end].TrimStart().StartsWith("## ", StringComparison.Ordinal))
        {
            end++;
        }

        var table = string.Join('\n', lines[start..end]).Trim();
        return table.Length > 0 ? table : null;
    }

    /// <summary>Stable short hash of the table text (first 16 hex chars of
    /// SHA-256) — enough to detect any change, never used as a secret.</summary>
    public static string Hash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>The hash recorded at the last audit post, or null when the
    /// audit has never been posted from this machine.</summary>
    public static string? ReadStateHash(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null; // unreadable state ⇒ treat as never posted
        }
    }

    /// <summary>Persists the hash of the just-posted table (best effort —
    /// a failed write only means the next tick posts the table again).</summary>
    public static void WriteStateHash(string path, string hash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, hash);
    }
}
