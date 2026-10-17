using System.Net;
using System.Text;
using Ferry.Core.Infrastructure;
using Ferry.Core.Model;

namespace Ferry.Core.Reporting;

/// <summary>Renders a <see cref="MigrationReport"/> as a self-contained HTML page (no external files), plus a JSON copy.</summary>
public static class HtmlReport
{
    /// <summary>Saves the report under the reports folder (and a copy in <paramref name="alsoCopyTo"/>). Returns the HTML path.</summary>
    public static string Save(MigrationReport report, string? alsoCopyTo = null)
    {
        var stamp = report.StartedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var machine = report.Kind == OperationKind.Capture ? report.Source?.ComputerName : report.Target?.ComputerName ?? Environment.MachineName;
        var name = $"Ferry {Title(report)} {machine} {stamp}";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        var html = Render(report);
        var path = Path.Combine(AppPaths.Reports, name + ".html");
        File.WriteAllText(path, html, new UTF8Encoding(false));
        Json.WriteFile(Path.ChangeExtension(path, ".json"), report);
        if (alsoCopyTo is not null)
        {
            try
            {
                Directory.CreateDirectory(alsoCopyTo);
                File.WriteAllText(Path.Combine(alsoCopyTo, name + ".html"), html, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not copy the report to {alsoCopyTo}: {ex.Message}");
            }
        }
        Log.Info($"Report saved to {path}");
        return path;
    }

    public static string Title(MigrationReport report) => report.Kind switch
    {
        OperationKind.Capture => report.Target is null ? "saved migration" : "sent migration",
        OperationKind.Restore => report.DryRun ? "dry run" : "restore",
        OperationKind.Undo => "undo",
        _ => "report",
    };

    public static string Outcome(MigrationReport report) =>
        report.Cancelled ? "Cancelled"
        : report.FatalError is not null ? "Stopped"
        : report.HasFailures ? "Finished with problems"
        : report.HasWarnings ? "Finished with warnings"
        : "Finished";

    public static string Render(MigrationReport r)
    {
        var sb = new StringBuilder();
        var heading = r.Kind switch
        {
            OperationKind.Capture => $"Moved out of {E(r.Source?.ComputerName)}",
            OperationKind.Restore => r.DryRun ? $"Dry run: moving {E(r.Source?.ComputerName)} to {E(r.Target?.ComputerName)}" : $"Moved from {E(r.Source?.ComputerName)} to {E(r.Target?.ComputerName)}",
            OperationKind.Undo => $"Undo on {E(r.Target?.ComputerName)}",
            _ => "Ferry report",
        };
        var outcome = Outcome(r);
        var tone = r.Cancelled || r.FatalError is not null || r.HasFailures ? "bad" : r.HasWarnings ? "warn" : "good";
        var duration = r.FinishedUtc > r.StartedUtc ? Format.Duration(r.FinishedUtc - r.StartedUtc) : "";

        sb.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            """);
        sb.Append($"<title>Ferry — {E(heading)}</title>");
        sb.Append("<style>").Append(Css).Append("</style></head><body><main>");
        sb.Append($"<header><div class=\"brand\">Ferry</div><h1>{heading}</h1>");
        sb.Append($"<p class=\"meta\">{r.StartedUtc.ToLocalTime():dddd d MMMM yyyy, HH:mm}{(duration.Length > 0 ? $" · took {duration}" : "")}</p>");
        sb.Append($"<span class=\"badge {tone}\">{E(outcome)}</span></header>");

        if (r.FatalError is not null)
            sb.Append($"<section class=\"callout bad\"><strong>Stopped:</strong> {E(r.FatalError)}</section>");

        var failed = r.Items.Count(i => i.Status == ItemStatus.Failed);
        var warned = r.Items.Count(i => i.Status == ItemStatus.Warnings);
        sb.Append("<section class=\"stats\">");
        Stat(sb, r.Items.Count.ToString("N0"), r.Kind == OperationKind.Undo ? "groups of changes" : "items");
        Stat(sb, r.TotalFiles.ToString("N0"), r.Kind == OperationKind.Undo ? "changes reversed" : "files and settings");
        if (r.Kind != OperationKind.Undo)
            Stat(sb, Format.Bytes(r.TotalBytes), "of data");
        Stat(sb, failed.ToString("N0"), "with problems", failed > 0 ? "bad" : null);
        Stat(sb, warned.ToString("N0"), "with warnings", warned > 0 ? "warn" : null);
        sb.Append("</section>");

        if (r.ManualSteps.Count > 0)
        {
            sb.Append("<section><h2>Still to do</h2><p class=\"hint\">Things Ferry couldn't do by itself. Tick them off as you go.</p><ul class=\"todo\">");
            foreach (var step in r.ManualSteps)
                sb.Append($"<li><label><input type=\"checkbox\"> {E(step)}</label></li>");
            sb.Append("</ul></section>");
        }
        if (r.ProgramsNotMoved.Count > 0)
        {
            sb.Append("<section><h2>Programs that weren't reinstalled</h2>");
            sb.Append($"<p class=\"hint\">{Format.Count(r.ProgramsNotMoved.Count, "program")} from the old PC weren't selected or couldn't be reinstalled. Install the ones you still use.</p><ul class=\"todo programs\">");
            foreach (var program in r.ProgramsNotMoved)
                sb.Append($"<li><label><input type=\"checkbox\"> {E(program)}</label></li>");
            sb.Append("</ul></section>");
        }
        if (r.Notes.Count > 0)
        {
            sb.Append("<section><h2>Good to know</h2><ul>");
            foreach (var note in r.Notes)
                sb.Append($"<li>{E(note)}</li>");
            sb.Append("</ul></section>");
        }

        foreach (var category in r.Items.GroupBy(i => i.Category).OrderBy(g => g.Key))
        {
            sb.Append($"<section><h2>{E(Categories.DisplayName(category.Key))}</h2><table><thead><tr><th>Item</th><th>Result</th><th class=\"num\">Files</th><th class=\"num\">Size</th></tr></thead><tbody>");
            foreach (var item in category)
            {
                var (label, cls) = item.Status switch
                {
                    ItemStatus.Succeeded => ("Done", "good"),
                    ItemStatus.Warnings => ("Done, with notes", "warn"),
                    ItemStatus.Failed => ("Failed", "bad"),
                    ItemStatus.Skipped => ("Skipped", "muted"),
                    _ => ("Not started", "muted"),
                };
                sb.Append("<tr><td>");
                sb.Append($"<div class=\"name\">{E(item.Name)}</div>");
                if (item.Group.Length > 0)
                    sb.Append($"<div class=\"group\">{E(item.Group)}</div>");
                var messages = item.Messages.Where(m => m.Text.Length > 0).ToList();
                if (messages.Count > 0)
                {
                    var open = item.Status is ItemStatus.Failed or ItemStatus.Warnings && messages.Count <= 5 ? " open" : "";
                    sb.Append($"<details{open}><summary>{Format.Count(messages.Count, "note")}</summary><ul class=\"messages\">");
                    foreach (var m in messages)
                        sb.Append($"<li class=\"{m.Level.ToString().ToLowerInvariant()}\">{E(m.Text)}</li>");
                    sb.Append("</ul></details>");
                }
                sb.Append($"</td><td><span class=\"pill {cls}\">{label}</span></td>");
                sb.Append($"<td class=\"num\">{(item.FilesDone > 0 ? item.FilesDone.ToString("N0") : "")}{(item.FilesFailed > 0 ? $" <span class=\"bad-text\">({item.FilesFailed:N0} failed)</span>" : "")}</td>");
                sb.Append($"<td class=\"num\">{(item.BytesDone > 0 ? Format.Bytes(item.BytesDone) : "")}</td></tr>");
            }
            sb.Append("</tbody></table></section>");
        }

        sb.Append("<section><h2>Details</h2><dl>");
        Machine(sb, "Old PC", r.Source);
        Machine(sb, "New PC", r.Target);
        if (r.PackagePath is not null)
            Detail(sb, "Migration file", r.PackagePath);
        if (r.UndoId is not null)
            Detail(sb, "Undo id", $"{r.UndoId} — open Ferry and choose \"Undo a migration\" to reverse these changes.");
        if (r.LogPath is not null)
            Detail(sb, "Log file", r.LogPath);
        Detail(sb, "Ferry version", SystemInfo.AppVersion);
        sb.Append("</dl></section></main></body></html>");
        return sb.ToString();
    }

    private static void Stat(StringBuilder sb, string value, string label, string? tone = null) =>
        sb.Append($"<div class=\"stat{(tone is null ? "" : " " + tone)}\"><div class=\"value\">{E(value)}</div><div class=\"label\">{E(label)}</div></div>");

    private static void Machine(StringBuilder sb, string label, MachineInfo? m)
    {
        if (m is null)
            return;
        Detail(sb, label, $"{m.ComputerName} · {m.OsSummary} · {m.Manufacturer} {m.Model} · {m.JoinType}{(m.DomainName is null ? "" : $" ({m.DomainName})")}");
    }

    private static void Detail(StringBuilder sb, string term, string value) => sb.Append($"<dt>{E(term)}</dt><dd>{E(value)}</dd>");

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private const string Css = """
        :root{--bg:#f6f7f9;--card:#fff;--text:#1b1f24;--muted:#5d6672;--line:#e3e6ea;--accent:#0b6bcb;
          --good:#1a7f37;--good-bg:#e6f4ea;--warn:#9a6700;--warn-bg:#fff4d6;--bad:#c62828;--bad-bg:#fdeaea;--muted-bg:#eef0f3}
        @media (prefers-color-scheme:dark){:root{--bg:#14171b;--card:#1d2127;--text:#e8eaed;--muted:#9aa3ad;--line:#2d333b;--accent:#4aa3ff;
          --good:#5fd07a;--good-bg:#173d22;--warn:#e3b341;--warn-bg:#3d3013;--bad:#ff7b72;--bad-bg:#45191a;--muted-bg:#272c33}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif}
        main{max-width:960px;margin:0 auto;padding:32px 16px 64px}
        header{margin-bottom:24px}.brand{font-weight:700;color:var(--accent);letter-spacing:.04em;text-transform:uppercase;font-size:12px}
        h1{font-size:28px;margin:4px 0 4px;font-weight:600}h2{font-size:18px;margin:0 0 12px;font-weight:600}
        .meta,.hint,.group{color:var(--muted)}.meta{margin:0 0 12px}.hint{margin:-6px 0 12px}
        section{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:20px;margin-bottom:16px}
        .stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:12px;background:none;border:0;padding:0}
        .stat{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:14px 16px}
        .stat .value{font-size:24px;font-weight:600}.stat .label{color:var(--muted);font-size:13px}
        .stat.bad .value{color:var(--bad)}.stat.warn .value{color:var(--warn)}
        .badge,.pill{display:inline-block;border-radius:999px;padding:2px 10px;font-size:13px;font-weight:600;white-space:nowrap}
        .good{background:var(--good-bg);color:var(--good)}.warn{background:var(--warn-bg);color:var(--warn)}
        .bad{background:var(--bad-bg);color:var(--bad)}.muted{background:var(--muted-bg);color:var(--muted)}
        .callout{border-color:transparent}.bad-text{color:var(--bad)}
        table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:10px 8px;border-top:1px solid var(--line);vertical-align:top}
        thead th{border-top:0;color:var(--muted);font-weight:600;font-size:13px}.num{text-align:right;white-space:nowrap}
        .name{font-weight:500}.group{font-size:13px}details{margin-top:4px}summary{cursor:pointer;color:var(--accent);font-size:13px}
        ul{margin:0;padding-left:20px}.messages{font-size:13px;margin-top:6px}.messages .warning{color:var(--warn)}.messages .error{color:var(--bad)}
        .todo{list-style:none;padding:0}.todo li{padding:6px 0;border-top:1px solid var(--line)}.todo li:first-child{border-top:0}
        .todo input{margin-right:8px}.programs{columns:2;column-gap:24px;font-size:13px}.programs li{break-inside:avoid;border-top:0;padding:3px 0}
        @media (max-width:700px){.programs{columns:1}}dl{display:grid;grid-template-columns:max-content 1fr;gap:6px 16px;margin:0}dt{color:var(--muted)}dd{margin:0;word-break:break-word}
        @media (max-width:600px){th:nth-child(3),td:nth-child(3){display:none}h1{font-size:22px}}
        @media print{body{background:#fff}section,.stat{break-inside:avoid}}
        """;
}
