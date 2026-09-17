using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace IoTSpy.Scanner.Reports;

/// <summary>
/// Renders <see cref="ReportData"/> to PDF via QuestPDF's fluent layout DSL. Kept separate from
/// the Scriban HTML template — QuestPDF's page-flow model doesn't map onto a text template the
/// way HTML does, so the two renderers share <see cref="ReportData"/> but not markup.
/// </summary>
internal static class ReportPdfBuilder
{
    public static byte[] Build(ReportData data)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Text("IoTSpy Security Report")
                    .SemiBold().FontSize(18).FontColor(Colors.Blue.Darken2);

                page.Content().Column(col =>
                {
                    col.Spacing(10);

                    col.Item().Text($"Scope: {data.ScopeLabel}");
                    col.Item().Text($"Generated: {data.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");

                    if (data.Device is { } device)
                    {
                        col.Item().Text("Device Information").SemiBold().FontSize(13);
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c => { c.RelativeColumn(); c.RelativeColumn(2); });
                            t.Cell().Text("IP Address"); t.Cell().Text(device.IpAddress);
                            t.Cell().Text("MAC Address"); t.Cell().Text(device.MacAddress);
                            t.Cell().Text("Hostname"); t.Cell().Text(device.Hostname);
                            t.Cell().Text("Security Score"); t.Cell().Text(device.SecurityScore == -1 ? "Unscored" : device.SecurityScore.ToString());
                        });
                    }

                    if (data.Session is { } session)
                    {
                        col.Item().Text("Session Information").SemiBold().FontSize(13);
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c => { c.RelativeColumn(); c.RelativeColumn(2); });
                            t.Cell().Text("Name"); t.Cell().Text(session.Name);
                            t.Cell().Text("Created By"); t.Cell().Text(session.CreatedByUsername);
                            t.Cell().Text("Status"); t.Cell().Text(session.IsActive ? "Active" : "Closed");
                        });

                        col.Item().Text($"Activity Feed ({data.Activities.Count})").SemiBold().FontSize(12);
                        if (data.Activities.Count == 0)
                        {
                            col.Item().Text("No recorded activity.").Italic();
                        }
                        else
                        {
                            col.Item().Table(t =>
                            {
                                t.ColumnsDefinition(c => { c.RelativeColumn(); c.RelativeColumn(); c.RelativeColumn(2); });
                                t.Header(h =>
                                {
                                    h.Cell().Background(Colors.Grey.Lighten2).Text("User").SemiBold();
                                    h.Cell().Background(Colors.Grey.Lighten2).Text("Action").SemiBold();
                                    h.Cell().Background(Colors.Grey.Lighten2).Text("Details").SemiBold();
                                });
                                foreach (var a in data.Activities)
                                {
                                    t.Cell().Text(a.Username);
                                    t.Cell().Text(a.Action);
                                    t.Cell().Text(a.Details ?? "");
                                }
                            });
                        }
                    }

                    col.Item().Text("Findings by Severity").SemiBold().FontSize(13);
                    foreach (var group in data.FindingsBySeverity)
                    {
                        col.Item().Text($"{group.Severity} Findings ({group.Findings.Count})").SemiBold().FontSize(12);
                        if (group.Findings.Count == 0)
                        {
                            col.Item().Text("No findings.").Italic();
                            continue;
                        }
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn(2);
                                c.RelativeColumn(3);
                                c.RelativeColumn();
                            });
                            t.Header(h =>
                            {
                                h.Cell().Background(Colors.Grey.Lighten2).Text("Title").SemiBold();
                                h.Cell().Background(Colors.Grey.Lighten2).Text("Description").SemiBold();
                                h.Cell().Background(Colors.Grey.Lighten2).Text("CVE").SemiBold();
                            });
                            foreach (var f in group.Findings)
                            {
                                t.Cell().Text(f.Title);
                                t.Cell().Text(f.Description);
                                t.Cell().Text(f.CveId ?? "");
                            }
                        });
                    }

                    col.Item().Text($"Captures ({data.Captures.Count})").SemiBold().FontSize(13);
                    if (data.Captures.Count == 0)
                    {
                        col.Item().Text("No captures in scope.").Italic();
                    }
                    else
                    {
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn();
                                c.RelativeColumn();
                                c.RelativeColumn(2);
                                c.RelativeColumn();
                            });
                            t.Header(h =>
                            {
                                h.Cell().Background(Colors.Grey.Lighten2).Text("Method").SemiBold();
                                h.Cell().Background(Colors.Grey.Lighten2).Text("Status").SemiBold();
                                h.Cell().Background(Colors.Grey.Lighten2).Text("Host / Path").SemiBold();
                                h.Cell().Background(Colors.Grey.Lighten2).Text("TLS").SemiBold();
                            });
                            foreach (var c in data.Captures)
                            {
                                t.Cell().Text(c.Method);
                                t.Cell().Text(c.StatusCode.ToString());
                                t.Cell().Text($"{c.Host}{c.Path}");
                                t.Cell().Text(c.IsTls ? c.TlsVersion + (c.TlsLikelyDot ? " (likely DoT)" : "") : "-");
                            }
                        });
                    }

                    col.Item().Text($"Protocol Messages ({data.ProtocolMessages.Count})").SemiBold().FontSize(13);
                    if (data.ProtocolMessages.Count == 0)
                    {
                        col.Item().Text("No MQTT/DNS protocol messages recorded in scope.").Italic();
                    }
                    else
                    {
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn();
                                c.RelativeColumn();
                                c.RelativeColumn(2);
                            });
                            t.Header(h =>
                            {
                                h.Cell().Background(Colors.Grey.Lighten2).Text("Protocol").SemiBold();
                                h.Cell().Background(Colors.Grey.Lighten2).Text("Subject").SemiBold();
                                h.Cell().Background(Colors.Grey.Lighten2).Text("Summary").SemiBold();
                            });
                            foreach (var m in data.ProtocolMessages)
                            {
                                t.Cell().Text(m.Protocol.ToString());
                                t.Cell().Text(m.Subject ?? "");
                                t.Cell().Text(m.Summary);
                            }
                        });
                    }

                    if (data.Session is not null)
                    {
                        col.Item().Text($"Annotations ({data.Annotations.Count})").SemiBold().FontSize(13);
                        if (data.Annotations.Count == 0)
                        {
                            col.Item().Text("No annotations recorded.").Italic();
                        }
                        else
                        {
                            col.Item().Table(t =>
                            {
                                t.ColumnsDefinition(c => { c.RelativeColumn(); c.RelativeColumn(3); c.RelativeColumn(); });
                                t.Header(h =>
                                {
                                    h.Cell().Background(Colors.Grey.Lighten2).Text("User").SemiBold();
                                    h.Cell().Background(Colors.Grey.Lighten2).Text("Note").SemiBold();
                                    h.Cell().Background(Colors.Grey.Lighten2).Text("Tags").SemiBold();
                                });
                                foreach (var a in data.Annotations)
                                {
                                    t.Cell().Text(a.Username);
                                    t.Cell().Text(a.Note);
                                    t.Cell().Text(a.Tags ?? "");
                                }
                            });
                        }
                    }
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("Page ");
                    x.CurrentPageNumber();
                    x.Span(" of ");
                    x.TotalPages();
                });
            });
        });

        return doc.GeneratePdf();
    }
}
