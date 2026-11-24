using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SldFile.Cli;
using Xunit;

namespace SldFileTests;

public class CliTests
{
    [Fact]
    public void CommandLineOptions_Parse_HelpAndVersionFlags()
    {
        var optHelp = CommandLineOptions.Parse(new[] { "--help" });
        Assert.True(optHelp.ShowHelp);

        var optVer = CommandLineOptions.Parse(new[] { "-v" });
        Assert.True(optVer.ShowVersion);

        var optEmpty = CommandLineOptions.Parse(Array.Empty<string>());
        Assert.True(optEmpty.ShowHelp);
    }

    [Fact]
    public void CommandLineOptions_Parse_SubcommandsAndFlags()
    {
        var opts = CommandLineOptions.Parse(new[]
        {
            "export", "model.SLDASM",
            "--format", "html",
            "--out", "custom.html",
            "--config", "Default",
            "--theme", "dark",
            "--json"
        });

        Assert.Equal("export", opts.Command);
        Assert.Equal("model.SLDASM", opts.FilePath);
        Assert.Equal("html", opts.Format);
        Assert.Equal("custom.html", opts.OutputPath);
        Assert.Equal("Default", opts.Configuration);
        Assert.Equal("dark", opts.Theme);
        Assert.True(opts.JsonOutput);
    }

    [Fact]
    public async Task Program_Main_HelpReturnsZero()
    {
        using var sw = new StringWriter();
        var origOut = Console.Out;
        try
        {
            Console.SetOut(sw);
            int code = await Program.Main(new[] { "--help" });
            Assert.Equal(0, code);
            string output = sw.ToString();
            Assert.Contains("sldfile-cli", output);
            Assert.Contains("USAGE:", output);
            Assert.Contains("export", output);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public async Task Program_Main_VersionReturnsZero()
    {
        using var sw = new StringWriter();
        var origOut = Console.Out;
        try
        {
            Console.SetOut(sw);
            int code = await Program.Main(new[] { "--version" });
            Assert.Equal(0, code);
            string output = sw.ToString();
            Assert.Contains("sldfile-cli v", output);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public async Task Program_Main_InfoCommand_FormattedAndJson()
    {
        string samplePart = TestFileProvider.CubePart;
        if (!File.Exists(samplePart)) return;

        // 1. 测试标准文本格式输出
        using (var sw = new StringWriter())
        {
            var origOut = Console.Out;
            try
            {
                Console.SetOut(sw);
                int code = await Program.Main(new[] { "info", samplePart });
                Assert.Equal(0, code);
                string text = sw.ToString();
                Assert.Contains("SolidWorks Document Information", text);
                Assert.Contains("Part", text);
                Assert.Contains("Vertices", text);
            }
            finally
            {
                Console.SetOut(origOut);
            }
        }

        // 2. 测试 --json 结构化输出
        using (var sw = new StringWriter())
        {
            var origOut = Console.Out;
            try
            {
                Console.SetOut(sw);
                int code = await Program.Main(new[] { "info", samplePart, "--json" });
                Assert.Equal(0, code);
                string json = sw.ToString();
                using var doc = JsonDocument.Parse(json);
                Assert.Equal("Part", doc.RootElement.GetProperty("Kind").GetString());
                Assert.True(doc.RootElement.TryGetProperty("DisplayMesh", out var meshElem));
                Assert.True(meshElem.GetProperty("Vertices").GetInt32() > 0);
            }
            finally
            {
                Console.SetOut(origOut);
            }
        }
    }

    [Fact]
    public async Task Program_Main_TreeCommand_PrintsAssemblyHierarchy()
    {
        string asmPath = @"C:\Users\HP\Downloads\YX-SCGZJJ-036-A 多晶收纳车.SLDASM";
        if (!File.Exists(asmPath)) return;

        using var sw = new StringWriter();
        var origOut = Console.Out;
        try
        {
            Console.SetOut(sw);
            int code = await Program.Main(new[] { "tree", asmPath, "--depth", "2" });
            Assert.Equal(0, code);
            string output = sw.ToString();
            Assert.Contains("YX-SCGZJJ-036-A", output);
            Assert.Contains("Assembly Tree", output);
            Assert.Contains("├──", output);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public async Task Program_Main_ExportCommand_ExportsVariousFormats()
    {
        string samplePart = TestFileProvider.CubePart;
        if (!File.Exists(samplePart)) return;

        string tempDir = Path.Combine(Path.GetTempPath(), $"SldCliTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. 导出 HTML 交互式查看器
            string htmlPath = Path.Combine(tempDir, "model.html");
            int htmlCode = await Program.Main(new[] { "export", samplePart, "--format", "html", "--out", htmlPath });
            Assert.Equal(0, htmlCode);
            Assert.True(File.Exists(htmlPath));
            string htmlContent = File.ReadAllText(htmlPath);
            Assert.Contains("btnMeasure", htmlContent);
            Assert.Contains("measurePanel", htmlContent);

            // 2. 导出 glTF 2.0 二进制网格
            string glbPath = Path.Combine(tempDir, "model.glb");
            int glbCode = await Program.Main(new[] { "export", samplePart, "--format", "glb", "--out", glbPath });
            Assert.Equal(0, glbCode);
            Assert.True(File.Exists(glbPath));
            Assert.True(new FileInfo(glbPath).Length > 100);

            // 3. 导出 Wavefront OBJ
            string objPath = Path.Combine(tempDir, "model.obj");
            int objCode = await Program.Main(new[] { "export", samplePart, "--format", "obj", "--out", objPath });
            Assert.Equal(0, objCode);
            Assert.True(File.Exists(objPath));
            string objText = File.ReadAllText(objPath);
            Assert.Contains("v ", objText);
            Assert.Contains("f ", objText);

            // 4. 导出 STL
            string stlPath = Path.Combine(tempDir, "model.stl");
            int stlCode = await Program.Main(new[] { "export", samplePart, "--format", "stl", "--out", stlPath });
            Assert.Equal(0, stlCode);
            Assert.True(File.Exists(stlPath));
            Assert.True(new FileInfo(stlPath).Length > 84);

            // 5. 导出 JSON 元数据
            string jsonPath = Path.Combine(tempDir, "meta.json");
            int jsonCode = await Program.Main(new[] { "export", samplePart, "--format", "json", "--out", jsonPath });
            Assert.Equal(0, jsonCode);
            Assert.True(File.Exists(jsonPath));
            string jsonText = File.ReadAllText(jsonPath);
            Assert.Contains("\"Kind\": \"Part\"", jsonText);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Program_Main_DumpCommand_ListsStreams()
    {
        string samplePart = TestFileProvider.CubePart;
        if (!File.Exists(samplePart)) return;

        using var sw = new StringWriter();
        var origOut = Console.Out;
        try
        {
            Console.SetOut(sw);
            int code = await Program.Main(new[] { "dump", samplePart });
            Assert.Equal(0, code);
            string output = sw.ToString();
            Assert.Contains("Compound File Binary Streams", output);
            Assert.Contains("Size (Bytes)", output);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }
}
