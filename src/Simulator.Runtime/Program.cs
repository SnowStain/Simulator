using System.Text.Json;
using Simulator.Assets;
using Simulator.Core;
using Simulator.Editors;

namespace Simulator.Runtime;

internal static class Program
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	private static int Main(string[] args)
	{
		try
		{
			var layout = ProjectLayout.Discover();
			var configService = new ConfigurationService();
			var assetsService = new AssetCatalogService();
			var terrainEditor = new TerrainEditorService(configService, assetsService);
			var appearanceEditor = new AppearanceEditorService();

			if (args.Length == 0 || IsCommand(args, "status"))
			{
				PrintStatus(layout, configService, assetsService);
				return 0;
			}

			if (IsCommand(args, "help") || IsCommand(args, "--help") || IsCommand(args, "-h"))
			{
				PrintHelp();
				return 0;
			}

			if (args.Length >= 2 && string.Equals(args[0], "terrain", StringComparison.OrdinalIgnoreCase))
			{
				if (string.Equals(args[1], "list", StringComparison.OrdinalIgnoreCase))
				{
					PrintTerrainPresets(layout, terrainEditor);
					return 0;
				}

				if (string.Equals(args[1], "set", StringComparison.OrdinalIgnoreCase) && args.Length >= 3)
				{
					string preset = args[2];
					IReadOnlyList<string> written = terrainEditor.SetActiveMapPreset(layout, preset);
					Console.WriteLine($"Active map preset set to '{preset}'. Updated config files:");
					foreach (string path in written)
					{
						Console.WriteLine($"- {path}");
					}
					return 0;
				}
			}

			if (args.Length >= 2 && string.Equals(args[0], "appearance", StringComparison.OrdinalIgnoreCase))
			{
				if (string.Equals(args[1], "show", StringComparison.OrdinalIgnoreCase))
				{
					var appearance = appearanceEditor.LoadLatestAppearance(layout);
					Console.WriteLine($"Appearance preset: {Path.GetRelativePath(layout.RootPath, layout.AppearancePresetPath)}");
					Console.WriteLine(appearance.ToJsonString(JsonOptions));
					return 0;
				}

				if (string.Equals(args[1], "set", StringComparison.OrdinalIgnoreCase) && args.Length >= 4)
				{
					string key = args[2];
					string jsonLiteral = args[3];
					appearanceEditor.SetTopLevelValue(layout, key, jsonLiteral);
					Console.WriteLine($"Updated appearance key '{key}' in {Path.GetRelativePath(layout.RootPath, layout.AppearancePresetPath)}");
					return 0;
				}
			}

			if (args.Length >= 2 && string.Equals(args[0], "rules", StringComparison.OrdinalIgnoreCase)
				&& string.Equals(args[1], "list", StringComparison.OrdinalIgnoreCase))
			{
				PrintRuleFiles(layout, assetsService);
				return 0;
			}

			Console.Error.WriteLine("Unknown command.");
			PrintHelp();
			return 2;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Simulator runtime failed: {ex.Message}");
			return 1;
		}
	}

	private static bool IsCommand(string[] args, string command)
	{
		return args.Length >= 1 && string.Equals(args[0], command, StringComparison.OrdinalIgnoreCase);
	}

	private static void PrintStatus(ProjectLayout layout, ConfigurationService configService, AssetCatalogService assetsService)
	{
		AssetCatalog catalog = assetsService.BuildCatalog(layout);
		string configPath = configService.ResolvePrimaryConfigPath(layout);
		var config = configService.LoadConfig(configPath);
		string mapPreset = configService.GetMapPreset(config);

		Console.WriteLine("Simulator C# migration runtime");
		Console.WriteLine($"Root: {layout.RootPath}");
		Console.WriteLine($"Primary config: {Path.GetRelativePath(layout.RootPath, configPath)}");
		Console.WriteLine($"Active map preset: {mapPreset}");
		Console.WriteLine($"Catalog complete: {(catalog.IsComplete ? "yes" : "no")}");
		Console.WriteLine($"Catalog total files: {catalog.TotalFileCount}");
		Console.WriteLine();
		Console.WriteLine("Required categories:");
		foreach (AssetCategoryStatus row in catalog.Categories)
		{
			string mark = row.Exists && row.FileCount > 0 ? "OK" : "MISSING";
			Console.WriteLine($"- {row.Name}: {mark}, files={row.FileCount}");
		}
		Console.WriteLine();
		Console.WriteLine("Use 'terrain list' to inspect available map presets.");
		Console.WriteLine("Use 'terrain set <preset>' to edit active map in config files.");
		Console.WriteLine("Use 'appearance show' or 'appearance set <key> <json>' to edit appearance presets.");
	}

	private static void PrintTerrainPresets(ProjectLayout layout, TerrainEditorService terrainEditor)
	{
		IReadOnlyList<string> presets = terrainEditor.ListMapPresets(layout);
		Console.WriteLine("Available map presets:");
		foreach (string preset in presets)
		{
			Console.WriteLine($"- {preset}");
		}
	}

	private static void PrintRuleFiles(ProjectLayout layout, AssetCatalogService assetsService)
	{
		IReadOnlyList<string> files = assetsService.ListRuleFiles(layout);
		Console.WriteLine($"Rule files ({files.Count}):");
		foreach (string file in files)
		{
			Console.WriteLine($"- {file}");
		}
	}

	private static void PrintHelp()
	{
		Console.WriteLine("Simulator.Runtime commands:");
		Console.WriteLine("- status");
		Console.WriteLine("- terrain list");
		Console.WriteLine("- terrain set <preset>");
		Console.WriteLine("- appearance show");
		Console.WriteLine("- appearance set <topLevelKey> <jsonLiteral>");
		Console.WriteLine("- rules list");
	}
}
