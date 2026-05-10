using Simulator.Core;
using Simulator.Core.Gameplay;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Simulator.Decision;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int Main(string[] args)
    {
        return RunCli(args);
    }

    private static int RunCli(string[] args)
    {
        try
        {
            var layout = ProjectLayout.Discover();
            var configService = new ConfigurationService();
            string configPath = configService.ResolvePrimaryConfigPath(layout);
            JsonObject config = configService.LoadConfig(configPath);
            DecisionDeploymentConfig deployment = DecisionDeploymentConfig.LoadFromConfig(config);

            if (args.Length == 0)
            {
                return RunInteractive(configService, deployment, configPath, config, layout);
            }

            if (IsCommand(args, "show"))
            {
                PrintCurrent(deployment, configPath, layout);
                return 0;
            }

            if (IsCommand(args, "help") || IsCommand(args, "--help") || IsCommand(args, "-h"))
            {
                PrintHelp();
                return 0;
            }

            if (IsCommand(args, "preset") && args.Length >= 2)
            {
                deployment.ApplyPreset(args[1]);
                WriteDeployment(configService, deployment, configPath, config);
                Console.WriteLine($"Applied preset '{args[1]}'.");
                return 0;
            }

            if (IsCommand(args, "set") && args.Length >= 3)
            {
                string role = args[1];
                string mode = args[2];
                if (!deployment.SetRoleMode(role, mode))
                {
                    Console.Error.WriteLine("Invalid role or mode.");
                    return 2;
                }

                WriteDeployment(configService, deployment, configPath, config);
                Console.WriteLine($"Set {role} -> {DecisionDeploymentConfig.NormalizeMode(mode)}");
                return 0;
            }

            Console.Error.WriteLine("Unknown command.");
            PrintHelp();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Decision deployment failed: {ex.Message}");
            return 1;
        }
    }

    private static int RunInteractive(
        ConfigurationService configService,
        DecisionDeploymentConfig deployment,
        string configPath,
        JsonObject config,
        ProjectLayout layout)
    {
        Console.WriteLine("Simulator.Decision cross-platform console");
        Console.WriteLine("Commands: show, preset <default|aggressive|defensive>, set <role> <aggressive|hold|support|flank>, help, quit");
        PrintCurrent(deployment, configPath, layout);
        while (true)
        {
            Console.Write("> ");
            string? line = Console.ReadLine();
            if (line is null)
            {
                return 0;
            }

            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            if (IsCommand(parts, "quit") || IsCommand(parts, "exit"))
            {
                return 0;
            }

            if (IsCommand(parts, "show"))
            {
                PrintCurrent(deployment, configPath, layout);
                continue;
            }

            if (IsCommand(parts, "help"))
            {
                PrintHelp();
                continue;
            }

            if (IsCommand(parts, "preset") && parts.Length >= 2)
            {
                deployment.ApplyPreset(parts[1]);
                WriteDeployment(configService, deployment, configPath, config);
                Console.WriteLine($"Applied preset '{parts[1]}'.");
                continue;
            }

            if (IsCommand(parts, "set") && parts.Length >= 3)
            {
                if (!deployment.SetRoleMode(parts[1], parts[2]))
                {
                    Console.WriteLine("Invalid role or mode.");
                    continue;
                }

                WriteDeployment(configService, deployment, configPath, config);
                Console.WriteLine($"Set {parts[1]} -> {DecisionDeploymentConfig.NormalizeMode(parts[2])}");
                continue;
            }

            Console.WriteLine("Unknown command. Type help for usage.");
        }
    }

    private static void PrintCurrent(DecisionDeploymentConfig deployment, string configPath, ProjectLayout layout)
    {
        Console.WriteLine("Decision deployment configuration");
        Console.WriteLine($"Config: {Path.GetRelativePath(layout.RootPath, configPath)}");
        Console.WriteLine(deployment.ToJson().ToJsonString(JsonOptions));
    }

    private static void WriteDeployment(
        ConfigurationService configService,
        DecisionDeploymentConfig deployment,
        string configPath,
        JsonObject root)
    {
        deployment.WriteToConfig(root);
        configService.SaveConfig(configPath, root);
    }

    private static bool IsCommand(string[] args, string command)
    {
        return args.Length >= 1 && string.Equals(args[0], command, StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Simulator.Decision commands:");
        Console.WriteLine("- show");
        Console.WriteLine("- preset <default|aggressive|defensive>");
        Console.WriteLine("- set <role> <aggressive|hold|support|flank>");
        Console.WriteLine("- quit");
    }
}
