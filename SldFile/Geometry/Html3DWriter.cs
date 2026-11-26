using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace SldFile.Geometry;

/// <summary>
/// Exports a 3D display mesh into an offline, standalone single-file HTML/WebGL (Three.js) interactive viewer.
/// The resulting HTML file embeds Three.js, OrbitControls, and all geometry/colors inline.
/// It requires zero external network connections and runs seamlessly by double-clicking in any modern web browser.
/// </summary>
public static class Html3DWriter
{
    private static string? _cachedThreeJsBundle;
    private static readonly object _bundleLock = new();

    /// <summary>
    /// Gets the inlined, decompressed Three.js r128 + OrbitControls runtime bundle.
    /// </summary>
    public static string GetThreeJsBundle()
    {
        if (_cachedThreeJsBundle != null)
        {
            return _cachedThreeJsBundle;
        }

        lock (_bundleLock)
        {
            if (_cachedThreeJsBundle != null)
            {
                return _cachedThreeJsBundle;
            }

            var asm = typeof(DisplayMesh).Assembly;
            using var stream = asm.GetManifestResourceStream("SldFile.Resources.three.min.js.gz");
            if (stream != null)
            {
                using var gz = new GZipStream(stream, CompressionMode.Decompress);
                using var reader = new StreamReader(gz, Encoding.UTF8);
                _cachedThreeJsBundle = reader.ReadToEnd();
                return _cachedThreeJsBundle;
            }

            // Fallback in development environments if embedded resource was not packaged
            string localRes = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "three.min.js.gz");
            if (File.Exists(localRes))
            {
                using var fs = File.OpenRead(localRes);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var reader = new StreamReader(gz, Encoding.UTF8);
                _cachedThreeJsBundle = reader.ReadToEnd();
                return _cachedThreeJsBundle;
            }

            return "// Three.js runtime bundle not found in resources.";
        }
    }

    /// <summary>
    /// Writes the display mesh as a standalone, offline single-file HTML viewer to the specified text writer.
    /// </summary>
    public static void Write(DisplayMesh mesh, TextWriter writer, string? modelTitle = null, Html3DExportOptions? options = null)
    {
        if (mesh == null) throw new ArgumentNullException(nameof(mesh));
        if (writer == null) throw new ArgumentNullException(nameof(writer));

        options ??= new Html3DExportOptions();
        string title = !string.IsNullOrWhiteSpace(options.Title) ? options.Title! :
                       (!string.IsNullOrWhiteSpace(modelTitle) ? modelTitle! : "SolidWorks 3D 模型交互式预览");

        // 1. 序列化全局顶点与法线数据 (Base64 Float32Array)
        int vertexCount = mesh.Positions.Count;
        float scale = options.Scale;

        var posFloats = new float[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            var p = mesh.Positions[i] * scale;
            posFloats[i * 3] = p.X;
            posFloats[i * 3 + 1] = p.Y;
            posFloats[i * 3 + 2] = p.Z;
        }
        byte[] posBytes = new byte[posFloats.Length * sizeof(float)];
        Buffer.BlockCopy(posFloats, 0, posBytes, 0, posBytes.Length);
        string posBase64 = Convert.ToBase64String(posBytes);

        var normFloats = new float[mesh.Normals.Count * 3];
        for (int i = 0; i < mesh.Normals.Count; i++)
        {
            var n = mesh.Normals[i];
            normFloats[i * 3] = n.X;
            normFloats[i * 3 + 1] = n.Y;
            normFloats[i * 3 + 2] = n.Z;
        }
        byte[] normBytes = new byte[normFloats.Length * sizeof(float)];
        Buffer.BlockCopy(normFloats, 0, normBytes, 0, normBytes.Length);
        string normBase64 = Convert.ToBase64String(normBytes);

        // 2. 序列化 CAD 轮廓特征边线 (OutlineEdges)
        string edgesBase64 = string.Empty;
        if (options.IncludeEdges && mesh.OutlineEdges != null && mesh.OutlineEdges.Count > 0)
        {
            var edgeIndices = new uint[mesh.OutlineEdges.Count * 2];
            for (int i = 0; i < mesh.OutlineEdges.Count; i++)
            {
                edgeIndices[i * 2] = (uint)mesh.OutlineEdges[i].Start;
                edgeIndices[i * 2 + 1] = (uint)mesh.OutlineEdges[i].End;
            }
            byte[] edgeBytes = new byte[edgeIndices.Length * sizeof(uint)];
            Buffer.BlockCopy(edgeIndices, 0, edgeBytes, 0, edgeBytes.Length);
            edgesBase64 = Convert.ToBase64String(edgeBytes);
        }

        // 3. 计算整体包围盒与中心
        Vector3 min = mesh.Min * scale;
        Vector3 max = mesh.Max * scale;
        if (float.IsInfinity(min.X) || float.IsInfinity(max.X))
        {
            min = new Vector3(-100, -100, -100);
            max = new Vector3(100, 100, 100);
        }
        Vector3 center = (min + max) * 0.5f;
        Vector3 size = max - min;

        // 4. 构件 (Parts) 提取与着色绑定
        var partsData = new StringBuilder();
        partsData.Append("[");

        if (mesh.Parts != null && mesh.Parts.Count > 0)
        {
            for (int pIdx = 0; pIdx < mesh.Parts.Count; pIdx++)
            {
                var p = mesh.Parts[pIdx];
                if (p.TriangleCount <= 0) continue;

                // 构件颜色提取
                uint colorVal = p.Color ?? mesh.DefaultColor ?? 0x919EAC;
                byte cr = (byte)((colorVal >> 16) & 0xFF);
                byte cg = (byte)((colorVal >> 8) & 0xFF);
                byte cb = (byte)(colorVal & 0xFF);
                string hexColor = $"#{cr:X2}{cg:X2}{cb:X2}";

                // 构件名称解析
                string rawName = !string.IsNullOrEmpty(p.AppearanceName) ? p.AppearanceName! :
                                 (p.SurfaceReferences != null && p.SurfaceReferences.Count > 0 && !string.IsNullOrEmpty(p.SurfaceReferences[0].Identity) ? p.SurfaceReferences[0].Identity : p.Id);
                string partName = CleanPartName(rawName, pIdx + 1);

                // 构件三角形索引抽取
                int tStart = p.TriangleStart * 3;
                int tTotal = p.TriangleCount * 3;
                if (tStart + tTotal > mesh.TriangleIndices.Count)
                {
                    tTotal = Math.Max(0, mesh.TriangleIndices.Count - tStart);
                }

                var pIndices = new uint[tTotal];
                for (int t = 0; t < tTotal; t++)
                {
                    pIndices[t] = (uint)mesh.TriangleIndices[tStart + t];
                }
                byte[] idxBytes = new byte[pIndices.Length * sizeof(uint)];
                Buffer.BlockCopy(pIndices, 0, idxBytes, 0, idxBytes.Length);
                string idxBase64 = Convert.ToBase64String(idxBytes);

                // 计算构件几何中心 (用于爆炸图向外发散偏移)
                Vector3 partCenter = Vector3.Zero;
                int sampleCount = 0;
                for (int t = 0; t < tTotal; t += 3)
                {
                    uint vi = pIndices[t];
                    if (vi < mesh.Positions.Count)
                    {
                        partCenter += mesh.Positions[(int)vi] * scale;
                        sampleCount++;
                    }
                }
                if (sampleCount > 0)
                {
                    partCenter /= sampleCount;
                }
                else
                {
                    partCenter = center;
                }

                if (pIdx > 0 && partsData.Length > 1) partsData.Append(",");
                partsData.Append("{");
                partsData.AppendFormat(CultureInfo.InvariantCulture, "\"id\":{0},", pIdx);
                partsData.AppendFormat(CultureInfo.InvariantCulture, "\"name\":{0},", EscapeJsonString(partName));
                partsData.AppendFormat(CultureInfo.InvariantCulture, "\"color\":\"{0}\",", hexColor);
                partsData.AppendFormat(CultureInfo.InvariantCulture, "\"triangles\":{0},", p.TriangleCount);
                partsData.AppendFormat(CultureInfo.InvariantCulture, "\"center\":[{0:F2},{1:F2},{2:F2}],", partCenter.X, partCenter.Y, partCenter.Z);
                partsData.AppendFormat(CultureInfo.InvariantCulture, "\"indices\":\"{0}\"", idxBase64);
                partsData.Append("}");
            }
        }
        else
        {
            // 单零件或无独立子部件模型
            uint colorVal = mesh.DefaultColor ?? 0x919EAC;
            byte cr = (byte)((colorVal >> 16) & 0xFF);
            byte cg = (byte)((colorVal >> 8) & 0xFF);
            byte cb = (byte)(colorVal & 0xFF);
            string hexColor = $"#{cr:X2}{cg:X2}{cb:X2}";

            var pIndices = new uint[mesh.TriangleIndices.Count];
            for (int t = 0; t < mesh.TriangleIndices.Count; t++)
            {
                pIndices[t] = (uint)mesh.TriangleIndices[t];
            }
            byte[] idxBytes = new byte[pIndices.Length * sizeof(uint)];
            Buffer.BlockCopy(pIndices, 0, idxBytes, 0, idxBytes.Length);
            string idxBase64 = Convert.ToBase64String(idxBytes);

            partsData.Append("{");
            partsData.Append("\"id\":0,");
            partsData.AppendFormat(CultureInfo.InvariantCulture, "\"name\":{0},", EscapeJsonString(title));
            partsData.AppendFormat(CultureInfo.InvariantCulture, "\"color\":\"{0}\",", hexColor);
            partsData.AppendFormat(CultureInfo.InvariantCulture, "\"triangles\":{0},", mesh.TriangleCount);
            partsData.AppendFormat(CultureInfo.InvariantCulture, "\"center\":[{0:F2},{1:F2},{2:F2}],", center.X, center.Y, center.Z);
            partsData.AppendFormat(CultureInfo.InvariantCulture, "\"indices\":\"{0}\"", idxBase64);
            partsData.Append("}");
        }
        partsData.Append("]");

        // 5. 组装内联单文件 HTML
        string threeJs = GetThreeJsBundle();

        writer.WriteLine("<!DOCTYPE html>");
        writer.WriteLine("<html lang=\"zh-CN\">");
        writer.WriteLine("<head>");
        writer.WriteLine("  <meta charset=\"UTF-8\">");
        writer.WriteLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0, user-scalable=no\">");
        writer.WriteLine($"  <title>{EscapeHtml(title)} - SldFile 3D Viewer</title>");
        writer.WriteLine("  <style>");
        writer.WriteLine(GetEmbeddedCss(options.Theme));
        writer.WriteLine("  </style>");
        writer.WriteLine("</head>");
        writer.WriteLine("<body>");

        // UI 骨架
        writer.WriteLine($@"
  <div id=""app"">
    <!-- 顶部导航栏 -->
    <header class=""navbar"">
      <div class=""nav-left"">
        <span class=""logo-icon"">📐</span>
        <span class=""model-title"" title=""{EscapeHtml(title)}"">{EscapeHtml(title)}</span>
        <div class=""stats-pill"">
          <span>顶点: {vertexCount:N0}</span>
          <span class=""divider"">|</span>
          <span>面数: {mesh.TriangleCount:N0}</span>
          <span class=""divider"">|</span>
          <span>包围盒: {size.X:F1} × {size.Y:F1} × {size.Z:F1} mm</span>
        </div>
      </div>
      <div class=""nav-right"">
        <div class=""btn-group"">
          <button id=""btnShaded"" class=""btn active"" title=""真实材质与高光着色"">🎨 着色</button>
          <button id=""btnWhite"" class=""btn"" title=""工程白模纸感渲染"">📄 白模</button>
          <button id=""btnWireframe"" class=""btn"" title=""网格线框显示"">🕸️ 线框</button>
        </div>
        <button id=""btnEdges"" class=""btn active"" title=""切换 CAD 黑色特征边缘线"">📐 边线</button>
        <button id=""btnDistinctColors"" class=""btn"" title=""切换按零件区分着色 / 原生材质色"">🌈 区分色</button>
        <button id=""btnMeasure"" class=""btn"" title=""三维尺寸测量工具 (点击模型表面两点测距)"">📏 测量</button>
        <button id=""btnScreenshot"" class=""btn"" title=""导出高清当前视角截图 (PNG)"">📸 截图</button>
        <button id=""btnFullscreen"" class=""btn"" title=""全屏查看"">⛶</button>
      </div>
    </header>

    <!-- 主视口容器 -->
    <div id=""viewport"">
      <canvas id=""webglCanvas""></canvas>

      <!-- 悬停构件信息提示 -->
      <div id=""partTooltip"" class=""tooltip""></div>

      <!-- 3D 尺寸测量控制面板 -->
      <div id=""measurePanel"" class=""measure-panel hidden"">
        <div class=""measure-header"">
          <span class=""measure-title"">📏 三维尺寸测量</span>
          <button id=""btnCloseMeasure"" class=""icon-btn"" title=""关闭测量模式"">✕</button>
        </div>
        <div class=""measure-body"">
          <div id=""measureTip"" class=""measure-tip"">点击模型表面捕捉第 1 个测量点</div>
          <div id=""measureStats"" class=""measure-stats"">
            <div class=""measure-main-val"">
              <span class=""measure-val-lbl"">空间距离 (L):</span>
              <span id=""valDistance"" class=""measure-dist-num"">0.00 mm</span>
            </div>
            <div class=""measure-components"">
              <div class=""measure-pill dx""><span class=""pill-label"">ΔX</span><span id=""valDx"">0.00 mm</span></div>
              <div class=""measure-pill dy""><span class=""pill-label"">ΔY</span><span id=""valDy"">0.00 mm</span></div>
              <div class=""measure-pill dz""><span class=""pill-label"">ΔZ</span><span id=""valDz"">0.00 mm</span></div>
            </div>
          </div>
          <div class=""measure-actions"">
            <button id=""btnResetMeasure"" class=""tool-btn"" title=""清空当前测量线并重新测距 (Esc)"">清空重测 (Esc)</button>
          </div>
        </div>
      </div>

      <!-- 视口跟随 3D 坐标投影动态标牌 Badge -->
      <div id=""measureBadge"" class=""measure-badge hidden"">
        <span id=""badgeDistance"">0.00 mm</span>
      </div>

      <!-- 左侧零部件树面板 (可折叠) -->
      <aside id=""partSidebar"" class=""sidebar open"">
        <div class=""sidebar-header"">
          <span class=""sidebar-title"">零部件清单</span>
          <div class=""sidebar-actions"">
            <button id=""btnShowAll"" class=""action-link"">全显</button>
            <button id=""btnResetIsolate"" class=""action-link"">重置</button>
            <button id=""btnToggleSidebar"" class=""icon-btn"" title=""收起面板"">◀</button>
          </div>
        </div>
        <div class=""search-box"">
          <input type=""text"" id=""partSearch"" placeholder=""搜索构件名称..."" />
        </div>
        <div id=""partList"" class=""part-list""></div>
      </aside>
      <button id=""btnOpenSidebar"" class=""sidebar-toggle-btn"" title=""展开零部件清单"">▶</button>

      <!-- 底部悬浮控制栏 (视角预设、爆炸图、剖切) -->
      <footer class=""bottom-toolbar"">
        <!-- 视角预设 -->
        <div class=""toolbar-group"">
          <button class=""tool-btn"" onclick=""setView('iso')"" title=""等轴测视角"">等轴测</button>
          <button class=""tool-btn"" onclick=""setView('front')"" title=""前视"">前</button>
          <button class=""tool-btn"" onclick=""setView('back')"" title=""后视"">后</button>
          <button class=""tool-btn"" onclick=""setView('top')"" title=""俯视"">俯</button>
          <button class=""tool-btn"" onclick=""setView('bottom')"" title=""仰视"">仰</button>
          <button class=""tool-btn"" onclick=""setView('left')"" title=""左视"">左</button>
          <button class=""tool-btn"" onclick=""setView('right')"" title=""右视"">右</button>
          <button class=""tool-btn highlight"" onclick=""fitCamera()"" title=""自适应居中视角"">居中聚焦</button>
        </div>

        <div class=""toolbar-divider""></div>

        <!-- 爆炸图滑块 -->
        <div class=""toolbar-control"">
          <span class=""ctrl-label"">💥 爆炸分解:</span>
          <input type=""range"" id=""explodeSlider"" min=""0"" max=""100"" value=""0"" />
          <span id=""explodeValue"" class=""ctrl-val"">0%</span>
        </div>

        <div class=""toolbar-divider""></div>

        <!-- 剖切分析 -->
        <div class=""toolbar-control"">
          <button id=""btnSectionToggle"" class=""tool-btn"">✂️ 剖切</button>
          <div id=""sectionControls"" class=""section-subcontrols hidden"">
            <select id=""sectionAxis"">
              <option value=""x"">X 轴</option>
              <option value=""y"">Y 轴</option>
              <option value=""z"" selected>Z 轴</option>
            </select>
            <input type=""range"" id=""sectionSlider"" min=""0"" max=""100"" value=""50"" />
            <button id=""btnSectionFlip"" class=""tool-btn-sm"" title=""翻转剖切方向"">翻转</button>
          </div>
        </div>
      </footer>
    </div>
  </div>
");

        // 嵌入运行时 JS 引擎与模型数据
        writer.WriteLine("  <script>");
        writer.WriteLine("    /* --- Three.js & OrbitControls Inlined Runtime --- */");
        writer.WriteLine(threeJs);
        writer.WriteLine("  </script>");

        writer.WriteLine("  <script>");
        writer.WriteLine("    /* --- Inlined CAD Model Geometry & Data --- */");
        writer.WriteLine($"    const MODEL_TITLE = {EscapeJsonString(title)};");
        writer.WriteLine($"    const MODEL_BOUNDS = {{ min: [{min.X:F3}, {min.Y:F3}, {min.Z:F3}], max: [{max.X:F3}, {max.Y:F3}, {max.Z:F3}], center: [{center.X:F3}, {center.Y:F3}, {center.Z:F3}], size: [{size.X:F3}, {size.Y:F3}, {size.Z:F3}] }};");
        writer.WriteLine($"    const RAW_POSITIONS_B64 = \"{posBase64}\";");
        writer.WriteLine($"    const RAW_NORMALS_B64 = \"{normBase64}\";");
        writer.WriteLine($"    const RAW_EDGES_B64 = \"{edgesBase64}\";");
        writer.WriteLine($"    const MODEL_PARTS = {partsData};");
        writer.WriteLine("  </script>");

        // 嵌入交互式查看器控制脚本
        writer.WriteLine("  <script>");
        writer.WriteLine(GetEmbeddedViewerJs());
        writer.WriteLine("  </script>");

        writer.WriteLine("</body>");
        writer.WriteLine("</html>");
    }

    /// <summary>
    /// Writes the display mesh as an offline single-file HTML viewer to the specified stream.
    /// </summary>
    public static void Write(DisplayMesh mesh, Stream stream, string? modelTitle = null, Html3DExportOptions? options = null)
    {
        using var writer = new StreamWriter(stream, Encoding.UTF8, 4096, leaveOpen: true);
        Write(mesh, writer, modelTitle, options);
    }

    /// <summary>
    /// Exports the display mesh as an offline single-file HTML viewer to a file on disk.
    /// </summary>
    public static void Export(DisplayMesh mesh, string filePath, string? modelTitle = null, Html3DExportOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path must not be null or empty.", nameof(filePath));
        string dir = Path.GetDirectoryName(filePath)!;
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fs = File.Create(filePath);
        Write(mesh, fs, modelTitle ?? Path.GetFileNameWithoutExtension(filePath), options);
    }

    /// <summary>
    /// Exports the 3D model of a SolidWorks file as an offline single-file HTML viewer to a file on disk.
    /// </summary>
    public static void Export(SwFile file, string filePath, string? modelTitle = null, Html3DExportOptions? options = null)
    {
        if (file == null) throw new ArgumentNullException(nameof(file));
        var mesh = file.GetDisplayMesh();
        if (mesh == null || mesh.VertexCount == 0)
        {
            throw new InvalidOperationException($"Document '{file.FilePath}' contains no 3D display mesh geometry to export.");
        }
        Export(mesh, filePath, modelTitle ?? Path.GetFileName(file.FilePath), options);
    }

    private static string CleanPartName(string? id, int index)
    {
        if (string.IsNullOrWhiteSpace(id)) return $"构件_{index}";
        string name = id!;
        if (name.StartsWith("stitched:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = name.Split(':');
            if (parts.Length >= 3 && !int.TryParse(parts[1], out _))
            {
                name = parts[1];
            }
            else
            {
                name = $"构件_{index}";
            }
        }
        int caret = name.IndexOf('^');
        if (caret > 0) name = name.Substring(0, caret);
        int angleBracket = name.IndexOf('<');
        if (angleBracket > 0) name = name.Substring(0, angleBracket);
        name = name.Replace("____", " ").Trim();
        return string.IsNullOrWhiteSpace(name) ? $"构件_{index}" : name;
    }

    private static string EscapeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        var sb = new StringBuilder();
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string EscapeHtml(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    private static string GetEmbeddedCss(string theme)
    {
        bool isDark = !string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);
        string bg = isDark ? "#121519" : "#F4F6F9";
        string cardBg = isDark ? "rgba(24, 28, 34, 0.85)" : "rgba(255, 255, 255, 0.9)";
        string cardBorder = isDark ? "rgba(255, 255, 255, 0.08)" : "rgba(0, 0, 0, 0.1)";
        string text = isDark ? "#E2E8F0" : "#1A202C";
        string textMuted = isDark ? "#94A3B8" : "#64748B";
        string activeBtn = isDark ? "#3B82F6" : "#2563EB";

        return $@"
* {{ box-sizing: border-box; margin: 0; padding: 0; user-select: none; -webkit-user-select: none; }}
html, body, #app {{ width: 100%; height: 100%; overflow: hidden; background: {bg}; font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, ""PingFang SC"", ""Microsoft YaHei"", sans-serif; color: {text}; }}
#app {{ display: flex; flex-direction: column; }}

/* 顶部导航栏 */
.navbar {{ height: 50px; background: {cardBg}; backdrop-filter: blur(12px); -webkit-backdrop-filter: blur(12px); border-bottom: 1px solid {cardBorder}; display: flex; align-items: center; justify-content: space-between; padding: 0 16px; z-index: 100; }}
.nav-left {{ display: flex; align-items: center; gap: 12px; }}
.logo-icon {{ font-size: 20px; }}
.model-title {{ font-size: 15px; font-weight: 600; max-width: 380px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }}
.stats-pill {{ display: flex; align-items: center; gap: 8px; font-size: 11px; color: {textMuted}; background: rgba(0, 0, 0, 0.2); padding: 4px 10px; border-radius: 20px; border: 1px solid {cardBorder}; }}
.divider {{ opacity: 0.3; }}
.nav-right {{ display: flex; align-items: center; gap: 10px; }}

/* 视口容器 */
#viewport {{ flex: 1; position: relative; width: 100%; height: calc(100% - 50px); }}
#webglCanvas {{ width: 100%; height: 100%; display: block; outline: none; }}

/* 按钮通用 */
.btn {{ background: rgba(255, 255, 255, 0.06); border: 1px solid {cardBorder}; color: {text}; padding: 6px 12px; font-size: 12px; font-weight: 500; border-radius: 6px; cursor: pointer; transition: all 0.15s ease; }}
.btn:hover {{ background: rgba(255, 255, 255, 0.14); border-color: rgba(255, 255, 255, 0.2); }}
.btn.active {{ background: {activeBtn}; color: #FFF; border-color: {activeBtn}; }}
.btn-group {{ display: flex; gap: 2px; background: rgba(0,0,0,0.25); padding: 2px; border-radius: 8px; }}
.btn-group .btn {{ border-radius: 6px; }}

/* 左侧构件树面板 */
.sidebar {{ position: absolute; top: 12px; left: 12px; bottom: 74px; width: 280px; background: {cardBg}; backdrop-filter: blur(16px); -webkit-backdrop-filter: blur(16px); border: 1px solid {cardBorder}; border-radius: 10px; display: flex; flex-direction: column; z-index: 50; transition: transform 0.25s ease, opacity 0.25s ease; box-shadow: 0 10px 30px rgba(0,0,0,0.3); }}
.sidebar:not(.open) {{ transform: translateX(-310px); opacity: 0; pointer-events: none; }}
.sidebar-header {{ display: flex; align-items: center; justify-content: space-between; padding: 12px 14px; border-bottom: 1px solid {cardBorder}; }}
.sidebar-title {{ font-size: 13px; font-weight: 600; }}
.sidebar-actions {{ display: flex; align-items: center; gap: 8px; }}
.action-link {{ background: none; border: none; color: #3B82F6; font-size: 11px; cursor: pointer; }}
.action-link:hover {{ text-decoration: underline; }}
.icon-btn {{ background: none; border: none; color: {textMuted}; cursor: pointer; font-size: 12px; padding: 2px 6px; border-radius: 4px; }}
.icon-btn:hover {{ background: rgba(255,255,255,0.1); color: {text}; }}
.search-box {{ padding: 8px 12px; border-bottom: 1px solid {cardBorder}; }}
.search-box input {{ width: 100%; background: rgba(0,0,0,0.2); border: 1px solid {cardBorder}; border-radius: 6px; padding: 6px 10px; font-size: 12px; color: {text}; outline: none; }}
.search-box input:focus {{ border-color: #3B82F6; }}
.part-list {{ flex: 1; overflow-y: auto; padding: 6px; }}
.part-item {{ display: flex; align-items: center; justify-content: space-between; padding: 6px 10px; border-radius: 6px; font-size: 12px; cursor: pointer; margin-bottom: 2px; transition: background 0.15s ease; }}
.part-item:hover {{ background: rgba(255,255,255,0.06); }}
.part-item.hovered {{ background: rgba(56, 189, 248, 0.15) !important; border-left: 3px solid #38BDF8; }}
.part-item.selected {{ background: rgba(59, 130, 246, 0.25) !important; border: 1px solid #3B82F6; }}
.part-item.isolated {{ background: rgba(59, 130, 246, 0.2); border: 1px solid #3B82F6; }}
.part-left {{ display: flex; align-items: center; gap: 8px; overflow: hidden; }}
.part-swatch {{ width: 12px; height: 12px; border-radius: 3px; flex-shrink: 0; border: 1px solid rgba(255,255,255,0.2); }}
.part-name {{ white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }}
.part-badge {{ font-size: 10px; color: {textMuted}; background: rgba(0,0,0,0.2); padding: 2px 6px; border-radius: 10px; }}
.sidebar-toggle-btn {{ position: absolute; top: 16px; left: 12px; z-index: 40; background: {cardBg}; border: 1px solid {cardBorder}; color: {text}; padding: 8px 10px; border-radius: 8px; cursor: pointer; backdrop-filter: blur(12px); box-shadow: 0 4px 12px rgba(0,0,0,0.2); }}

/* 底部悬浮控制栏 */
.bottom-toolbar {{ position: absolute; bottom: 16px; left: 50%; transform: translateX(-50%); background: {cardBg}; backdrop-filter: blur(16px); -webkit-backdrop-filter: blur(16px); border: 1px solid {cardBorder}; border-radius: 10px; display: flex; align-items: center; gap: 10px; padding: 8px 16px; z-index: 50; box-shadow: 0 10px 30px rgba(0,0,0,0.3); max-width: 95%; }}
.toolbar-group {{ display: flex; gap: 4px; }}
.tool-btn {{ background: rgba(255,255,255,0.06); border: 1px solid {cardBorder}; color: {text}; padding: 6px 10px; font-size: 11px; border-radius: 6px; cursor: pointer; }}
.tool-btn:hover {{ background: rgba(255,255,255,0.14); }}
.tool-btn.highlight {{ background: rgba(59, 130, 246, 0.2); border-color: #3B82F6; color: #60A5FA; font-weight: 600; }}
.tool-btn-sm {{ background: rgba(255,255,255,0.08); border: 1px solid {cardBorder}; color: {text}; padding: 4px 8px; font-size: 10px; border-radius: 4px; cursor: pointer; }}
.toolbar-divider {{ width: 1px; height: 20px; background: {cardBorder}; }}
.toolbar-control {{ display: flex; align-items: center; gap: 8px; font-size: 12px; }}
.ctrl-label {{ color: {textMuted}; white-space: nowrap; }}
.ctrl-val {{ font-variant-numeric: tabular-nums; min-width: 32px; font-size: 11px; color: #60A5FA; }}
input[type=range] {{ -webkit-appearance: none; appearance: none; width: 100px; height: 4px; background: rgba(255,255,255,0.2); border-radius: 2px; outline: none; }}
input[type=range]::-webkit-slider-thumb {{ -webkit-appearance: none; appearance: none; width: 14px; height: 14px; border-radius: 50%; background: #3B82F6; cursor: pointer; }}
.section-subcontrols {{ display: flex; align-items: center; gap: 6px; }}
.section-subcontrols.hidden {{ display: none; }}
.section-subcontrols select {{ background: rgba(0,0,0,0.3); border: 1px solid {cardBorder}; color: {text}; font-size: 11px; padding: 3px 6px; border-radius: 4px; outline: none; }}

/* 悬停信息浮层 */
.tooltip {{ position: absolute; background: rgba(15, 23, 42, 0.95); border: 1px solid rgba(255,255,255,0.15); border-radius: 6px; padding: 8px 12px; font-size: 11px; pointer-events: none; z-index: 200; display: none; transform: translate(12px, 12px); box-shadow: 0 6px 20px rgba(0,0,0,0.4); line-height: 1.5; }}
.tooltip .title {{ font-weight: 600; color: #60A5FA; margin-bottom: 2px; }}

/* 3D 尺寸测量浮动控制面板 */
.measure-panel {{ position: absolute; top: 16px; right: 16px; width: 260px; background: {cardBg}; backdrop-filter: blur(16px); -webkit-backdrop-filter: blur(16px); border: 1px solid {cardBorder}; border-radius: 10px; z-index: 60; box-shadow: 0 10px 30px rgba(0,0,0,0.35); transition: opacity 0.2s ease, transform 0.2s ease; }}
.measure-panel.hidden {{ opacity: 0; pointer-events: none; transform: translateY(-8px); }}
.measure-header {{ display: flex; align-items: center; justify-content: space-between; padding: 10px 14px; border-bottom: 1px solid {cardBorder}; }}
.measure-title {{ font-size: 13px; font-weight: 600; color: #38BDF8; }}
.measure-body {{ padding: 12px 14px; display: flex; flex-direction: column; gap: 10px; }}
.measure-tip {{ font-size: 11px; color: {textMuted}; background: rgba(0,0,0,0.2); padding: 6px 10px; border-radius: 6px; border-left: 3px solid #38BDF8; line-height: 1.4; }}
.measure-stats {{ display: flex; flex-direction: column; gap: 8px; }}
.measure-main-val {{ display: flex; align-items: baseline; justify-content: space-between; background: rgba(56, 189, 248, 0.1); border: 1px solid rgba(56, 189, 248, 0.3); border-radius: 8px; padding: 8px 12px; }}
.measure-val-lbl {{ font-size: 11px; color: {textMuted}; }}
.measure-dist-num {{ font-size: 18px; font-weight: 700; color: #38BDF8; font-variant-numeric: tabular-nums; }}
.measure-components {{ display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 6px; }}
.measure-pill {{ display: flex; flex-direction: column; align-items: center; padding: 4px 6px; border-radius: 6px; font-size: 10px; font-variant-numeric: tabular-nums; background: rgba(0,0,0,0.25); border: 1px solid {cardBorder}; }}
.measure-pill .pill-label {{ font-size: 9px; font-weight: 700; margin-bottom: 2px; }}
.measure-pill.dx .pill-label {{ color: #EF4444; }}
.measure-pill.dy .pill-label {{ color: #10B981; }}
.measure-pill.dz .pill-label {{ color: #3B82F6; }}
.measure-actions {{ display: flex; justify-content: flex-end; }}

/* 视口跟随 3D 标牌 Badge */
.measure-badge {{ position: absolute; pointer-events: none; z-index: 55; background: rgba(15, 23, 42, 0.92); border: 1px solid #38BDF8; color: #38BDF8; font-size: 11px; font-weight: 600; font-variant-numeric: tabular-nums; padding: 3px 8px; border-radius: 12px; transform: translate(-50%, -100%); white-space: nowrap; box-shadow: 0 4px 12px rgba(0,0,0,0.4); margin-top: -8px; }}
.measure-badge.hidden {{ display: none; }}

/* 移动端与触控手势优化 */
#webglCanvas {{ touch-action: none; }}
";
    }

    private static string GetEmbeddedViewerJs()
    {
        return @"
(function () {
  'use strict';

  // --- 1. Base64 二进制解码工具函数 ---
  function b64ToF32(b64) {
    if (!b64) return new Float32Array(0);
    const bin = atob(b64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return new Float32Array(bytes.buffer);
  }

  function b64ToU32(b64) {
    if (!b64) return new Uint32Array(0);
    const bin = atob(b64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return new Uint32Array(bytes.buffer);
  }

  // --- 2. 初始化 Three.js 场景与渲染器 ---
  const container = document.getElementById('viewport');
  const canvas = document.getElementById('webglCanvas');

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0x121519);

  const camera = new THREE.PerspectiveCamera(45, container.clientWidth / container.clientHeight, 0.1, 10000);
  const renderer = new THREE.WebGLRenderer({ canvas: canvas, antialias: true, alpha: true, preserveDrawingBuffer: true });
  renderer.setPixelRatio(window.devicePixelRatio || 1);
  renderer.setSize(container.clientWidth, container.clientHeight);
  renderer.localClippingEnabled = true;

  const controls = new THREE.OrbitControls(camera, renderer.domElement);
  controls.enableDamping = true;
  controls.dampingFactor = 0.05;

  // --- 3. 真实 CAD 照明系统 (Key, Fill, Rim, Ambient) ---
  const hemiLight = new THREE.HemisphereLight(0xffffff, 0x334155, 0.7);
  scene.add(hemiLight);

  const keyLight = new THREE.DirectionalLight(0xffffff, 0.75);
  keyLight.position.set(1, 2, 1.5).normalize();
  scene.add(keyLight);

  const fillLight = new THREE.DirectionalLight(0xffffff, 0.4);
  fillLight.position.set(-1, -1, -1).normalize();
  scene.add(fillLight);

  const rimLight = new THREE.DirectionalLight(0x93c5fd, 0.3);
  rimLight.position.set(0, -1, 1).normalize();
  scene.add(rimLight);

  // --- 4. 几何重组与多材质装配体生成 ---
  const globalPositions = b64ToF32(RAW_POSITIONS_B64);
  const globalNormals = b64ToF32(RAW_NORMALS_B64);
  const posAttr = new THREE.BufferAttribute(globalPositions, 3);
  const normAttr = new THREE.BufferAttribute(globalNormals, 3);

  const rootGroup = new THREE.Group();
  scene.add(rootGroup);

  const modelCenter = new THREE.Vector3(...MODEL_BOUNDS.center);
  const modelSize = new THREE.Vector3(...MODEL_BOUNDS.size);
  const maxDim = Math.max(modelSize.x, modelSize.y, modelSize.z) || 100;

  // 辅助网格地平面
  const gridHelper = new THREE.GridHelper(maxDim * 2.5, 20, 0x334155, 0x1e293b);
  gridHelper.position.y = MODEL_BOUNDS.min[1];
  scene.add(gridHelper);

  const partRecords = [];

  MODEL_PARTS.forEach((p, idx) => {
    const geom = new THREE.BufferGeometry();
    geom.setAttribute('position', posAttr);
    geom.setAttribute('normal', normAttr);
    const indices = b64ToU32(p.indices);
    geom.setIndex(new THREE.BufferAttribute(indices, 1));

    const color = new THREE.Color(p.color || '#919EAC');
    const DISTINCT_PALETTE = [
      '#E5C94F', '#38BDF8', '#E11D48', '#10B981', '#8B5CF6', '#F59E0B', '#14B8A6',
      '#6366F1', '#64748B', '#F43F5E', '#06B6D4', '#A855F7', '#84CC16', '#EC4899'
    ];
    let nameHash = 0;
    const keyStr = p.name || p.id || '';
    for (let hi = 0; hi < keyStr.length; hi++) {
      nameHash = ((nameHash << 5) - nameHash + keyStr.charCodeAt(hi)) | 0;
    }
    const distinctHex = DISTINCT_PALETTE[Math.abs(nameHash) % DISTINCT_PALETTE.length];
    const distinctColor = new THREE.Color(distinctHex);

    const mat = new THREE.MeshStandardMaterial({
      color: color,
      metalness: 0.25,
      roughness: 0.45,
      side: THREE.DoubleSide
    });

    const mesh = new THREE.Mesh(geom, mat);
    mesh.userData = { id: p.id, name: p.name, colorHex: p.color, triangles: p.triangles };
    rootGroup.add(mesh);

    // 爆炸图发散向量计算
    const pCenter = new THREE.Vector3(...p.center);
    let explodeDir = pCenter.clone().sub(modelCenter);
    if (explodeDir.lengthSq() < 1e-4) {
      explodeDir = new THREE.Vector3(0, 1, 0);
    } else {
      explodeDir.normalize();
    }

    partRecords.push({
      id: p.id,
      name: p.name,
      colorHex: p.color,
      distinctHex: distinctHex,
      triangles: p.triangles,
      mesh: mesh,
      origColor: color,
      distinctColor: distinctColor,
      initialPos: mesh.position.clone(),
      explodeDir: explodeDir
    });
  });

  // --- 5. CAD 轮廓特征边线 (OutlineEdges) ---
  let edgesMesh = null;
  if (RAW_EDGES_B64 && RAW_EDGES_B64.length > 0) {
    const edgeIndices = b64ToU32(RAW_EDGES_B64);
    const edgeGeom = new THREE.BufferGeometry();
    edgeGeom.setAttribute('position', posAttr);
    edgeGeom.setIndex(new THREE.BufferAttribute(edgeIndices, 1));
    const edgeMat = new THREE.LineBasicMaterial({ color: 0x1a202c, linewidth: 1 });
    edgesMesh = new THREE.LineSegments(edgeGeom, edgeMat);
    rootGroup.add(edgesMesh);
  }

  // --- 6. 相机位置设置与居中自适应 ---
  function fitCamera() {
    controls.target.copy(modelCenter);
    camera.position.set(
      modelCenter.x + maxDim * 1.2,
      modelCenter.y + maxDim * 0.9,
      modelCenter.z + maxDim * 1.3
    );
    camera.near = maxDim * 0.01;
    camera.far = maxDim * 20;
    camera.updateProjectionMatrix();
    controls.update();
  }
  fitCamera();

  // --- 7. 标准视角切换 ---
  window.setView = function (viewName) {
    controls.target.copy(modelCenter);
    const dist = maxDim * 1.8;
    switch (viewName) {
      case 'front':  camera.position.set(modelCenter.x, modelCenter.y, modelCenter.z + dist); break;
      case 'back':   camera.position.set(modelCenter.x, modelCenter.y, modelCenter.z - dist); break;
      case 'top':    camera.position.set(modelCenter.x, modelCenter.y + dist, modelCenter.z); break;
      case 'bottom': camera.position.set(modelCenter.x, modelCenter.y - dist, modelCenter.z); break;
      case 'left':   camera.position.set(modelCenter.x - dist, modelCenter.y, modelCenter.z); break;
      case 'right':  camera.position.set(modelCenter.x + dist, modelCenter.y, modelCenter.z); break;
      case 'iso':
      default:
        camera.position.set(modelCenter.x + dist * 0.7, modelCenter.y + dist * 0.6, modelCenter.z + dist * 0.7);
        break;
    }
    camera.lookAt(modelCenter);
    controls.update();
  };

  // --- 8. 显示样式切换 (着色 / 白模 / 线框 / 零件区分色) ---
  let currentStyle = 'shaded';
  let useDistinctColors = false;
  function updateDisplayStyle(style) {
    currentStyle = style;
    document.getElementById('btnShaded').classList.toggle('active', style === 'shaded');
    document.getElementById('btnWhite').classList.toggle('active', style === 'white');
    document.getElementById('btnWireframe').classList.toggle('active', style === 'wireframe');

    partRecords.forEach(p => {
      const mat = p.mesh.material;
      const targetColor = useDistinctColors ? p.distinctColor : p.origColor;
      if (style === 'wireframe') {
        mat.wireframe = true;
        mat.color.copy(targetColor);
      } else if (style === 'white') {
        mat.wireframe = false;
        mat.color.set(0xf5f7fa);
      } else {
        mat.wireframe = false;
        mat.color.copy(targetColor);
      }
      mat.needsUpdate = true;

      const swatch = document.querySelector(`#part-row-${CSS.escape(p.id)} .part-swatch`);
      if (swatch) {
        swatch.style.background = useDistinctColors ? p.distinctHex : p.colorHex;
      }
    });
  }

  document.getElementById('btnShaded').addEventListener('click', () => updateDisplayStyle('shaded'));
  document.getElementById('btnWhite').addEventListener('click', () => updateDisplayStyle('white'));
  document.getElementById('btnWireframe').addEventListener('click', () => updateDisplayStyle('wireframe'));

  const btnDistinctColors = document.getElementById('btnDistinctColors');
  btnDistinctColors.addEventListener('click', () => {
    useDistinctColors = !useDistinctColors;
    btnDistinctColors.classList.toggle('active', useDistinctColors);
    updateDisplayStyle(currentStyle);
  });

  // 边线开关
  const btnEdges = document.getElementById('btnEdges');
  btnEdges.addEventListener('click', () => {
    if (edgesMesh) {
      edgesMesh.visible = !edgesMesh.visible;
      btnEdges.classList.toggle('active', edgesMesh.visible);
    }
  });

  // --- 9. 装配体爆炸图滑块联动 ---
  const explodeSlider = document.getElementById('explodeSlider');
  const explodeValue = document.getElementById('explodeValue');

  explodeSlider.addEventListener('input', (e) => {
    const val = parseFloat(e.target.value);
    explodeValue.textContent = Math.round(val) + '%';
    const dist = (val / 100.0) * maxDim * 0.8;

    partRecords.forEach(p => {
      p.mesh.position.copy(p.initialPos).addScaledVector(p.explodeDir, dist);
    });
    if (edgesMesh) {
      // 爆炸时隐藏边线或随构件移动，此处简单处理保持视口干净
      edgesMesh.visible = val === 0 && btnEdges.classList.contains('active');
    }
  });

  // --- 10. 三向交互式剖切工具 ---
  let sectionActive = false;
  let sectionPlane = new THREE.Plane(new THREE.Vector3(0, 0, -1), 0);
  let sectionFlipped = false;

  const btnSectionToggle = document.getElementById('btnSectionToggle');
  const sectionControls = document.getElementById('sectionControls');
  const sectionAxis = document.getElementById('sectionAxis');
  const sectionSlider = document.getElementById('sectionSlider');
  const btnSectionFlip = document.getElementById('btnSectionFlip');

  function updateSectionPlane() {
    if (!sectionActive) {
      renderer.clippingPlanes = [];
      return;
    }
    const axis = sectionAxis.value;
    let normal = new THREE.Vector3();
    let minVal = 0, maxVal = 0;

    if (axis === 'x') { normal.set(1, 0, 0); minVal = MODEL_BOUNDS.min[0]; maxVal = MODEL_BOUNDS.max[0]; }
    else if (axis === 'y') { normal.set(0, 1, 0); minVal = MODEL_BOUNDS.min[1]; maxVal = MODEL_BOUNDS.max[1]; }
    else { normal.set(0, 0, 1); minVal = MODEL_BOUNDS.min[2]; maxVal = MODEL_BOUNDS.max[2]; }

    if (sectionFlipped) normal.negate();

    const t = parseFloat(sectionSlider.value) / 100.0;
    const cutPos = minVal + t * (maxVal - minVal);
    const constant = sectionFlipped ? cutPos : -cutPos;

    sectionPlane.set(normal, constant);
    renderer.clippingPlanes = [sectionPlane];
  }

  btnSectionToggle.addEventListener('click', () => {
    sectionActive = !sectionActive;
    btnSectionToggle.classList.toggle('highlight', sectionActive);
    sectionControls.classList.toggle('hidden', !sectionActive);
    updateSectionPlane();
  });

  sectionAxis.addEventListener('change', updateSectionPlane);
  sectionSlider.addEventListener('input', updateSectionPlane);
  btnSectionFlip.addEventListener('click', () => {
    sectionFlipped = !sectionFlipped;
    updateSectionPlane();
  });

  // --- 11. 零部件树与隔离模式 (Isolate / Visibility / 双向联动) ---
  const partListEl = document.getElementById('partList');
  let currentIsolatedId = null;

  function renderPartList(filterText = '') {
    partListEl.innerHTML = '';
    const q = (filterText || '').toLowerCase().trim();

    partRecords.forEach(p => {
      if (q && !p.name.toLowerCase().includes(q)) return;

      const row = document.createElement('div');
      row.id = 'part-row-' + p.id;
      row.className = 'part-item' + (currentIsolatedId === p.id ? ' isolated' : '');

      row.innerHTML = `
        <div class=""part-left"">
          <div class=""part-swatch"" style=""background: ${useDistinctColors ? p.distinctHex : p.colorHex}""></div>
          <span class=""part-name"" title=""${p.name}"">${p.name}</span>
        </div>
        <div class=""part-badge"">${p.triangles} 面</div>
      `;

      // 悬停树节点：高亮对应 3D 构件 (天蓝色自发光)
      row.addEventListener('mouseenter', () => {
        if (p.mesh && p.mesh.material) {
          p.mesh.material.emissive.setHex(0x38bdf8);
          p.mesh.material.emissiveIntensity = 0.45;
        }
        row.classList.add('hovered');
      });
      row.addEventListener('mouseleave', () => {
        if (p.mesh && p.mesh.material) {
          p.mesh.material.emissive.setHex(0x000000);
          p.mesh.material.emissiveIntensity = 0.0;
        }
        row.classList.remove('hovered');
      });

      // 点击树节点：隔离 / 恢复构件
      row.addEventListener('click', (ev) => {
        document.querySelectorAll('.part-item').forEach(el => el.classList.remove('selected'));
        row.classList.add('selected');

        if (currentIsolatedId === p.id) {
          // 解除隔离
          currentIsolatedId = null;
          partRecords.forEach(item => {
            item.mesh.visible = true;
            item.mesh.material.opacity = 1.0;
            item.mesh.material.transparent = false;
          });
        } else {
          // 隔离该构件
          currentIsolatedId = p.id;
          partRecords.forEach(item => {
            if (item.id === p.id) {
              item.mesh.visible = true;
              item.mesh.material.opacity = 1.0;
              item.mesh.material.transparent = false;
            } else {
              item.mesh.visible = true;
              item.mesh.material.opacity = 0.15;
              item.mesh.material.transparent = true;
            }
          });
        }
        renderPartList(document.getElementById('partSearch').value);
      });

      partListEl.appendChild(row);
    });
  }

  renderPartList();

  document.getElementById('partSearch').addEventListener('input', (e) => {
    renderPartList(e.target.value);
  });

  document.getElementById('btnShowAll').addEventListener('click', () => {
    currentIsolatedId = null;
    partRecords.forEach(p => {
      p.mesh.visible = true;
      p.mesh.material.opacity = 1.0;
      p.mesh.material.transparent = false;
    });
    renderPartList(document.getElementById('partSearch').value);
  });

  document.getElementById('btnResetIsolate').addEventListener('click', () => {
    currentIsolatedId = null;
    partRecords.forEach(p => {
      p.mesh.visible = true;
      p.mesh.material.opacity = 1.0;
      p.mesh.material.transparent = false;
    });
    explodeSlider.value = 0;
    explodeSlider.dispatchEvent(new Event('input'));
    fitCamera();
    renderPartList();
  });

  // 侧边栏折叠/展开
  const partSidebar = document.getElementById('partSidebar');
  const btnToggleSidebar = document.getElementById('btnToggleSidebar');
  const btnOpenSidebar = document.getElementById('btnOpenSidebar');

  btnToggleSidebar.addEventListener('click', () => {
    partSidebar.classList.remove('open');
  });
  btnOpenSidebar.addEventListener('click', () => {
    partSidebar.classList.add('open');
  });

  // --- 12. 悬停提示 Tooltip 与 3D 构件拾取反查树节点 ---
  const tooltip = document.getElementById('partTooltip');
  const raycaster = new THREE.Raycaster();
  const mouse = new THREE.Vector2();

  window.addEventListener('mousemove', (e) => {
    const rect = canvas.getBoundingClientRect();
    if (e.clientX < rect.left || e.clientX > rect.right || e.clientY < rect.top || e.clientY > rect.bottom) {
      tooltip.style.display = 'none';
      return;
    }

    mouse.x = ((e.clientX - rect.left) / rect.width) * 2 - 1;
    mouse.y = -((e.clientY - rect.top) / rect.height) * 2 + 1;

    if (isMeasureActive) {
      tooltip.style.display = 'none';
      return;
    }

    raycaster.setFromCamera(mouse, camera);
    const visibleMeshes = partRecords.filter(p => p.mesh.visible).map(p => p.mesh);
    const intersects = raycaster.intersectObjects(visibleMeshes);

    if (intersects.length > 0) {
      const hit = intersects[0].object.userData;
      tooltip.innerHTML = `
        <div class=""title"">${hit.name}</div>
        <div>三角面: ${hit.triangles} | 颜色: ${hit.colorHex}</div>
      `;
      tooltip.style.left = e.clientX + 'px';
      tooltip.style.top = e.clientY + 'px';
      tooltip.style.display = 'block';
    } else {
      tooltip.style.display = 'none';
    }
  });

  // 点击 3D 构件自动在左侧树中定位展开并平滑滚动高亮 (双向联动)
  canvas.addEventListener('click', (e) => {
    if (isMeasureActive) return;
    const rect = canvas.getBoundingClientRect();
    mouse.x = ((e.clientX - rect.left) / rect.width) * 2 - 1;
    mouse.y = -((e.clientY - rect.top) / rect.height) * 2 + 1;

    raycaster.setFromCamera(mouse, camera);
    const visibleMeshes = partRecords.filter(p => p.mesh.visible).map(p => p.mesh);
    const intersects = raycaster.intersectObjects(visibleMeshes);

    if (intersects.length > 0) {
      const hitId = intersects[0].object.userData.id;
      if (!partSidebar.classList.contains('open')) {
        partSidebar.classList.add('open');
      }
      const targetRow = document.getElementById('part-row-' + hitId);
      if (targetRow) {
        document.querySelectorAll('.part-item').forEach(el => el.classList.remove('selected'));
        targetRow.classList.add('selected');
        targetRow.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
      }
    }
  });

  // --- 13. 原生级 3D 尺寸测量工具 (点击两点/角点吸附/正交分量/Esc复位) ---
  let isMeasureActive = false;
  let measureStep = 0; // 0: 待选起点, 1: 橡皮筋实时预览, 2: 测距完成
  let point1 = null;
  let point2 = null;
  let currentHoverPoint = null;

  const btnMeasure = document.getElementById('btnMeasure');
  const measurePanel = document.getElementById('measurePanel');
  const btnCloseMeasure = document.getElementById('btnCloseMeasure');
  const btnResetMeasure = document.getElementById('btnResetMeasure');
  const measureTip = document.getElementById('measureTip');
  const valDistance = document.getElementById('valDistance');
  const valDx = document.getElementById('valDx');
  const valDy = document.getElementById('valDy');
  const valDz = document.getElementById('valDz');
  const measureBadge = document.getElementById('measureBadge');
  const badgeDistance = document.getElementById('badgeDistance');

  // 测量场景对象组
  const measureGroup = new THREE.Group();
  scene.add(measureGroup);

  const markerGeom = new THREE.SphereGeometry(maxDim * 0.008, 16, 16);

  // 角点吸附指示小球 (亮黄色发光)
  const snapMarker = new THREE.Mesh(
    markerGeom,
    new THREE.MeshBasicMaterial({ color: 0xFACC15, depthTest: false, transparent: true, opacity: 0.9 })
  );
  snapMarker.renderOrder = 9999;
  snapMarker.visible = false;
  measureGroup.add(snapMarker);

  // 起点标记球 (翠绿色)
  const pt1Marker = new THREE.Mesh(
    markerGeom,
    new THREE.MeshBasicMaterial({ color: 0x10B981, depthTest: false })
  );
  pt1Marker.renderOrder = 10000;
  pt1Marker.visible = false;
  measureGroup.add(pt1Marker);

  // 终点标记球 (亮橙色)
  const pt2Marker = new THREE.Mesh(
    markerGeom,
    new THREE.MeshBasicMaterial({ color: 0xF97316, depthTest: false })
  );
  pt2Marker.renderOrder = 10000;
  pt2Marker.visible = false;
  measureGroup.add(pt2Marker);

  // 3D 测量主引线 (天蓝色)
  const lineGeom = new THREE.BufferGeometry();
  lineGeom.setAttribute('position', new THREE.BufferAttribute(new Float32Array(6), 3));
  const dimensionLine = new THREE.Line(
    lineGeom,
    new THREE.LineBasicMaterial({ color: 0x38BDF8, linewidth: 2, depthTest: false })
  );
  dimensionLine.renderOrder = 9998;
  dimensionLine.visible = false;
  measureGroup.add(dimensionLine);

  // 正交辅助线 (X: 红, Y: 绿, Z: 蓝)
  function createOrthoLine(hexColor) {
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(6), 3));
    const m = new THREE.LineBasicMaterial({ color: hexColor, linewidth: 1.5, depthTest: false, transparent: true, opacity: 0.75 });
    const l = new THREE.Line(g, m);
    l.renderOrder = 9997;
    l.visible = false;
    measureGroup.add(l);
    return l;
  }
  const orthoLineX = createOrthoLine(0xEF4444);
  const orthoLineY = createOrthoLine(0x10B981);
  const orthoLineZ = createOrthoLine(0x3B82F6);

  // 角点吸附 (Vertex Snapping)：在屏幕 18px 范围内优先捕捉三角面角点
  function getSnappedPoint(hit, clientX, clientY, rect) {
    if (!hit || !hit.face || !hit.object) return null;
    const geom = hit.object.geometry;
    const pos = geom.attributes.position;
    if (!pos) return hit.point;

    const vA = new THREE.Vector3().fromBufferAttribute(pos, hit.face.a).applyMatrix4(hit.object.matrixWorld);
    const vB = new THREE.Vector3().fromBufferAttribute(pos, hit.face.b).applyMatrix4(hit.object.matrixWorld);
    const vC = new THREE.Vector3().fromBufferAttribute(pos, hit.face.c).applyMatrix4(hit.object.matrixWorld);

    function toScreen(v) {
      const p = v.clone().project(camera);
      return new THREE.Vector2((p.x * 0.5 + 0.5) * rect.width + rect.left, (-(p.y * 0.5) + 0.5) * rect.height + rect.top);
    }

    const mouseScreen = new THREE.Vector2(clientX, clientY);
    const sA = toScreen(vA);
    const sB = toScreen(vB);
    const sC = toScreen(vC);

    const dA = mouseScreen.distanceTo(sA);
    const dB = mouseScreen.distanceTo(sB);
    const dC = mouseScreen.distanceTo(sC);

    const snapThreshold = 18; // 屏幕像素阈值
    let best = null;
    let minD = snapThreshold;
    if (dA < minD) { minD = dA; best = vA; }
    if (dB < minD) { minD = dB; best = vB; }
    if (dC < minD) { minD = dC; best = vC; }

    return best ? best : hit.point;
  }

  function updateMeasureUI(p1, p2) {
    const dist = p1.distanceTo(p2);
    const dx = Math.abs(p2.x - p1.x);
    const dy = Math.abs(p2.y - p1.y);
    const dz = Math.abs(p2.z - p1.z);

    valDistance.textContent = dist.toFixed(2) + ' mm';
    valDx.textContent = dx.toFixed(2) + ' mm';
    valDy.textContent = dy.toFixed(2) + ' mm';
    valDz.textContent = dz.toFixed(2) + ' mm';
    badgeDistance.textContent = dist.toFixed(2) + ' mm';

    // 更新 3D 尺寸主线
    const posAttr = dimensionLine.geometry.attributes.position;
    posAttr.setXYZ(0, p1.x, p1.y, p1.z);
    posAttr.setXYZ(1, p2.x, p2.y, p2.z);
    posAttr.needsUpdate = true;
    dimensionLine.visible = true;

    // 更新 XYZ 正交辅助线
    const xAttr = orthoLineX.geometry.attributes.position;
    xAttr.setXYZ(0, p1.x, p1.y, p1.z);
    xAttr.setXYZ(1, p2.x, p1.y, p1.z);
    xAttr.needsUpdate = true;
    orthoLineX.visible = dx > 0.01;

    const yAttr = orthoLineY.geometry.attributes.position;
    yAttr.setXYZ(0, p2.x, p1.y, p1.z);
    yAttr.setXYZ(1, p2.x, p2.y, p1.z);
    yAttr.needsUpdate = true;
    orthoLineY.visible = dy > 0.01;

    const zAttr = orthoLineZ.geometry.attributes.position;
    zAttr.setXYZ(0, p2.x, p2.y, p1.z);
    zAttr.setXYZ(1, p2.x, p2.y, p2.z);
    zAttr.needsUpdate = true;
    orthoLineZ.visible = dz > 0.01;
  }

  function resetMeasure() {
    measureStep = 0;
    point1 = null;
    point2 = null;
    currentHoverPoint = null;
    snapMarker.visible = false;
    pt1Marker.visible = false;
    pt2Marker.visible = false;
    dimensionLine.visible = false;
    orthoLineX.visible = false;
    orthoLineY.visible = false;
    orthoLineZ.visible = false;
    measureBadge.classList.add('hidden');
    measureTip.textContent = '点击模型表面捕捉第 1 个测量点';
    valDistance.textContent = '0.00 mm';
    valDx.textContent = '0.00 mm';
    valDy.textContent = '0.00 mm';
    valDz.textContent = '0.00 mm';
  }

  function toggleMeasure(active) {
    isMeasureActive = active;
    btnMeasure.classList.toggle('active', isMeasureActive);
    measurePanel.classList.toggle('hidden', !isMeasureActive);
    canvas.style.cursor = isMeasureActive ? 'crosshair' : 'default';
    resetMeasure();
  }

  btnMeasure.addEventListener('click', () => toggleMeasure(!isMeasureActive));
  btnCloseMeasure.addEventListener('click', () => toggleMeasure(false));
  btnResetMeasure.addEventListener('click', resetMeasure);

  // 鼠标移动检测捕捉
  window.addEventListener('mousemove', (e) => {
    if (!isMeasureActive) return;
    const rect = canvas.getBoundingClientRect();
    if (e.clientX < rect.left || e.clientX > rect.right || e.clientY < rect.top || e.clientY > rect.bottom) {
      snapMarker.visible = false;
      return;
    }

    mouse.x = ((e.clientX - rect.left) / rect.width) * 2 - 1;
    mouse.y = -((e.clientY - rect.top) / rect.height) * 2 + 1;

    raycaster.setFromCamera(mouse, camera);
    const visibleMeshes = partRecords.filter(p => p.mesh.visible).map(p => p.mesh);
    const intersects = raycaster.intersectObjects(visibleMeshes);

    if (intersects.length > 0) {
      const snapped = getSnappedPoint(intersects[0], e.clientX, e.clientY, rect);
      currentHoverPoint = snapped;
      snapMarker.position.copy(snapped);
      snapMarker.visible = true;

      // 橡皮筋实时测量预览
      if (measureStep === 1 && point1) {
        updateMeasureUI(point1, snapped);
      }
    } else {
      snapMarker.visible = false;
      currentHoverPoint = null;
    }
  });

  // 点击选择测量点
  canvas.addEventListener('click', (e) => {
    if (!isMeasureActive || !currentHoverPoint) return;

    if (measureStep === 0) {
      // 确定起点
      point1 = currentHoverPoint.clone();
      pt1Marker.position.copy(point1);
      pt1Marker.visible = true;
      measureStep = 1;
      measureTip.textContent = '请点击选择第 2 个测量点 (按 Esc 清空)';
    } else if (measureStep === 1) {
      // 确定终点
      point2 = currentHoverPoint.clone();
      pt2Marker.position.copy(point2);
      pt2Marker.visible = true;
      measureStep = 2;
      snapMarker.visible = false;
      updateMeasureUI(point1, point2);
      measureTip.textContent = '测量完成。按 Esc 清空并可重新测距';
    }
  });

  // 快捷键 Esc 清空或退出
  window.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      if (measureStep > 0) {
        resetMeasure();
      } else if (isMeasureActive) {
        toggleMeasure(false);
      }
    }
  });

  // --- 14. 高清截图导出 ---
  document.getElementById('btnScreenshot').addEventListener('click', () => {
    renderer.render(scene, camera);
    const url = canvas.toDataURL('image/png');
    const a = document.createElement('a');
    a.href = url;
    a.download = (MODEL_TITLE || 'SolidWorks_Model') + '_3D.png';
    a.click();
  });

  // --- 15. 全屏切换 ---
  document.getElementById('btnFullscreen').addEventListener('click', () => {
    if (!document.fullscreenElement) {
      document.documentElement.requestFullscreen().catch(() => {});
    } else {
      document.exitFullscreen().catch(() => {});
    }
  });

  // --- 16. 窗口响应与渲染循环 ---
  window.addEventListener('resize', () => {
    camera.aspect = container.clientWidth / container.clientHeight;
    camera.updateProjectionMatrix();
    renderer.setSize(container.clientWidth, container.clientHeight);
  });

  function animate() {
    requestAnimationFrame(animate);
    controls.update();

    // 动态投影更新测量 Badge 的屏幕位置
    if (isMeasureActive && (measureStep === 1 || measureStep === 2) && point1) {
      const p2 = measureStep === 2 ? point2 : currentHoverPoint;
      if (p2) {
        const mid = new THREE.Vector3().addVectors(point1, p2).multiplyScalar(0.5);
        const proj = mid.clone().project(camera);
        if (proj.z < 1) {
          const sx = (proj.x * 0.5 + 0.5) * container.clientWidth;
          const sy = (-(proj.y * 0.5) + 0.5) * container.clientHeight;
          measureBadge.style.left = sx + 'px';
          measureBadge.style.top = sy + 'px';
          measureBadge.classList.remove('hidden');
        } else {
          measureBadge.classList.add('hidden');
        }
      } else {
        measureBadge.classList.add('hidden');
      }
    } else {
      measureBadge.classList.add('hidden');
    }

    renderer.render(scene, camera);
  }
  animate();

})();
";
    }
}
