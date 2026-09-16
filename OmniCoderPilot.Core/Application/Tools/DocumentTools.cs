using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using S = DocumentFormat.OpenXml.Spreadsheet;
using P = DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace OmniCoderPilot.Application.Tools;

// ── Word Tool ─────────────────────────────────────────────────────────────────

public sealed class CreateWordDocumentTool(IWorkspaceFileService files) : IAgentTool
{
    public string Name => "CreateWordDocument";
    public string Description =>
        "Create a professional Word (.docx) document from a JSON structure. " +
        "Use this when the user asks for a CV, resume, report, letter, or any Word document.";
    public JsonObject Schema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["relativePath"] = new JsonObject { ["type"] = "string", ["description"] = "File path, e.g. 'CV.docx'" },
            ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Document title" },
            ["sections"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Sections with 'heading' and 'content' arrays",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["heading"] = new JsonObject { ["type"] = "string" },
                        ["content"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject() }
                    }
                }
            }
        },
        ["required"] = new JsonArray { "relativePath", "sections" }
    };
    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var path = ToolJson.String(input, "relativePath");
        var title = ToolJson.String(input, "title", "");
        var sections = input["sections"] as JsonArray ?? [];

        var fullPath = files.ResolveInsideWorkspace(context.WorkspaceRoot, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);

        var mainPart = doc.AddMainDocumentPart();
        mainPart.Document = new W.Document();
        var body = mainPart.Document.AppendChild(new W.Body());

        var stylesPart = mainPart.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new W.Styles(
            new W.Style(
                new W.StyleName { Val = "Normal" },
                new W.PrimaryStyle(),
                new W.StyleRunProperties(
                    new W.RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" },
                    new W.FontSize { Val = "22" }))
            { Type = W.StyleValues.Paragraph, StyleId = "Normal", Default = true },
            new W.Style(
                new W.StyleName { Val = "Title" },
                new W.BasedOn { Val = "Normal" },
                new W.StyleParagraphProperties(
                    new W.Justification { Val = W.JustificationValues.Center }),
                new W.StyleRunProperties(
                    new W.RunFonts { Ascii = "Calibri Light" },
                    new W.FontSize { Val = "32" },
                    new W.Color { Val = "1F3864" }))
            { Type = W.StyleValues.Paragraph, StyleId = "Title" },
            new W.Style(
                new W.StyleName { Val = "heading 1" },
                new W.BasedOn { Val = "Normal" },
                new W.StyleParagraphProperties(
                    new W.ParagraphBorders(
                        new W.BottomBorder { Val = W.BorderValues.Single, Size = 6, Color = "1F3864" })),
                new W.StyleRunProperties(
                    new W.RunFonts { Ascii = "Calibri Light" },
                    new W.FontSize { Val = "24" },
                    new W.Color { Val = "1F3864" },
                    new W.Bold()))
            { Type = W.StyleValues.Paragraph, StyleId = "Heading1" });

        if (!string.IsNullOrWhiteSpace(title))
        {
            body.AppendChild(MakeParagraph(title, "Title", bold: true, fontSize: 32));
            body.AppendChild(MakeParagraph(""));
        }

        foreach (var section in sections)
        {
            var heading = section["heading"]?.GetValue<string>() ?? "";
            var content = section["content"] as JsonArray ?? [];

            if (!string.IsNullOrWhiteSpace(heading))
                body.AppendChild(MakeParagraph(heading, "Heading1", bold: true, fontSize: 24));

            foreach (var line in content)
            {
                if (line is JsonValue sv && sv.TryGetValue<string>(out var text))
                    body.AppendChild(MakeParagraph(text));
                else if (line is JsonObject obj)
                {
                    var label = obj["label"]?.GetValue<string>() ?? "";
                    var value = obj["value"]?.GetValue<string>() ?? "";
                    body.AppendChild(MakeLabelParagraph(label, value));
                }
            }
        }

        doc.Save();
        return new(Name, true, $"Created {path} ({sections.Count} sections)");
    }

    private static W.Paragraph MakeParagraph(string text, string? styleId = null, bool bold = false, int fontSize = 22)
    {
        var para = new W.Paragraph();
        if (styleId != null)
            para.AppendChild(new W.ParagraphProperties(new W.ParagraphStyleId { Val = styleId }));
        var run = new W.Run();
        var rp = new W.RunProperties();
        if (bold) rp.AppendChild(new W.Bold());
        if (fontSize != 22) rp.AppendChild(new W.FontSize { Val = fontSize.ToString() });
        run.AppendChild(rp);
        run.AppendChild(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });
        para.AppendChild(run);
        return para;
    }

    private static W.Paragraph MakeLabelParagraph(string label, string value)
    {
        var para = new W.Paragraph();
        para.AppendChild(new W.ParagraphProperties(new W.Indentation { Left = "360" }));
        var lr = new W.Run();
        lr.AppendChild(new W.RunProperties(new W.Bold()));
        lr.AppendChild(new W.Text(label + ": ") { Space = SpaceProcessingModeValues.Preserve });
        para.AppendChild(lr);
        var vr = new W.Run();
        vr.AppendChild(new W.Text(value) { Space = SpaceProcessingModeValues.Preserve });
        para.AppendChild(vr);
        return para;
    }
}

// ── Excel Tool ────────────────────────────────────────────────────────────────

public sealed class CreateExcelDocumentTool(IWorkspaceFileService files) : IAgentTool
{
    public string Name => "CreateExcelDocument";
    public string Description =>
        "Create an Excel (.xlsx) workbook from a JSON structure with sheets. " +
        "Use this when the user asks for a spreadsheet, table, budget, or any Excel file.";
    public JsonObject Schema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["relativePath"] = new JsonObject { ["type"] = "string", ["description"] = "File path, e.g. 'Report.xlsx'" },
            ["sheets"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Sheets with 'name', optional 'headers', and 'rows'",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["name"] = new JsonObject { ["type"] = "string" },
                        ["headers"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject() },
                        ["rows"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject() }
                    }
                }
            }
        },
        ["required"] = new JsonArray { "relativePath", "sheets" }
    };
    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var path = ToolJson.String(input, "relativePath");
        var sheets = input["sheets"] as JsonArray ?? [];

        var fullPath = files.ResolveInsideWorkspace(context.WorkspaceRoot, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook);

        var wbPart = doc.AddWorkbookPart();
        wbPart.Workbook = new S.Workbook();
        var sheetsElement = wbPart.Workbook.AppendChild(new S.Sheets());

        uint sid = 1;
        foreach (var sheet in sheets)
        {
            var name = sheet["name"]?.GetValue<string>() ?? $"Sheet{sid}";
            var headers = sheet["headers"] as JsonArray ?? [];
            var rows = sheet["rows"] as JsonArray ?? [];

            var sheetPart = wbPart.AddNewPart<WorksheetPart>();
            sheetPart.Worksheet = new S.Worksheet();
            var sd = sheetPart.Worksheet.AppendChild(new S.SheetData());

            if (headers.Count > 0)
            {
                var hr = new S.Row { RowIndex = 1 };
                for (int c = 0; c < headers.Count; c++)
                {
                    hr.AppendChild(new S.Cell
                    {
                        CellReference = ColLetter(c) + "1",
                        DataType = S.CellValues.String,
                        CellValue = new S.CellValue(headers[c]?.GetValue<string>() ?? "")
                    });
                }
                sd.AppendChild(hr);
            }

            uint rn = headers.Count > 0 ? 2u : 1u;
            foreach (var row in rows)
            {
                if (row is JsonArray cells)
                {
                    var dr = new S.Row { RowIndex = rn };
                    for (int c = 0; c < cells.Count; c++)
                    {
                        var cv = cells[c];
                        var cell = new S.Cell { CellReference = ColLetter(c) + rn };
                        if (cv is JsonValue numVal && numVal.TryGetValue<double>(out var d))
                        {
                            cell.DataType = S.CellValues.Number;
                            cell.CellValue = new S.CellValue(d);
                        }
                        else
                        {
                            cell.DataType = S.CellValues.String;
                            cell.CellValue = new S.CellValue(cv?.GetValue<string>() ?? "");
                        }
                        dr.AppendChild(cell);
                    }
                    sd.AppendChild(dr);
                    rn++;
                }
            }

            sheetsElement.AppendChild(new S.Sheet
            {
                Id = wbPart.GetIdOfPart(sheetPart),
                SheetId = sid,
                Name = name
            });
            sid++;
        }

        doc.Save();
        return new(Name, true, $"Created {path} ({sheets.Count} sheet(s))");
    }

    private static string ColLetter(int col)
    {
        col++;
        var r = "";
        while (col > 0) { col--; r = (char)('A' + col % 26) + r; col /= 26; }
        return r;
    }
}

// ── PowerPoint Tool ───────────────────────────────────────────────────────────

public sealed class CreatePowerPointDocumentTool(IWorkspaceFileService files) : IAgentTool
{
    public string Name => "CreatePowerPointDocument";
    public string Description =>
        "Create a PowerPoint (.pptx) presentation from a JSON structure with slides. " +
        "Use this when the user asks for a presentation, slideshow, or any PowerPoint file.";
    public JsonObject Schema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["relativePath"] = new JsonObject { ["type"] = "string", ["description"] = "File path, e.g. 'Slides.pptx'" },
            ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Presentation title" },
            ["slides"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Slides with 'title' and optional 'content' bullets",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["title"] = new JsonObject { ["type"] = "string" },
                        ["content"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject() }
                    }
                }
            }
        },
        ["required"] = new JsonArray { "relativePath", "slides" }
    };
    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject input, ToolExecutionContext context, CancellationToken ct)
    {
        var path = ToolJson.String(input, "relativePath");
        var title = ToolJson.String(input, "title", "");
        var slides = input["slides"] as JsonArray ?? [];

        var fullPath = files.ResolveInsideWorkspace(context.WorkspaceRoot, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var doc = PresentationDocument.Create(stream, PresentationDocumentType.Presentation);

        var presPart = doc.AddPresentationPart();
        presPart.Presentation = new P.Presentation();

        var smPart = presPart.AddNewPart<SlideMasterPart>();
        smPart.SlideMaster = new P.SlideMaster(
            new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new A.TransformGroup()))),
            new P.ColorMap());

        var slPart = smPart.AddNewPart<SlideLayoutPart>();
        slPart.SlideLayout = new P.SlideLayout(
            new P.CommonSlideData(new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new A.TransformGroup()))));

        smPart.SlideMaster.AppendChild(new P.SlideLayoutIdList(
            new P.SlideLayoutId { Id = 256, RelationshipId = presPart.GetIdOfPart(slPart) }));

        var slideIdList = presPart.Presentation.AppendChild(new P.SlideIdList());
        uint slideNum = 256;

        if (!string.IsNullOrWhiteSpace(title))
        {
            var titlePart = presPart.AddNewPart<SlidePart>();
            titlePart.Slide = MakeSlide(title, "", true);
            slideIdList.AppendChild(new P.SlideId { Id = slideNum++, RelationshipId = presPart.GetIdOfPart(titlePart) });
        }

        foreach (var s in slides)
        {
            var sTitle = s["title"]?.GetValue<string>() ?? "";
            var content = s["content"] as JsonArray ?? [];
            var bullets = string.Join("\n", content.Where(c => c is JsonValue).Select(c => c!.GetValue<string>()));

            var sp = presPart.AddNewPart<SlidePart>();
            sp.Slide = MakeSlide(sTitle, bullets);
            slideIdList.AppendChild(new P.SlideId { Id = slideNum++, RelationshipId = presPart.GetIdOfPart(sp) });
        }

        doc.Save();
        return new(Name, true, $"Created {path} ({slideIdList.Count()} slides)");
    }

    private static P.Slide MakeSlide(string titleText, string bodyText, bool isTitleSlide = false)
    {
        var tree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()));

        uint sid = 2;
        tree.AppendChild(MakeTextShape(sid++, titleText,
            isTitleSlide ? 1600000 : 457200, isTitleSlide ? 2600000 : 457200,
            isTitleSlide ? 6858000 : 8229600, isTitleSlide ? 1200000 : 1000000));

        if (!string.IsNullOrWhiteSpace(bodyText))
        {
            tree.AppendChild(MakeTextShape(sid++, bodyText,
                1371600, isTitleSlide ? 4000000 : 1600000,
                7467600, isTitleSlide ? 4200000 : 5000000));
        }

        return new P.Slide(new P.CommonSlideData(tree));
    }

    private static P.Shape MakeTextShape(uint id, string text,
        long left, long top, long width, long height)
    {
        var tb = new P.TextBody();
        var bodyProps = new A.BodyProperties { Wrap = A.TextWrappingValues.Square };
        tb.AppendChild(bodyProps);

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var para = new A.Paragraph();
            para.AppendChild(new A.ParagraphProperties());
            var run = new A.Run();
            run.AppendChild(new A.RunProperties());
            run.AppendChild(new A.Text(line));
            para.AppendChild(run);
            tb.AppendChild(para);
        }

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = $"Shape{id}" },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = width, Cy = height }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
            tb);
    }
}
