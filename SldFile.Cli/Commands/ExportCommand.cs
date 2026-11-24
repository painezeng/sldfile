using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using SldFile.Geometry;

namespace SldFile.Cli.Commands;

public static class ExportCommand
{
    public static int Execute(CommandLineOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.FilePath))
        {
            Console.Error.WriteLine("Error: File path is required for 'export' command. See 'sldfile-cli --help'.");
            return 1;
        }

        string fullPath = Path.GetFullPath(opts.FilePath);
        if (!File.Exists(fullPath))
        {
            Console.Error.WriteLine($"Error: File not found: '{fullPath}'");
            return 1;
        }

        // Determine format
        string format = (opts.Format ?? string.Empty).ToLowerInvariant().Trim();
        if (string.IsNullOrEmpty(format) && !string.IsNullOrEmpty(opts.OutputPath))
        {
            string ext = Path.GetExtension(opts.OutputPath).ToLowerInvariant().TrimStart('.');
            format = ext switch
            {
                "html" or "htm" => "html",
                "glb" or "gltf" => "glb",
                "obj" => "obj",
                "stl" => "stl",
                "svg" => "svg",
                "dxf" => "dxf",
                "xb" or "x_b" or "xmt_bin" or "parasolid" => "xb",
                "json" => "json",
                _ => string.Empty
            };
        }

        if (string.IsNullOrEmpty(format))
        {
            // Default format is html
            format = "html";
        }

        // Determine output path
        string outPath = opts.OutputPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(outPath))
        {
            string dir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            string name = Path.GetFileNameWithoutExtension(fullPath);
            string ext = format switch
            {
                "html" => ".html",
                "glb" => ".glb",
                "obj" => ".obj",
                "stl" => ".stl",
                "svg" => ".svg",
                "dxf" => ".dxf",
                "xb" => ".x_b",
                "json" => ".json",
                _ => $".{format}"
            };
            outPath = Path.Combine(dir, $"{name}{ext}");
        }

        string? outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        var sw = Stopwatch.StartNew();

        try
        {
            using var file = SwFile.Open(fullPath, readOnly: true);

            switch (format)
            {
                case "html":
                    return ExportHtml(file, fullPath, outPath, opts, sw);

                case "glb":
                    return ExportGlb(file, fullPath, outPath, sw);

                case "obj":
                    return ExportObj(file, fullPath, outPath, sw);

                case "stl":
                    return ExportStl(file, fullPath, outPath, sw);

                case "svg":
                    return ExportSvg(file, fullPath, outPath, opts, sw);

                case "dxf":
                    return ExportDxf(file, fullPath, outPath, opts, sw);

                case "xb":
                case "x_b":
                case "parasolid":
                    return ExportParasolid(file, fullPath, outPath, opts, sw);

                case "json":
                    return ExportJson(file, fullPath, outPath, opts, sw);

                default:
                    Console.Error.WriteLine($"Error: Unsupported export format '{format}'. Supported formats: html, glb, obj, stl, svg, dxf, xb, json.");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error during export: {ex.Message}");
            return 1;
        }
    }

    private static int ExportHtml(SwFile file, string inputPath, string outPath, CommandLineOptions opts, Stopwatch sw)
    {
        if (file.Kind == DocumentKind.Drawing)
        {
            Console.Error.WriteLine("Error: HTML 3D WebGL export is only available for Part (.SLDPRT) and Assembly (.SLDASM) models. For drawings, export to SVG or DXF.");
            return 1;
        }

        var exportOptions = new Html3DExportOptions
        {
            Title = Path.GetFileName(inputPath),
            Theme = !string.IsNullOrEmpty(opts.Theme) ? opts.Theme : "dark",
            IncludeEdges = true
        };

        file.ExportToHtml(outPath, Path.GetFileName(inputPath), exportOptions);
        sw.Stop();

        long bytes = new FileInfo(outPath).Length;
        PrintSuccess("HTML 3D WebGL Viewer", outPath, bytes, sw.ElapsedMilliseconds);
        return 0;
    }

    private static int ExportGlb(SwFile file, string inputPath, string outPath, Stopwatch sw)
    {
        var mesh = file.GetDisplayMesh();
        if (mesh == null || mesh.VertexCount == 0)
        {
            Console.Error.WriteLine($"Error: Document '{Path.GetFileName(inputPath)}' contains no 3D display mesh geometry.");
            return 1;
        }

        mesh.ExportGlb(outPath);
        sw.Stop();

        long bytes = new FileInfo(outPath).Length;
        PrintSuccess("glTF 2.0 Binary (.glb)", outPath, bytes, sw.ElapsedMilliseconds);
        return 0;
    }

    private static int ExportObj(SwFile file, string inputPath, string outPath, Stopwatch sw)
    {
        var mesh = file.GetDisplayMesh();
        if (mesh == null || mesh.VertexCount == 0)
        {
            Console.Error.WriteLine($"Error: Document '{Path.GetFileName(inputPath)}' contains no 3D display mesh geometry.");
            return 1;
        }

        mesh.ExportObj(outPath);
        sw.Stop();

        long bytes = new FileInfo(outPath).Length;
        PrintSuccess("Wavefront OBJ (.obj)", outPath, bytes, sw.ElapsedMilliseconds);
        return 0;
    }

    private static int ExportStl(SwFile file, string inputPath, string outPath, Stopwatch sw)
    {
        var mesh = file.GetDisplayMesh();
        if (mesh == null || mesh.VertexCount == 0)
        {
            Console.Error.WriteLine($"Error: Document '{Path.GetFileName(inputPath)}' contains no 3D display mesh geometry.");
            return 1;
        }

        mesh.ExportStl(outPath);
        sw.Stop();

        long bytes = new FileInfo(outPath).Length;
        PrintSuccess("Stereolithography STL (.stl)", outPath, bytes, sw.ElapsedMilliseconds);
        return 0;
    }

    private static int ExportSvg(SwFile file, string inputPath, string outPath, CommandLineOptions opts, Stopwatch sw)
    {
        if (file.Kind != DocumentKind.Drawing || file is not SldDrawing drw)
        {
            Console.Error.WriteLine("Error: SVG vector export is only available for 2D Engineering Drawings (.SLDDRW).");
            return 1;
        }

        var sheets = drw.Sheets;
        if (sheets == null || sheets.Count == 0)
        {
            Console.Error.WriteLine("Error: Drawing document contains no sheets to export.");
            return 1;
        }

        int sheetIdx = ResolveSheetIndex(drw, opts.Sheet);
        drw.ExportSheetSvg(sheetIdx, outPath);
        sw.Stop();

        long bytes = new FileInfo(outPath).Length;
        string sheetName = sheets.Count > sheetIdx ? sheets[sheetIdx].Name : $"Sheet {sheetIdx}";
        PrintSuccess($"SVG Vector Drawing ({sheetName})", outPath, bytes, sw.ElapsedMilliseconds);
        return 0;
    }

    private static int ExportDxf(SwFile file, string inputPath, string outPath, CommandLineOptions opts, Stopwatch sw)
    {
        if (file.Kind != DocumentKind.Drawing || file is not SldDrawing drw)
        {
            Console.Error.WriteLine("Error: DXF export is only available for 2D Engineering Drawings (.SLDDRW).");
            return 1;
        }

        var sheets = drw.Sheets;
        if (sheets == null || sheets.Count == 0)
        {
            Console.Error.WriteLine("Error: Drawing document contains no sheets to export.");
            return 1;
        }

        int sheetIdx = ResolveSheetIndex(drw, opts.Sheet);
        drw.ExportSheetDxf(sheetIdx, outPath);
        sw.Stop();

        long bytes = new FileInfo(outPath).Length;
        string sheetName = sheets.Count > sheetIdx ? sheets[sheetIdx].Name : $"Sheet {sheetIdx}";
        PrintSuccess($"AutoCAD DXF Drawing ({sheetName})", outPath, bytes, sw.ElapsedMilliseconds);
        return 0;
    }

    private static int ExportParasolid(SwFile file, string inputPath, string outPath, CommandLineOptions opts, Stopwatch sw)
    {
        int cfgIdx = 0;
        if (!string.IsNullOrEmpty(opts.Configuration) && file.Configurations != null)
        {
            for (int i = 0; i < file.Configurations.Count; i++)
            {
                if (string.Equals(file.Configurations[i].Name, opts.Configuration, StringComparison.OrdinalIgnoreCase))
                {
                    cfgIdx = file.Configurations[i].Index;
                    break;
                }
            }
        }

        byte[]? psData = file.ExtractParasolid(cfgIdx);
        if (psData == null || psData.Length == 0)
        {
            Console.Error.WriteLine($"Error: Document '{Path.GetFileName(inputPath)}' contains no Parasolid B-rep kernel stream in configuration {cfgIdx}.");
            return 1;
        }

        File.WriteAllBytes(outPath, psData);
        sw.Stop();

        PrintSuccess("Parasolid B-rep Model (.x_b)", outPath, psData.Length, sw.ElapsedMilliseconds);
        return 0;
    }

    private static int ExportJson(SwFile file, string inputPath, string outPath, CommandLineOptions opts, Stopwatch sw)
    {
        var cfgs = file.Configurations?.Select((c, idx) => new { Name = c.Name ?? $"Config-{c.Index}", Index = c.Index, IsDefault = idx == 0 }).ToList();
        var props = file.CustomProperties?.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? string.Empty);

        var data = new
        {
            File = Path.GetFileName(inputPath),
            FilePath = inputPath,
            Kind = file.Kind.ToString(),
            Author = file.Metadata?.Author,
            Title = file.Metadata?.Title,
            CreatedDate = file.Metadata?.CreatedAt,
            LastSavedDate = file.Metadata?.LastSavedAt,
            Configurations = cfgs,
            CustomProperties = props
        };

        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outPath, json);
        sw.Stop();

        long bytes = new FileInfo(outPath).Length;
        PrintSuccess("JSON Metadata", outPath, bytes, sw.ElapsedMilliseconds);
        return 0;
    }

    private static int ResolveSheetIndex(SldDrawing drw, string? sheetParam)
    {
        if (string.IsNullOrWhiteSpace(sheetParam)) return 0;

        if (int.TryParse(sheetParam, out int idx))
        {
            if (idx >= 0 && idx < drw.Sheets.Count) return idx;
        }

        for (int i = 0; i < drw.Sheets.Count; i++)
        {
            if (string.Equals(drw.Sheets[i].Name, sheetParam, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return 0;
    }

    private static void PrintSuccess(string formatName, string path, long bytes, long ms)
    {
        string sizeStr = bytes > 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0):F2} MB"
            : $"{bytes / 1024.0:F1} KB";

        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("✔ ");
        Console.ResetColor();
        Console.WriteLine($"Successfully exported {formatName} -> {path} ({sizeStr}, {ms} ms)");
    }
}
