using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Simulator.Core.Gameplay;
using Simulator.Core.Map;
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
			var mapPresetService = new MapPresetService();
			var ruleLoader = new RuleSetLoader();
			var terrainEditor = new TerrainEditorService(configService, assetsService);
			var appearanceEditor = new AppearanceEditorService();
			var ruleEditor = new RuleEditorService(configService);

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

			if (args.Length >= 1 && string.Equals(args[0], "rules", StringComparison.OrdinalIgnoreCase))
			{
				if (args.Length >= 2 && string.Equals(args[1], "list", StringComparison.OrdinalIgnoreCase))
				{
					PrintRuleFiles(layout, assetsService);
					return 0;
				}

				if (args.Length >= 2 && string.Equals(args[1], "show", StringComparison.OrdinalIgnoreCase))
				{
					PrintRules(ruleEditor.LoadRules(layout));
					return 0;
				}

				if (args.Length >= 2 && string.Equals(args[1], "validate", StringComparison.OrdinalIgnoreCase))
				{
					ValidateRules(layout, configService, ruleLoader);
					return 0;
				}

				if (args.Length >= 4 && string.Equals(args[1], "set", StringComparison.OrdinalIgnoreCase))
				{
					IReadOnlyList<string> written = ruleEditor.SetRuleValue(layout, args[2], args[3]);
					Console.WriteLine($"Updated rule path '{args[2]}'. Updated config files:");
					foreach (string path in written)
					{
						Console.WriteLine($"- {path}");
					}
					return 0;
				}
			}

			if (args.Length >= 1 && string.Equals(args[0], "arena", StringComparison.OrdinalIgnoreCase))
			{
				if (args.Length >= 2 && string.Equals(args[1], "probe", StringComparison.OrdinalIgnoreCase))
				{
					if (args.Length < 4)
					{
						throw new ArgumentException("Usage: arena probe <x> <y> [preset]");
					}

					double x = ParseDoubleArg(args[2], "x");
					double y = ParseDoubleArg(args[3], "y");
					string preset = args.Length >= 5
						? args[4]
						: mapPresetService.ResolvePresetName(layout, configService);

					ProbeArena(layout, configService, mapPresetService, ruleLoader, preset, x, y);
					return 0;
				}
			}

			if (args.Length >= 1 && string.Equals(args[0], "simulate", StringComparison.OrdinalIgnoreCase))
			{
				if (args.Length >= 2 && string.Equals(args[1], "run", StringComparison.OrdinalIgnoreCase))
				{
					double duration = args.Length >= 3 ? ParseDoubleArg(args[2], "durationSec") : 60.0;
					double dt = args.Length >= 4 ? ParseDoubleArg(args[3], "dtSec") : 0.2;
					string preset = args.Length >= 5
						? args[4]
						: mapPresetService.ResolvePresetName(layout, configService);

					RunSimulation(layout, configService, mapPresetService, ruleLoader, preset, duration, dt);
					return 0;
				}
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
		Console.WriteLine("- rules show");
		Console.WriteLine("- rules validate");
		Console.WriteLine("- rules set <path> <jsonLiteral>");
		Console.WriteLine("- arena probe <x> <y> [preset]");
		Console.WriteLine("- simulate run [durationSec] [dtSec] [preset]");
	}

	private static double ParseDoubleArg(string input, string argumentName)
	{
		if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
		{
			throw new ArgumentException($"Invalid value for {argumentName}: {input}");
		}

		return value;
	}

	private static void PrintRules(JsonObject rules)
	{
		Console.WriteLine("Effective rules snapshot:");
		Console.WriteLine(rules.ToJsonString(JsonOptions));
	}

	private static void ValidateRules(ProjectLayout layout, ConfigurationService configService, RuleSetLoader ruleLoader)
	{
		string configPath = configService.ResolvePrimaryConfigPath(layout);
		JsonObject config = configService.LoadConfig(configPath);
		RuleSet rules = ruleLoader.LoadFromConfig(config);
		IReadOnlyList<RuleValidationIssue> issues = ruleLoader.Validate(rules);

		Console.WriteLine($"Rule validation for {Path.GetRelativePath(layout.RootPath, configPath)}");
		if (issues.Count == 0)
		{
			Console.WriteLine("- OK: no validation issues.");
			return;
		}

		foreach (RuleValidationIssue issue in issues)
		{
			Console.WriteLine($"- {issue.Severity.ToUpperInvariant()}: {issue.Message}");
		}
	}

	private static void ProbeArena(
		ProjectLayout layout,
		ConfigurationService configService,
		MapPresetService mapPresetService,
		RuleSetLoader ruleLoader,
		string preset,
		double x,
		double y)
	{
		MapPresetDefinition mapPreset = mapPresetService.LoadPreset(layout, preset);

		string configPath = configService.ResolvePrimaryConfigPath(layout);
		JsonObject config = configService.LoadConfig(configPath);
		RuleSet rules = ruleLoader.LoadFromConfig(config);
		var interactionService = new ArenaInteractionService(rules);

		IReadOnlyList<FacilityRegion> facilities = mapPresetService.QueryFacilitiesAt(mapPreset, x, y);
		Console.WriteLine($"Arena probe preset='{preset}', position=({x:0.##}, {y:0.##})");
		if (facilities.Count == 0)
		{
			Console.WriteLine("- No facilities hit.");
			return;
		}

		foreach (FacilityRegion region in facilities)
		{
			FacilityInteractionDescriptor descriptor = interactionService.DescribeFacility(region);
			string roles = descriptor.RecommendedRoles.Count == 0
				? "n/a"
				: string.Join(",", descriptor.RecommendedRoles);
			Console.WriteLine($"- {region.Id} | type={region.Type} | team={region.Team}");
			Console.WriteLine($"  interaction: {descriptor.Summary}");
			Console.WriteLine($"  recommended_roles: {roles}");
		}
	}

	private static void RunSimulation(
		ProjectLayout layout,
		ConfigurationService configService,
		MapPresetService mapPresetService,
		RuleSetLoader ruleLoader,
		string preset,
		double durationSec,
		double dtSec)
	{
		string configPath = configService.ResolvePrimaryConfigPath(layout);
		JsonObject config = configService.LoadConfig(configPath);
		RuleSet rules = ruleLoader.LoadFromConfig(config);
		MapPresetDefinition mapPreset = mapPresetService.LoadPreset(layout, preset);

		var bootstrap = new SimulationBootstrapService();
		SimulationWorldState world = bootstrap.BuildInitialWorld(config, rules, mapPreset);
		var interactionService = new ArenaInteractionService(rules);
		var simulation = new RuleSimulationService(rules, interactionService, seed: 20260419);

		SimulationRunReport report = simulation.Run(world, mapPreset.Facilities, durationSec, dtSec);

		Console.WriteLine($"Simulation done. preset={preset}, duration={report.DurationSec:0.##}s, dt={report.DeltaTimeSec:0.###}s");
		Console.WriteLine($"Shots: total={report.TotalShots}, hits={report.HitShots}, hit_ratio={(report.TotalShots > 0 ? (double)report.HitShots / report.TotalShots : 0):P1}");
		Console.WriteLine($"Facility interaction events: {report.InteractionEventCount}");
		Console.WriteLine();
		Console.WriteLine("Final entity states:");
		foreach (SimulationEntity entity in report.FinalEntities
			.OrderBy(item => item.Team, StringComparer.OrdinalIgnoreCase)
			.ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
		{
			Console.WriteLine(
				$"- {entity.Id}: alive={entity.IsAlive}, state={entity.State}, hp={entity.Health:0.#}/{entity.MaxHealth:0.#}, heat={entity.Heat:0.#}, ammo17={entity.Ammo17Mm}, ammo42={entity.Ammo42Mm}, minerals={entity.CarriedMinerals}");
		}

		if (report.InteractionEvents.Count > 0)
		{
			Console.WriteLine();
			Console.WriteLine("Recent facility events:");
			foreach (FacilityInteractionEvent evt in report.InteractionEvents.TakeLast(10))
			{
				Console.WriteLine($"- t={evt.TimeSec:0.##} [{evt.Team}] {evt.EntityId} @ {evt.FacilityType}/{evt.FacilityId}: {evt.Message}");
			}
		}

		if (report.CombatEvents.Count > 0)
		{
			Console.WriteLine();
			Console.WriteLine("Recent combat events:");
			foreach (SimulationCombatEvent evt in report.CombatEvents.TakeLast(10))
			{
				Console.WriteLine(
					$"- t={evt.TimeSec:0.##} {evt.ShooterId} -> {evt.TargetId}, dist={evt.DistanceM:0.##}m, p={evt.HitProbability:0.##}, hit={evt.Hit}, dmg={evt.Damage:0.##}");
			}
		}
	}
}
