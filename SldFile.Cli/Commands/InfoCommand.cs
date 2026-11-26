using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SldFile.Cli.Commands;

public static class InfoCommand
{
    public static int Execute(CommandLineOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.FilePath))
        {
            Console.Error.WriteLine("Error: File path is required for 'info' command. See 'sldfile-cli --help'.");
            return 1;
        }

        string fullPath = Path.GetFullPath(opts.FilePath);
        if (!File.Exists(fullPath))
        {
            Console.Error.WriteLine($"Error: File not found: '{fullPath}'");
            return 1;
        }

        try
        {
            using var file = SwFile.Open(fullPath, readOnly: true);

            var cfgs = file.Configurations?.Select((c, idx) => new
            {
                Name = c.Name ?? $"Config-{c.Index}",
                Index = c.Index,
                IsDefault = idx == 0
            }).ToList();

            var props = new Dictionary<string, string>();
            if (file.CustomProperties != null)
            {
                foreach (var p in file.CustomProperties)
                {
                    props[p.Key] = p.Value?.ToString() ?? string.Empty;
                }
            }

            // Geometry and Mesh info
            object? meshInfo = null;
            if (file.Kind != DocumentKind.Drawing)
            {
                var mesh = file.GetDisplayMesh();
                if (mesh != null && mesh.VertexCount > 0)
                {
                    var size = mesh.Max - mesh.Min;
                    meshInfo = new
                    {
                        Vertices = mesh.VertexCount,
                        Triangles = mesh.TriangleCount,
                        Edges = mesh.EdgeCount,
                        BoundingBox = new
                        {
                            Min = new[] { mesh.Min.X, mesh.Min.Y, mesh.Min.Z },
                            Max = new[] { mesh.Max.X, mesh.Max.Y, mesh.Max.Z },
                            SizeMm = new[] { size.X, size.Y, size.Z },
                            DimensionsText = $"{size.X:F1} × {size.Y:F1} × {size.Z:F1} mm"
                        },
                        PartsCount = mesh.Parts?.Count ?? 0
                    };
                }
            }

            // Assembly info
            object? asmInfo = null;
            if (file.Kind == DocumentKind.Assembly)
            {
                var tree = file.GetAssemblyTree();
                int topCount = tree.Count;
                int flatCount = CountFlattened(tree);
                asmInfo = new
                {
                    TopLevelComponents = topCount,
                    TotalInstances = flatCount
                };
            }

            // Drawing info
            object? drawingInfo = null;
            if (file.Kind == DocumentKind.Drawing)
            {
                var sheets = file.GetDrawingSheets()?.Select(s => new
                {
                    Index = s.SheetIndex,
                    Name = s.Name,
                    Width = s.VectorData?.PaperWidthMm ?? 0.0,
                    Height = s.VectorData?.PaperHeightMm ?? 0.0,
                    Scale = (s.VectorData != null && s.VectorData.ScaleDenominator > 0) ? $"{s.VectorData.ScaleNumerator}:{s.VectorData.ScaleDenominator}" : "1:1",
                    ViewsCount = s.Views?.Count ?? 0,
                    HasVectorData = s.VectorData != null
                }).ToList();

                drawingInfo = new
                {
                    SheetCount = sheets?.Count ?? 0,
                    Sheets = sheets
                };
            }

            string author = file.Metadata?.Author ?? file.SolidWorksInformation?.Author ?? string.Empty;
            string title = file.Metadata?.Title ?? file.SolidWorksInformation?.Title ?? string.Empty;
            string comments = file.Metadata?.Comments ?? file.SolidWorksInformation?.Comments ?? string.Empty;
            string versionStr = FormatSolidWorksVersion(file);
            DateTimeOffset? createdDate = file.Metadata?.CreatedAt;
            DateTimeOffset? lastSavedDate = file.Metadata?.LastSavedAt;

            if (opts.JsonOutput)
            {
                object jsonPayload;
                if (opts.PropsOnly)
                {
                    jsonPayload = props;
                }
                else
                {
                    jsonPayload = new
                    {
                        File = Path.GetFileName(fullPath),
                        FilePath = fullPath,
                        Kind = file.Kind.ToString(),
                        Version = versionStr,
                        Author = author,
                        Title = title,
                        Comments = comments,
                        CreatedDate = createdDate,
                        LastSavedDate = lastSavedDate,
                        Configurations = cfgs,
                        CustomProperties = props,
                        DisplayMesh = meshInfo,
                        Assembly = asmInfo,
                        Drawing = drawingInfo
                    };
                }

                string json = JsonSerializer.Serialize(jsonPayload, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(json);
                return 0;
            }

            // Human-readable formatted terminal output
            if (opts.PropsOnly)
            {
                Console.WriteLine($"[Custom Properties: {Path.GetFileName(fullPath)}] ({props.Count} properties)");
                foreach (var kvp in props)
                {
                    Console.WriteLine($"  {kvp.Key,-24}: {kvp.Value}");
                }
                return 0;
            }

            Console.WriteLine("================================================================================");
            Console.WriteLine($" SolidWorks Document Information: {Path.GetFileName(fullPath)}");
            Console.WriteLine("================================================================================");
            Console.WriteLine($"  File Path       : {fullPath}");
            Console.WriteLine($"  Document Kind   : {file.Kind} ({(file.Kind == DocumentKind.Part ? ".SLDPRT" : file.Kind == DocumentKind.Assembly ? ".SLDASM" : ".SLDDRW")})");
            if (!string.IsNullOrEmpty(versionStr))
                Console.WriteLine($"  SW Version      : {versionStr}");
            if (!string.IsNullOrEmpty(author))
                Console.WriteLine($"  Author          : {author}");
            if (createdDate.HasValue)
                Console.WriteLine($"  Created Date    : {createdDate.Value:yyyy-MM-dd HH:mm:ss}");
            if (lastSavedDate.HasValue)
                Console.WriteLine($"  Last Saved      : {lastSavedDate.Value:yyyy-MM-dd HH:mm:ss}");

            if (cfgs != null && cfgs.Count > 0)
            {
                Console.WriteLine($"  Configurations  : {cfgs.Count} [{string.Join(", ", cfgs.Select(c => c.Name + (c.IsDefault ? " (Default)" : "")))}]");
            }

            if (file.Kind != DocumentKind.Drawing)
            {
                Console.WriteLine();
                Console.WriteLine("  [3D Geometry & Display Mesh]");
                var mesh = file.GetDisplayMesh();
                if (mesh != null && mesh.VertexCount > 0)
                {
                    var size = mesh.Max - mesh.Min;
                    Console.WriteLine($"    Vertices      : {mesh.VertexCount:N0}");
                    Console.WriteLine($"    Triangles     : {mesh.TriangleCount:N0}");
                    Console.WriteLine($"    Outline Edges : {mesh.EdgeCount:N0}");
                    Console.WriteLine($"    Bounding Box  : {size.X:F1} × {size.Y:F1} × {size.Z:F1} mm");
                    if (mesh.Parts != null && mesh.Parts.Count > 0)
                    {
                        Console.WriteLine($"    Parts/Surfaces: {mesh.Parts.Count:N0}");
                    }
                }
                else
                {
                    Console.WriteLine("    No 3D tessellation mesh found in document.");
                }
            }

            if (file.Kind == DocumentKind.Assembly)
            {
                var tree = file.GetAssemblyTree();
                Console.WriteLine();
                Console.WriteLine("  [Assembly Components]");
                Console.WriteLine($"    Top-Level     : {tree.Count}");
                Console.WriteLine($"    Total Nodes   : {CountFlattened(tree)}");
            }

            if (file.Kind == DocumentKind.Drawing)
            {
                var sheets = file.GetDrawingSheets();
                if (sheets != null && sheets.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"  [Drawing Sheets] ({sheets.Count} sheet{(sheets.Count > 1 ? "s" : "")})");
                    foreach (var s in sheets)
                    {
                        double w = s.VectorData?.PaperWidthMm ?? 0.0;
                        double h = s.VectorData?.PaperHeightMm ?? 0.0;
                        string scale = (s.VectorData != null && s.VectorData.ScaleDenominator > 0) ? $"{s.VectorData.ScaleNumerator}:{s.VectorData.ScaleDenominator}" : "1:1";
                        Console.WriteLine($"    Sheet {s.SheetIndex}: '{s.Name}' ({w:F1} × {h:F1} mm, Scale: {scale}, Views: {s.Views?.Count ?? 0})");
                    }
                }
            }

            if (props.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"  [Custom Properties] ({props.Count} items)");
                foreach (var kvp in props)
                {
                    Console.WriteLine($"    {kvp.Key,-20} : {kvp.Value}");
                }
            }

            Console.WriteLine("================================================================================");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error reading document: {ex.Message}");
            return 1;
        }
    }

    private static int CountFlattened(IReadOnlyList<AssemblyComponentNode> nodes)
    {
        if (nodes == null || nodes.Count == 0) return 0;
        int count = nodes.Count;
        foreach (var n in nodes)
        {
            if (n.Children != null && n.Children.Count > 0)
                count += CountFlattened(n.Children);
        }
        return count;
    }

    private static string FormatSolidWorksVersion(SwFile file)
    {
        int maxMoVersion = 0;
        if (file.RawStreams != null)
        {
            foreach (var key in file.RawStreams.Keys)
            {
                const string prefix = "_MO_VERSION_";
                int idx = key.IndexOf(prefix, StringComparison.Ordinal);
                if (idx < 0) continue;
                int start = idx + prefix.Length;
                int slash = key.IndexOf('/', start);
                string numStr = slash > start ? key.Substring(start, slash - start) : key.Substring(start);
                if (int.TryParse(numStr, out int ver) && ver > maxMoVersion)
                {
                    maxMoVersion = ver;
                }
            }
        }

        string? yearStr = (maxMoVersion >= 1000 && maxMoVersion % 1000 == 0)
            ? $"SolidWorks {2007 + (maxMoVersion / 1000)}"
            : null;
        string? spStr = string.IsNullOrWhiteSpace(file.Biography?.ServicePackVersion)
            ? null
            : file.Biography!.ServicePackVersion.Trim();

        if (!string.IsNullOrEmpty(yearStr) && !string.IsNullOrEmpty(spStr))
            return $"{yearStr} ({spStr})";
        if (!string.IsNullOrEmpty(yearStr))
            return yearStr!;
        if (!string.IsNullOrEmpty(spStr))
            return spStr!;
        return "SolidWorks";
    }
}
