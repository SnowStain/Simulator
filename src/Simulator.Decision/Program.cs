using Simulator.Core;
using Simulator.Core.Gameplay;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace Simulator.Decision;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new DecisionDeploymentForm());
            return 0;
        }

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

            if (args.Length == 0 || IsCommand(args, "show"))
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
        Console.WriteLine("- preset <aggressive|defensive>");
        Console.WriteLine("- set <role> <aggressive|hold|support|flank>");
    }
}
