using System;
using System.Threading.Tasks;
using SldFile.Cli.Commands;

namespace SldFile.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var opts = CommandLineOptions.Parse(args);

        if (opts.ShowVersion)
        {
            PrintVersion();
            return 0;
        }

        if (opts.ShowHelp || string.IsNullOrEmpty(opts.Command) || opts.Command == "help")
        {
            PrintHelp();
            return 0;
        }

        int exitCode = opts.Command switch
        {
            "info" => InfoCommand.Execute(opts),
            "tree" => TreeCommand.Execute(opts),
            "export" => ExportCommand.Execute(opts),
            "dump" => DumpCommand.Execute(opts),
            _ => HandleUnknownCommand(opts.Command)
        };

        return await Task.FromResult(exitCode);
    }

    private static int HandleUnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: '{cmd}'. Run 'sldfile-cli --help' for available commands.");
        return 1;
    }

    private static void PrintVersion()
    {
        var asm = typeof(Program).Assembly;
        var ver = asm.GetName().Version?.ToString(3) ?? "1.0.0";
        Console.WriteLine($"sldfile-cli v{ver} - Pure C# Cross-Platform SolidWorks Automation CLI");
    }

    private static void PrintHelp()
    {
        PrintVersion();
        Console.WriteLine();
        Console.WriteLine("USAGE:");
        Console.WriteLine("  sldfile-cli <command> <file> [options]");
        Console.WriteLine();
        Console.WriteLine("COMMANDS:");
        Console.WriteLine("  info <file>      Display document metadata, configurations, custom properties & 3D mesh metrics");
        Console.WriteLine("  tree <file>      Print hierarchical assembly component tree with instance and virtual status");
        Console.WriteLine("  export <file>    Headless batch export to 3D HTML, glTF, OBJ, STL, SVG, DXF, or Parasolid");
        Console.WriteLine("  dump <file>      Inspect internal OLE/CFBF compound document streams");
        Console.WriteLine("  help             Show this help information");
        Console.WriteLine();
        Console.WriteLine("EXPORT FORMATS (--format <fmt>):");
        Console.WriteLine("  html             Offline standalone single-file 3D WebGL (Three.js) viewer with measurements");
        Console.WriteLine("  glb              Binary glTF 2.0 3D mesh (.glb)");
        Console.WriteLine("  obj              Wavefront OBJ (.obj) 3D mesh with normals");
        Console.WriteLine("  stl              Stereolithography binary STL (.stl) for 3D printing");
        Console.WriteLine("  svg              Vector SVG for 2D engineering drawing sheets (.slddrw)");
        Console.WriteLine("  dxf              AutoCAD DXF for 2D engineering drawing sheets (.slddrw)");
        Console.WriteLine("  xb               Parasolid B-rep kernel model (.x_b)");
        Console.WriteLine("  json             Document metadata and custom properties as JSON");
        Console.WriteLine();
        Console.WriteLine("OPTIONS:");
        Console.WriteLine("  -o, --out <path>       Output file path (default: <file_dir>/<file_name>.<ext>)");
        Console.WriteLine("  -f, --format <format>  Export format (html|glb|obj|stl|svg|dxf|xb|json)");
        Console.WriteLine("  -c, --config <name>    Target SolidWorks configuration name");
        Console.WriteLine("  -s, --sheet <idx|name> Target drawing sheet index or name (for SVG/DXF)");
        Console.WriteLine("  -d, --depth <n>        Maximum depth for assembly component tree");
        Console.WriteLine("      --theme <theme>    Theme for 3D HTML viewer: 'dark' (default) or 'light'");
        Console.WriteLine("      --json             Output results formatted as JSON");
        Console.WriteLine("      --props            Output only custom properties (for 'info' command)");
        Console.WriteLine("  -v, --version          Show version banner");
        Console.WriteLine("  -h, --help             Show this help screen");
        Console.WriteLine();
        Console.WriteLine("EXAMPLES:");
        Console.WriteLine("  # Inspect file summary and 3D dimensions:");
        Console.WriteLine("  sldfile-cli info model.SLDASM");
        Console.WriteLine();
        Console.WriteLine("  # Export standalone offline 3D WebGL viewer (zero dependencies):");
        Console.WriteLine("  sldfile-cli export model.SLDASM --format html --out viewer.html");
        Console.WriteLine();
        Console.WriteLine("  # Export 3D mesh to glTF 2.0 binary:");
        Console.WriteLine("  sldfile-cli export model.SLDPRT --format glb");
        Console.WriteLine();
        Console.WriteLine("  # Print assembly tree with maximum 2 levels of depth in JSON:");
        Console.WriteLine("  sldfile-cli tree cart.SLDASM --depth 2 --json");
        Console.WriteLine();
        Console.WriteLine("  # Export 2D engineering drawing sheet to SVG vector:");
        Console.WriteLine("  sldfile-cli export drawing.SLDDRW --format svg --sheet 0");
    }
}
