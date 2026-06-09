using System.Text;
using System.Text.RegularExpressions;

namespace TelltaleD3DMeshEditor.Core;

// Turns a GitHub release body (Markdown, possibly with a little raw HTML such as an <img> banner) into a
// small self-contained HTML document so the update dialog can render it the way it looks on GitHub —
// headings, lists, a table, links and the image at its intended size — instead of showing the raw text.
// The content comes from the project's own releases, so it is trusted and not aggressively escaped.
public static class ReleaseNotesHtml
{
    public static string Build(string markdown)
    {
        var body = string.IsNullOrWhiteSpace(markdown)
            ? "<p>(No changelog was provided for this release.)</p>"
            : ConvertMarkdown(markdown);

        return "<!DOCTYPE html><html><head>"
            + "<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">"
            + "<meta charset=\"utf-8\">"
            + "<style>"
            + "body{font-family:'Segoe UI',Arial,sans-serif;font-size:13px;line-height:1.5;color:#1f2328;margin:14px;background:#ffffff;}"
            + "h1{font-size:22px;border-bottom:1px solid #d0d7de;padding-bottom:.3em;margin:.7em 0 .4em;}"
            + "h2{font-size:18px;border-bottom:1px solid #d0d7de;padding-bottom:.3em;margin:.7em 0 .4em;}"
            + "h3{font-size:15px;margin:.7em 0 .3em;}"
            + "ul{margin:.3em 0 .7em;padding-left:24px;}li{margin:.15em 0;}"
            + "a{color:#0969da;text-decoration:none;}"
            + "code{background:#eff1f3;padding:1px 5px;border-radius:5px;font-family:Consolas,monospace;font-size:12px;}"
            + "img{max-width:100%;height:auto;display:block;margin:.5em 0;}"
            + "table{border-collapse:collapse;margin:.6em 0;}th,td{border:1px solid #d0d7de;padding:5px 12px;}th{background:#f6f8fa;}"
            + "hr{border:0;border-top:1px solid #d0d7de;margin:1em 0;}p{margin:.4em 0;}"
            + "</style></head><body>"
            + body
            + "</body></html>";
    }

    private static string ConvertMarkdown(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var sb = new StringBuilder();
        var inList = false;

        void CloseList()
        {
            if (inList)
            {
                sb.Append("</ul>");
                inList = false;
            }
        }

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd();
            var t = line.TrimStart();

            if (t.StartsWith('|') && t.EndsWith('|'))
            {
                CloseList();
                i = AppendTable(sb, lines, i);
                continue;
            }

            if (t.Length == 0)
            {
                CloseList();
                i++;
                continue;
            }

            if (t.StartsWith("### ", StringComparison.Ordinal)) { CloseList(); sb.Append("<h3>").Append(Inline(t[4..])).Append("</h3>"); i++; continue; }
            if (t.StartsWith("## ", StringComparison.Ordinal)) { CloseList(); sb.Append("<h2>").Append(Inline(t[3..])).Append("</h2>"); i++; continue; }
            if (t.StartsWith("# ", StringComparison.Ordinal)) { CloseList(); sb.Append("<h1>").Append(Inline(t[2..])).Append("</h1>"); i++; continue; }
            if (t is "---" or "***" or "___") { CloseList(); sb.Append("<hr>"); i++; continue; }

            if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal))
            {
                if (!inList) { sb.Append("<ul>"); inList = true; }
                sb.Append("<li>").Append(Inline(t[2..])).Append("</li>");
                i++;
                continue;
            }

            // A line that is already raw HTML (e.g. the <img> banner) is passed straight through.
            if (t.StartsWith('<'))
            {
                CloseList();
                sb.Append(t);
                i++;
                continue;
            }

            CloseList();
            sb.Append("<p>").Append(Inline(line)).Append("</p>");
            i++;
        }

        CloseList();
        return sb.ToString();
    }

    private static int AppendTable(StringBuilder sb, string[] lines, int start)
    {
        var rows = new List<string[]>();
        var i = start;
        while (i < lines.Length)
        {
            var t = lines[i].Trim();
            if (!(t.StartsWith('|') && t.EndsWith('|')))
            {
                break;
            }

            rows.Add(t.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray());
            i++;
        }

        sb.Append("<table>");
        for (var r = 0; r < rows.Count; r++)
        {
            // The second row of a Markdown table is the |---|---| separator; skip it.
            if (r == 1 && rows[r].All(cell => cell.Length > 0 && cell.All(ch => ch is '-' or ':' or ' ')))
            {
                continue;
            }

            var tag = r == 0 ? "th" : "td";
            sb.Append("<tr>");
            foreach (var cell in rows[r])
            {
                sb.Append('<').Append(tag).Append('>').Append(Inline(cell)).Append("</").Append(tag).Append('>');
            }

            sb.Append("</tr>");
        }

        sb.Append("</table>");
        return i;
    }

    private static string Inline(string text)
    {
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "<b>$1</b>");
        text = Regex.Replace(text, @"`([^`]+?)`", "<code>$1</code>");
        text = Regex.Replace(text, @"\[([^\]]+)\]\((https?://[^\s)]+)\)", "<a href=\"$2\">$1</a>");
        // Linkify bare URLs that are not already part of an href/src attribute.
        text = Regex.Replace(text, "(?<![\"'>=])(https?://[^\\s<]+)", "<a href=\"$1\">$1</a>");
        return text;
    }
}
