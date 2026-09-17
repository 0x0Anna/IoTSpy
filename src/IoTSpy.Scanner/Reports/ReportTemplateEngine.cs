using Scriban;
using Scriban.Runtime;

namespace IoTSpy.Scanner.Reports;

/// <summary>
/// Renders <see cref="ReportData"/> into HTML via the embedded Scriban template. The template
/// is parsed once and cached — <see cref="Template"/> instances are safe to reuse/render
/// concurrently as long as each render gets its own model, which is the case here.
/// </summary>
internal static class ReportTemplateEngine
{
    private const string ResourceName = "IoTSpy.Scanner.Reports.Templates.report.scriban";

    private static readonly Template CachedTemplate = LoadTemplate();

    public static string Render(ReportData data)
    {
        // Scriban's default member renamer lowercases/snake-cases .NET property names;
        // keep them as-is so the template can reference e.g. `Device.IpAddress` directly.
        var root = new ScriptObject();
        root.Import(data, renamer: member => member.Name);

        // Scriban's builtin `date.to_string` only accepts DateTime, not DateTimeOffset (which
        // every timestamp in this model is) — a small custom formatter sidesteps that entirely.
        root.Import("fmt_date", new Func<object?, string, string>((value, format) => value switch
        {
            DateTimeOffset dto => dto.ToString(format),
            DateTime dt => dt.ToString(format),
            null => string.Empty,
            _ => value.ToString() ?? string.Empty
        }));

        var context = new TemplateContext { MemberRenamer = member => member.Name };
        context.PushGlobal(root);
        return CachedTemplate.Render(context);
    }

    private static Template LoadTemplate()
    {
        var assembly = typeof(ReportTemplateEngine).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded report template '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        var template = Template.Parse(text, ResourceName);
        if (template.HasErrors)
            throw new InvalidOperationException(
                $"Report template failed to parse: {string.Join("; ", template.Messages)}");
        return template;
    }
}
