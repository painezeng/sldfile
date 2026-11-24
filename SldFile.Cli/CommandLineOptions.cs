using System;
using System.Collections.Generic;

namespace SldFile.Cli;

/// <summary>
/// Parsed command line options for sldfile-cli.
/// </summary>
public sealed class CommandLineOptions
{
    public string Command { get; set; } = "help";
    public string? FilePath { get; set; }
    public string? OutputPath { get; set; }
    public string? Format { get; set; }
    public string? Configuration { get; set; }
    public string? Sheet { get; set; }
    public string? Theme { get; set; }
    public int? MaxDepth { get; set; }
    public bool JsonOutput { get; set; }
    public bool PropsOnly { get; set; }
    public bool ShowHelp { get; set; }
    public bool ShowVersion { get; set; }
    public bool Quiet { get; set; }
    public List<string> ExtraArgs { get; } = new();

    public static CommandLineOptions Parse(string[] args)
    {
        var opts = new CommandLineOptions();
        if (args == null || args.Length == 0)
        {
            opts.ShowHelp = true;
            return opts;
        }

        int index = 0;
        string first = args[0];

        if (first is "--help" or "-h" or "help")
        {
            opts.ShowHelp = true;
            return opts;
        }

        if (first is "--version" or "-v" or "version")
        {
            opts.ShowVersion = true;
            return opts;
        }

        if (!first.StartsWith("-", StringComparison.Ordinal))
        {
            opts.Command = first.ToLowerInvariant();
            index = 1;
        }
        else
        {
            // Default to "info" if a file or flag was given without command
            opts.Command = "info";
        }

        for (int i = index; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg is "--help" or "-h")
            {
                opts.ShowHelp = true;
            }
            else if (arg is "--version" or "-v")
            {
                opts.ShowVersion = true;
            }
            else if (arg is "--json")
            {
                opts.JsonOutput = true;
            }
            else if (arg is "--props")
            {
                opts.PropsOnly = true;
            }
            else if (arg is "--quiet" or "-q")
            {
                opts.Quiet = true;
            }
            else if (arg is "--out" or "-o")
            {
                if (i + 1 < args.Length) opts.OutputPath = args[++i];
            }
            else if (arg.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
            {
                opts.OutputPath = arg.Substring(6);
            }
            else if (arg is "--format" or "-f")
            {
                if (i + 1 < args.Length) opts.Format = args[++i];
            }
            else if (arg.StartsWith("--format=", StringComparison.OrdinalIgnoreCase))
            {
                opts.Format = arg.Substring(9);
            }
            else if (arg is "--config" or "-c")
            {
                if (i + 1 < args.Length) opts.Configuration = args[++i];
            }
            else if (arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
            {
                opts.Configuration = arg.Substring(9);
            }
            else if (arg is "--sheet" or "-s")
            {
                if (i + 1 < args.Length) opts.Sheet = args[++i];
            }
            else if (arg.StartsWith("--sheet=", StringComparison.OrdinalIgnoreCase))
            {
                opts.Sheet = arg.Substring(8);
            }
            else if (arg is "--depth" or "-d")
            {
                if (i + 1 < args.Length && int.TryParse(args[++i], out int d)) opts.MaxDepth = d;
            }
            else if (arg is "--theme")
            {
                if (i + 1 < args.Length) opts.Theme = args[++i];
            }
            else if (!arg.StartsWith("-", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(opts.FilePath))
                {
                    opts.FilePath = arg;
                }
                else
                {
                    opts.ExtraArgs.Add(arg);
                }
            }
        }

        return opts;
    }
}
