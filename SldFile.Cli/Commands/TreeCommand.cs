using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SldFile.Cli.Commands;

public static class TreeCommand
{
    public static int Execute(CommandLineOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.FilePath))
        {
            Console.Error.WriteLine("Error: File path is required for 'tree' command. See 'sldfile-cli --help'.");
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

            if (file.Kind != DocumentKind.Assembly)
            {
                if (file.Kind == DocumentKind.Part)
                {
                    Console.WriteLine($"[Part Document: {Path.GetFileName(fullPath)}]");
                    Console.WriteLine("  Parts do not have an assembly component tree. Use 'info' command to view properties and mesh geometry.");
                    return 0;
                }
                else
                {
                    Console.WriteLine($"[Drawing Document: {Path.GetFileName(fullPath)}]");
                    Console.WriteLine("  Drawings contain sheets and views rather than assembly component trees.");
                    return 0;
                }
            }

            var tree = file.GetAssemblyTree(opts.Configuration);
            int maxDepth = opts.MaxDepth ?? int.MaxValue;

            if (opts.JsonOutput)
            {
                var jsonTree = BuildJsonTree(tree, 1, maxDepth);
                string json = JsonSerializer.Serialize(new
                {
                    Assembly = Path.GetFileName(fullPath),
                    FilePath = fullPath,
                    TotalComponents = tree.Count,
                    Components = jsonTree
                }, new JsonSerializerOptions { WriteIndented = true });

                Console.WriteLine(json);
                return 0;
            }

            Console.WriteLine($"📦 {Path.GetFileName(fullPath)} (Assembly Tree)");
            for (int i = 0; i < tree.Count; i++)
            {
                bool isLast = i == tree.Count - 1;
                PrintNode(tree[i], "", isLast, 1, maxDepth);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error reading assembly tree: {ex.Message}");
            return 1;
        }
    }

    private static void PrintNode(AssemblyComponentNode node, string indent, bool isLast, int currentDepth, int maxDepth)
    {
        string branch = isLast ? "└── " : "├── ";
        string badge = node.IsVirtual ? "[Virtual] " : (node.IsSuppressed ? "[Suppressed] " : "");
        string name = string.IsNullOrWhiteSpace(node.InstanceName) ? node.Name : $"{node.Name} ({node.InstanceName})";

        Console.WriteLine($"{indent}{branch}{badge}{name}");

        if (currentDepth >= maxDepth) return;

        if (node.Children != null && node.Children.Count > 0)
        {
            string childIndent = indent + (isLast ? "    " : "│   ");
            for (int i = 0; i < node.Children.Count; i++)
            {
                bool childLast = i == node.Children.Count - 1;
                PrintNode(node.Children[i], childIndent, childLast, currentDepth + 1, maxDepth);
            }
        }
    }

    private static List<object> BuildJsonTree(IReadOnlyList<AssemblyComponentNode> nodes, int currentDepth, int maxDepth)
    {
        var list = new List<object>();
        if (nodes == null) return list;

        foreach (var n in nodes)
        {
            var children = (currentDepth < maxDepth && n.Children != null && n.Children.Count > 0)
                ? BuildJsonTree(n.Children, currentDepth + 1, maxDepth)
                : null;

            list.Add(new
            {
                Name = n.Name,
                InstanceName = n.InstanceName,
                FilePath = n.FilePath,
                Configuration = n.ConfigurationName,
                IsVirtual = n.IsVirtual,
                IsSuppressed = n.IsSuppressed,
                Children = children
            });
        }
        return list;
    }
}
