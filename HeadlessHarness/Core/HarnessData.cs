using System.Globalization;
using System.Text;

namespace HeadlessHarness.Core;

// Machine-readable run output beside the human HarnessLog: one CSV per consumer-chosen tag, written
// in the same directory as the run log and sharing its run identity, so a measuring test (calibration
// sweep, benchmark) can emit rows a downstream tool parses instead of scraping them out of the log.
// No schema is imposed; column meaning is the consumer's business. Cells are formatted with the
// invariant culture (a decimal point regardless of machine locale) and CSV-escaped; keep cell content
// ASCII per project convention. Public so consumer IHarnessTests can use it.
//
// Each row is appended with its own open/close so nothing is lost when the harness ends the process
// with Environment.Exit (no graceful flush), the same best-effort model as HarnessLog.
public sealed class HarnessData
{
    private static readonly char[] CsvSpecials = { ',', '"', '\r', '\n' };

    public string FilePath { get; }

    private bool _appendFailureLogged;

    private HarnessData(string filePath) => FilePath = filePath;

    // Create (truncating any leftover) a CSV named <run-log-basename>-<tag>.csv next to the run log,
    // so it carries the run's timestamp+pid identity and the run script can list it by that prefix.
    // The optional header is written as the first row. The path is logged via HarnessLog so the run
    // is self-describing.
    public static HarnessData Create(string tag, string? header = null)
    {
        string safeTag = string.Join("_", tag.Split(Path.GetInvalidFileNameChars()));
        string dir = Path.GetDirectoryName(HarnessLog.FilePath) ?? HarnessLog.DataDirectory;
        string baseName = Path.GetFileNameWithoutExtension(HarnessLog.FilePath);
        string path = Path.Combine(dir, $"{baseName}-{safeTag}.csv");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, header != null ? header + Environment.NewLine : string.Empty);
            HarnessLog.Line($"[harness] data file: {path}");
        }
        catch (Exception e)
        {
            HarnessLog.Line($"[harness] data: could not create CSV '{path}': {e.Message}");
        }
        return new HarnessData(path);
    }

    // Append one row; each cell is invariant-culture formatted and CSV-escaped (AppendLine does neither).
    public void AppendRow(params object?[] cells)
    {
        StringBuilder sb = new();
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(EscapeCell(Format(cells[i])));
        }
        AppendLine(sb.ToString());
    }

    // Append a pre-built line verbatim; the consumer owns its formatting and escaping.
    public void AppendLine(string rawLine)
    {
        try
        {
            File.AppendAllText(FilePath, rawLine + Environment.NewLine);
        }
        catch (Exception e)
        {
            // A silently short data file reads as complete to a downstream parser (unlike a truncated
            // human log), so surface the first lost row. Once per file, to not flood on a stuck disk.
            if (!_appendFailureLogged)
            {
                _appendFailureLogged = true;
                HarnessLog.Line($"[harness] data: lost a row for '{FilePath}': {e.Message}");
            }
        }
    }

    private static string Format(object? cell) => cell switch
    {
        null => string.Empty,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => cell.ToString() ?? string.Empty,
    };

    // RFC 4180: quote a field containing a comma, quote, or newline, doubling embedded quotes.
    private static string EscapeCell(string s)
    {
        if (s.IndexOfAny(CsvSpecials) < 0)
            return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
