using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SldFile.Drawing;
using SldFile.Drawing.Export;
using SldFile.Drawing.Vector;

namespace SldFile;

/// <summary>
/// Represents a SolidWorks drawing document (.slddrw).
/// Provides high-precision offline extraction of drawing sheets, 2D vector CAD geometry,
/// views, and lossless export to standard W3C SVG and AutoCAD DXF formats.
/// </summary>
public class SldDrawing : SwFile
{
    private IReadOnlyList<DrawingSheetInfo>? _cachedSheets;

    public SldDrawing(string filePath, bool readOnly = true) : base(filePath, readOnly)
    {
    }

    /// <summary>
    /// All drawing sheets identified in this drawing document.
    /// </summary>
    public IReadOnlyList<DrawingSheetInfo> Sheets => _cachedSheets ??= GetDrawingSheets();

    /// <summary>
    /// Gets the complete 2D vector CAD metadata (lines, borders, text, dimensions) for the specified drawing sheet.
    /// </summary>
    public DrawingSheetVectorData? GetSheetVectorData(int sheetIndex = 0)
    {
        var sheets = Sheets;
        if (sheets.Count == 0) return null;
        if (sheetIndex < 0 || sheetIndex >= sheets.Count)
            sheetIndex = 0;
        return sheets[sheetIndex].VectorData;
    }

    /// <summary>
    /// Exports the specified drawing sheet to a standard W3C SVG vector graphic XML string.
    /// </summary>
    public string ExportSheetSvg(int sheetIndex = 0)
    {
        var vectorData = GetSheetVectorData(sheetIndex);
        if (vectorData == null) return string.Empty;

        var sheets = Sheets;
        string sheetName = (sheetIndex >= 0 && sheetIndex < sheets.Count) ? sheets[sheetIndex].Name : $"Sheet{sheetIndex + 1}";
        return DrawingSvgWriter.Write(vectorData, sheetName);
    }

    /// <summary>
    /// Exports the specified drawing sheet to a standard W3C SVG vector graphic written to the stream.
    /// </summary>
    public void ExportSheetSvg(int sheetIndex, Stream outputStream)
    {
        if (outputStream == null) throw new ArgumentNullException(nameof(outputStream));
        var svg = ExportSheetSvg(sheetIndex);
        using var writer = new StreamWriter(outputStream, new UTF8Encoding(false), 1024, leaveOpen: true);
        writer.Write(svg);
        writer.Flush();
    }

    /// <summary>
    /// Exports the specified drawing sheet to an SVG file on disk.
    /// </summary>
    public void ExportSheetSvg(int sheetIndex, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("A valid file path is required.", nameof(filePath));
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        ExportSheetSvg(sheetIndex, fs);
    }

    /// <summary>
    /// Exports the specified drawing sheet to an industry-standard AutoCAD ASCII DXF (R12 / AC1009) string.
    /// </summary>
    public string ExportSheetDxf(int sheetIndex = 0)
    {
        var vectorData = GetSheetVectorData(sheetIndex);
        if (vectorData == null) return string.Empty;

        var sheets = Sheets;
        string sheetName = (sheetIndex >= 0 && sheetIndex < sheets.Count) ? sheets[sheetIndex].Name : $"Sheet{sheetIndex + 1}";
        return DrawingDxfWriter.Write(vectorData, sheetName);
    }

    /// <summary>
    /// Exports the specified drawing sheet to an AutoCAD ASCII DXF written to the stream.
    /// </summary>
    public void ExportSheetDxf(int sheetIndex, Stream outputStream)
    {
        if (outputStream == null) throw new ArgumentNullException(nameof(outputStream));
        var dxf = ExportSheetDxf(sheetIndex);
        using var writer = new StreamWriter(outputStream, new UTF8Encoding(false), 1024, leaveOpen: true);
        writer.Write(dxf);
        writer.Flush();
    }

    /// <summary>
    /// Exports the specified drawing sheet to an AutoCAD ASCII DXF file on disk.
    /// </summary>
    public void ExportSheetDxf(int sheetIndex, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("A valid file path is required.", nameof(filePath));
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        ExportSheetDxf(sheetIndex, fs);
    }
}
