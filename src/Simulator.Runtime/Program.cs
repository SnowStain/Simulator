using System.Diagnostics;
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

			if (args.Length >= 1 && string.Equals(args[0], "energy", StringComparison.OrdinalIgnoreCase))
			{
				if (args.Length >= 2 && string.Equals(args[1], "visual-check", StringComparison.OrdinalIgnoreCase))
				{
					string outputDirectory = args.Length >= 3
						? args[2]
						: Path.Combine(layout.RootPath, "build_verify", "energy_visual_check");
					RunEnergyVisualCheck(outputDirectory);
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
		Console.WriteLine("- energy visual-check [outputDir]");
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

	private static void RunEnergyVisualCheck(string outputDirectory)
	{
		string resolvedOutputDirectory = Path.GetFullPath(outputDirectory);
		Directory.CreateDirectory(resolvedOutputDirectory);

		var cases = BuildEnergyVisualCheckCases();
		var failures = new List<string>();
		foreach (EnergyVisualCheckCase testCase in cases)
		{
			failures.AddRange((testCase.ExtraAssertions ?? Array.Empty<string>())
				.Select(assertion => $"{testCase.Name}: {assertion}"));
			foreach (EnergyVisualExpectation expectation in testCase.Expectations)
			{
				EnergyMechanismArmDisplayState actual = EnergyMechanismVisualLogic.ResolveArmState(
					testCase.TeamState,
					expectation.ArmIndex,
					testCase.GameTimeSec);
				if (actual.Kind != expectation.Kind
					|| actual.Pending != expectation.Pending
					|| actual.Completed != expectation.Completed)
				{
					failures.Add(
						$"{testCase.Name} A{expectation.ArmIndex}: expected {expectation.Kind}/pending={expectation.Pending}/completed={expectation.Completed}, got {actual.Kind}/pending={actual.Pending}/completed={actual.Completed}");
				}
			}
		}

		string svgPath = Path.Combine(resolvedOutputDirectory, "energy_visual_check.svg");
		File.WriteAllText(svgPath, RenderEnergyVisualCheckSvg(cases));

		string? pngPath = TryRenderSvgWithChrome(svgPath, resolvedOutputDirectory);
		string jsonPath = Path.Combine(resolvedOutputDirectory, "energy_visual_check.json");
		File.WriteAllText(jsonPath, JsonSerializer.Serialize(BuildEnergyVisualCheckReport(cases, failures, svgPath, pngPath), JsonOptions));

		Console.WriteLine("Energy visual check done.");
		Console.WriteLine($"SVG: {svgPath}");
		if (!string.IsNullOrWhiteSpace(pngPath))
		{
			Console.WriteLine($"PNG: {pngPath}");
		}
		else
		{
			Console.WriteLine("PNG: skipped (google-chrome headless render failed or was unavailable).");
		}

		Console.WriteLine($"Report: {jsonPath}");
		Console.WriteLine(failures.Count == 0
			? "Assertions: OK, energy mechanism visual state matches the rule scenarios."
			: $"Assertions: FAILED, {failures.Count} issue(s).");
		foreach (string failure in failures)
		{
			Console.WriteLine($"- {failure}");
		}

		if (failures.Count > 0)
		{
			throw new InvalidOperationException("Energy visual check assertions failed.");
		}
	}

	private static IReadOnlyList<EnergyVisualCheckCase> BuildEnergyVisualCheckCases()
	{
		var cases = new List<EnergyVisualCheckCase>();

		SimulationTeamState inactive = CreateEnergyTeamState("red", large: false, state: "inactive");
		cases.Add(new EnergyVisualCheckCase(
			"inactive_all_off",
			"未激活：所有灯臂熄灭",
			0.0,
			inactive,
			ExpectAll(EnergyMechanismArmDisplayKind.Off, pending: false, completed: false)));

		SimulationTeamState smallPending = CreateEnergyTeamState("red", large: false, state: "activating", litMask: 1 << 2);
		cases.Add(new EnergyVisualCheckCase(
			"small_pending_single",
			"小能量机关待命中：仅一个圆盘显示待命中标识，灯臂不常亮",
			12.0,
			smallPending,
			new[]
			{
				Expect(0, EnergyMechanismArmDisplayKind.Off),
				Expect(1, EnergyMechanismArmDisplayKind.Off),
				Expect(2, EnergyMechanismArmDisplayKind.Pending, pending: true, completed: false),
				Expect(3, EnergyMechanismArmDisplayKind.Off),
				Expect(4, EnergyMechanismArmDisplayKind.Off),
			}));

		SimulationTeamState smallHit = CreateEnergyTeamState("red", large: false, state: "activating", litMask: 1 << 4);
		smallHit.EnergyHitRingsByArm[2] = 8;
		smallHit.EnergyHitRingCount = 1;
		smallHit.EnergyHitRingSum = 8;
		smallHit.EnergyActivatedGroupCount = 1;
		smallHit.EnergyActivationOrder[0] = 2;
		smallHit.EnergyActivationOrder[1] = 4;
		smallHit.EnergyLastHitArmIndex = 2;
		smallHit.EnergyLastRingScore = 8;
		smallHit.EnergyLastHitFlashEndSec = 30.0;
		cases.Add(new EnergyVisualCheckCase(
			"small_hit_then_next_pending",
			"小能量机关命中后：命中的灯臂常亮，下一目标显示待命中",
			20.0,
			smallHit,
			new[]
			{
				Expect(0, EnergyMechanismArmDisplayKind.Off),
				Expect(1, EnergyMechanismArmDisplayKind.Off),
				Expect(2, EnergyMechanismArmDisplayKind.Hit, pending: false, completed: true),
				Expect(3, EnergyMechanismArmDisplayKind.Off),
				Expect(4, EnergyMechanismArmDisplayKind.Pending, pending: true, completed: false),
			}));

		SimulationTeamState largePending = CreateEnergyTeamState("blue", large: true, state: "activating", litMask: (1 << 0) | (1 << 3));
		cases.Add(new EnergyVisualCheckCase(
			"large_pending_pair",
			"大能量机关待命中：同组两个圆盘同时显示待命中标识",
			190.0,
			largePending,
			new[]
			{
				Expect(0, EnergyMechanismArmDisplayKind.Pending, pending: true, completed: false),
				Expect(1, EnergyMechanismArmDisplayKind.Off),
				Expect(2, EnergyMechanismArmDisplayKind.Off),
				Expect(3, EnergyMechanismArmDisplayKind.Pending, pending: true, completed: false),
				Expect(4, EnergyMechanismArmDisplayKind.Off),
			}));

		SimulationTeamState largeHitPair = CreateEnergyTeamState("blue", large: true, state: "activating", litMask: 1 << 3);
		largeHitPair.EnergyHitRingsByArm[0] = 10;
		largeHitPair.EnergyHitRingCount = 1;
		largeHitPair.EnergyHitRingSum = 10;
		largeHitPair.EnergyActivatedGroupCount = 1;
		largeHitPair.EnergyActivationOrder[0] = 0;
		largeHitPair.EnergyActivationOrder[1] = 3;
		largeHitPair.EnergyNextModuleDelaySec = 0.6;
		cases.Add(new EnergyVisualCheckCase(
			"large_first_hit_supplement_window",
			"大能量机关命中任意一个后：命中灯臂常亮，同组剩余灯臂仍显示补击窗口",
			191.0,
			largeHitPair,
			new[]
			{
				Expect(0, EnergyMechanismArmDisplayKind.Hit, pending: false, completed: true),
				Expect(1, EnergyMechanismArmDisplayKind.Off),
				Expect(2, EnergyMechanismArmDisplayKind.Off),
				Expect(3, EnergyMechanismArmDisplayKind.Pending, pending: true, completed: false),
				Expect(4, EnergyMechanismArmDisplayKind.Off),
			}));

		SimulationTeamState completed = CreateEnergyTeamState("red", large: true, state: "activated");
		for (int index = 0; index < EnergyMechanismVisualLogic.ArmCount; index++)
		{
			completed.EnergyHitRingsByArm[index] = 10;
		}
		completed.EnergyActivatedGroupCount = 5;
		completed.EnergyBuffTimerSec = 45.0;
		cases.Add(new EnergyVisualCheckCase(
			"activated_all_on",
			"完全激活：五个灯臂全部常亮",
			260.0,
			completed,
			ExpectAll(EnergyMechanismArmDisplayKind.Completed, pending: false, completed: true)));

		SimulationTeamState wrongHit = CreateEnergyTeamState("red", large: false, state: "activating", litMask: 1 << 1);
		var world = new SimulationWorldState { GameTimeSec = 42.0 };
		world.Teams["red"] = wrongHit;
		var shooter = new SimulationEntity
		{
			Id = "red_robot_3",
			Team = "red",
			EntityType = "robot",
			RoleKey = "infantry",
			IsAlive = true,
		};
		var interaction = new ArenaInteractionService(RuleSet.CreateDefault());
		FacilityInteractionEvent? wrongHitEvent = interaction.ApplyEnergyMechanismHit(
			world,
			shooter,
			new ArmorPlateTarget("energy_red_arm_4_ring_10", 0, 0, 0, 0, EnergyRingScore: 10),
			10);
		cases.Add(new EnergyVisualCheckCase(
			"wrong_lit_plate_resets",
			wrongHitEvent is null
				? "错误命中检查：未产生失败事件"
				: $"错误命中检查：{wrongHitEvent.Message}",
			world.GameTimeSec,
			wrongHit,
			new[]
			{
				Expect(0, (wrongHit.EnergyCurrentLitMask & (1 << 0)) != 0 ? EnergyMechanismArmDisplayKind.Pending : EnergyMechanismArmDisplayKind.Off, pending: (wrongHit.EnergyCurrentLitMask & (1 << 0)) != 0),
				Expect(1, (wrongHit.EnergyCurrentLitMask & (1 << 1)) != 0 ? EnergyMechanismArmDisplayKind.Pending : EnergyMechanismArmDisplayKind.Off, pending: (wrongHit.EnergyCurrentLitMask & (1 << 1)) != 0),
				Expect(2, (wrongHit.EnergyCurrentLitMask & (1 << 2)) != 0 ? EnergyMechanismArmDisplayKind.Pending : EnergyMechanismArmDisplayKind.Off, pending: (wrongHit.EnergyCurrentLitMask & (1 << 2)) != 0),
				Expect(3, (wrongHit.EnergyCurrentLitMask & (1 << 3)) != 0 ? EnergyMechanismArmDisplayKind.Pending : EnergyMechanismArmDisplayKind.Off, pending: (wrongHit.EnergyCurrentLitMask & (1 << 3)) != 0),
				Expect(4, (wrongHit.EnergyCurrentLitMask & (1 << 4)) != 0 ? EnergyMechanismArmDisplayKind.Pending : EnergyMechanismArmDisplayKind.Off, pending: (wrongHit.EnergyCurrentLitMask & (1 << 4)) != 0),
			},
			ExtraAssertions: BuildWrongHitAssertions(wrongHit, wrongHitEvent)));

		return cases;
	}

	private static SimulationTeamState CreateEnergyTeamState(
		string team,
		bool large,
		string state,
		int litMask = 0)
	{
		var teamState = new SimulationTeamState(team, initialGold: 0.0)
		{
			EnergyMechanismState = state,
			EnergyLargeMechanismActive = large,
			EnergyCurrentLitMask = litMask & 0x1F,
			EnergyRotorDirectionSign = string.Equals(team, "blue", StringComparison.OrdinalIgnoreCase) ? -1 : 1,
		};

		for (int index = 0; index < teamState.EnergyActivationOrder.Length; index++)
		{
			teamState.EnergyActivationOrder[index] = index;
		}

		return teamState;
	}

	private static EnergyVisualExpectation Expect(
		int armIndex,
		EnergyMechanismArmDisplayKind kind,
		bool pending = false,
		bool? completed = null)
		=> new(armIndex, kind, pending, completed ?? kind is EnergyMechanismArmDisplayKind.Hit or EnergyMechanismArmDisplayKind.ActivatedByProgress or EnergyMechanismArmDisplayKind.Completed);

	private static IReadOnlyList<EnergyVisualExpectation> ExpectAll(
		EnergyMechanismArmDisplayKind kind,
		bool pending,
		bool completed)
		=> Enumerable.Range(0, EnergyMechanismVisualLogic.ArmCount)
			.Select(index => new EnergyVisualExpectation(index, kind, pending, completed))
			.ToArray();

	private static IReadOnlyList<string> BuildWrongHitAssertions(
		SimulationTeamState teamState,
		FacilityInteractionEvent? wrongHitEvent)
	{
		var failures = new List<string>();
		if (wrongHitEvent is null)
		{
			failures.Add("wrong-hit did not produce a failure event");
		}

		if (EnergyMechanismVisualLogic.ResolveHitMask(teamState, large: false) != 0)
		{
			failures.Add("wrong-hit did not clear persistent hit rings");
		}

		if (teamState.EnergyHitRingCount != 0)
		{
			failures.Add("wrong-hit did not reset hit count");
		}

		if (!string.Equals(teamState.EnergyMechanismState, "activating", StringComparison.OrdinalIgnoreCase)
			|| teamState.EnergyCurrentLitMask == 0)
		{
			failures.Add("wrong-hit did not restart an activating attempt with a new lit target");
		}

		return failures;
	}

	private static string RenderEnergyVisualCheckSvg(IReadOnlyList<EnergyVisualCheckCase> cases)
	{
		const int width = 1180;
		int rowHeight = 164;
		int height = 78 + cases.Count * rowHeight;
		var lines = new List<string>
		{
			$"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">",
			"<rect width=\"100%\" height=\"100%\" fill=\"#111820\"/>",
			"<text x=\"32\" y=\"42\" fill=\"#f2f7fb\" font-family=\"Arial, sans-serif\" font-size=\"26\" font-weight=\"700\">Energy Mechanism Visual Check</text>",
			"<text x=\"32\" y=\"66\" fill=\"#a9bac7\" font-family=\"Arial, sans-serif\" font-size=\"14\">Linux-native snapshot: pending ring 4/7 marks, hit steady lights, full activation, and wrong-hit reset.</text>",
		};

		for (int caseIndex = 0; caseIndex < cases.Count; caseIndex++)
		{
			EnergyVisualCheckCase testCase = cases[caseIndex];
			int y = 88 + caseIndex * rowHeight;
			lines.Add($"<rect x=\"24\" y=\"{y - 10}\" width=\"1132\" height=\"144\" rx=\"8\" fill=\"#17222c\" stroke=\"#2b3c4a\"/>");
			lines.Add($"<text x=\"44\" y=\"{y + 18}\" fill=\"#eef5f8\" font-family=\"Arial, sans-serif\" font-size=\"17\" font-weight=\"700\">{EscapeXml(testCase.Title)}</text>");
			lines.Add($"<text x=\"44\" y=\"{y + 42}\" fill=\"#a8bac8\" font-family=\"Arial, sans-serif\" font-size=\"13\">{EscapeXml(testCase.Name)} | state={EscapeXml(testCase.TeamState.EnergyMechanismState)} | lit={FormatEnergyMask(testCase.TeamState.EnergyCurrentLitMask)} | hit={FormatEnergyMask(EnergyMechanismVisualLogic.ResolveHitMask(testCase.TeamState, testCase.TeamState.EnergyLargeMechanismActive))}</text>");
			for (int arm = 0; arm < EnergyMechanismVisualLogic.ArmCount; arm++)
			{
				EnergyMechanismArmDisplayState state = EnergyMechanismVisualLogic.ResolveArmState(testCase.TeamState, arm, testCase.GameTimeSec);
				int cx = 82 + arm * 92;
				int cy = y + 94;
				lines.Add(RenderEnergyArmSvg(cx, cy, arm, state, testCase.TeamState.Team));
			}

			int legendX = 570;
			int legendY = y + 72;
			lines.Add($"<text x=\"{legendX}\" y=\"{legendY}\" fill=\"#d3e1e8\" font-family=\"Arial, sans-serif\" font-size=\"14\">{EscapeXml(testCase.Title)}</text>");
			lines.Add($"<text x=\"{legendX}\" y=\"{legendY + 24}\" fill=\"#8fa3b2\" font-family=\"Arial, sans-serif\" font-size=\"13\">常亮=实心队色；待命中=第4/7环常亮；熄灭=暗灰</text>");
			lines.Add($"<text x=\"{legendX}\" y=\"{legendY + 48}\" fill=\"#8fa3b2\" font-family=\"Arial, sans-serif\" font-size=\"13\">{EscapeXml(BuildCaseStateText(testCase))}</text>");
		}

		lines.Add("</svg>");
		return string.Join(Environment.NewLine, lines);
	}

	private static string RenderEnergyArmSvg(int cx, int cy, int armIndex, EnergyMechanismArmDisplayState state, string team)
	{
		string teamColor = string.Equals(team, "blue", StringComparison.OrdinalIgnoreCase) ? "#2e73ff" : "#ff3939";
		string fill = state.Completed ? teamColor : "#39434d";
		string stroke = state.Pending ? "#ffd84a" : state.Completed ? "#eaf3ff" : "#65717d";
		string opacity = state.Kind == EnergyMechanismArmDisplayKind.Off ? "0.58" : "1";
		var lines = new List<string>
		{
			$"<g opacity=\"{opacity}\">",
			$"<line x1=\"{cx}\" y1=\"{cy}\" x2=\"{cx}\" y2=\"{cy - 46}\" stroke=\"{fill}\" stroke-width=\"12\" stroke-linecap=\"round\"/>",
			$"<circle cx=\"{cx}\" cy=\"{cy - 58}\" r=\"24\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"4\"/>",
			$"<circle cx=\"{cx}\" cy=\"{cy - 58}\" r=\"13\" fill=\"none\" stroke=\"{(state.Completed ? "#ffffff" : "#8b96a2")}\" stroke-width=\"2\"/>",
			$"<text x=\"{cx}\" y=\"{cy + 28}\" fill=\"#cbd7df\" text-anchor=\"middle\" font-family=\"Arial, sans-serif\" font-size=\"13\">A{armIndex}</text>",
			$"<text x=\"{cx}\" y=\"{cy + 46}\" fill=\"#8fa3b2\" text-anchor=\"middle\" font-family=\"Arial, sans-serif\" font-size=\"11\">{EscapeXml(state.Kind.ToString())}{(state.RingScore > 0 ? $" R{state.RingScore}" : string.Empty)}</text>",
		};

		if (state.Pending)
		{
			lines.Add($"<circle cx=\"{cx}\" cy=\"{cy - 58}\" r=\"17\" fill=\"none\" stroke=\"#ffd84a\" stroke-width=\"3\"/>");
			lines.Add($"<circle cx=\"{cx}\" cy=\"{cy - 58}\" r=\"10\" fill=\"none\" stroke=\"#ffd84a\" stroke-width=\"3\"/>");
		}

		if (state.Flashing)
		{
			lines.Add($"<circle cx=\"{cx}\" cy=\"{cy - 58}\" r=\"30\" fill=\"none\" stroke=\"#fff1a6\" stroke-width=\"3\" stroke-dasharray=\"6 5\"/>");
		}

		lines.Add("</g>");
		return string.Join(Environment.NewLine, lines);
	}

	private static string BuildCaseStateText(EnergyVisualCheckCase testCase)
	{
		List<string> parts = new();
		for (int arm = 0; arm < EnergyMechanismVisualLogic.ArmCount; arm++)
		{
			EnergyMechanismArmDisplayState state = EnergyMechanismVisualLogic.ResolveArmState(testCase.TeamState, arm, testCase.GameTimeSec);
			parts.Add($"A{arm}:{state.Kind}{(state.RingScore > 0 ? $"({state.RingScore})" : string.Empty)}");
		}

		return string.Join("  ", parts);
	}

	private static object BuildEnergyVisualCheckReport(
		IReadOnlyList<EnergyVisualCheckCase> cases,
		IReadOnlyList<string> failures,
		string svgPath,
		string? pngPath)
		=> new
		{
			GeneratedAt = DateTimeOffset.Now,
			Svg = svgPath,
			Png = pngPath,
			Passed = failures.Count == 0,
			Failures = failures,
			Cases = cases.Select(testCase => new
			{
				testCase.Name,
				testCase.Title,
				testCase.GameTimeSec,
				Team = testCase.TeamState.Team,
				State = testCase.TeamState.EnergyMechanismState,
				Large = testCase.TeamState.EnergyLargeMechanismActive,
				LitMask = FormatEnergyMask(testCase.TeamState.EnergyCurrentLitMask),
				HitMask = FormatEnergyMask(EnergyMechanismVisualLogic.ResolveHitMask(testCase.TeamState, testCase.TeamState.EnergyLargeMechanismActive)),
				Arms = Enumerable.Range(0, EnergyMechanismVisualLogic.ArmCount)
					.Select(arm => EnergyMechanismVisualLogic.ResolveArmState(testCase.TeamState, arm, testCase.GameTimeSec))
					.Select(state => new
					{
						state.ArmIndex,
						Kind = state.Kind.ToString(),
						state.RingScore,
						state.Pending,
						state.Completed,
						state.Flashing,
					})
			})
		};

	private static string? TryRenderSvgWithChrome(string svgPath, string outputDirectory)
	{
		string? chrome = ResolveChromePath();
		if (string.IsNullOrWhiteSpace(chrome))
		{
			return null;
		}

		string pngPath = Path.Combine(outputDirectory, "energy_visual_check.png");
		var startInfo = new ProcessStartInfo
		{
			FileName = chrome,
			UseShellExecute = false,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
		};
		startInfo.ArgumentList.Add("--headless=new");
		startInfo.ArgumentList.Add("--no-sandbox");
		startInfo.ArgumentList.Add("--disable-gpu");
		startInfo.ArgumentList.Add($"--screenshot={pngPath}");
		startInfo.ArgumentList.Add("--window-size=1180,1230");
		startInfo.ArgumentList.Add(new Uri(svgPath).AbsoluteUri);

		using Process? process = Process.Start(startInfo);
		if (process is null)
		{
			return null;
		}

		if (!process.WaitForExit(15000))
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (InvalidOperationException)
			{
			}

			return null;
		}

		return process.ExitCode == 0 && File.Exists(pngPath) ? pngPath : null;
	}

	private static string? ResolveChromePath()
	{
		foreach (string candidate in new[] { "/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser" })
		{
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		return null;
	}

	private static string FormatEnergyMask(int mask)
	{
		List<string> arms = new();
		for (int index = 0; index < EnergyMechanismVisualLogic.ArmCount; index++)
		{
			if ((mask & (1 << index)) != 0)
			{
				arms.Add($"A{index}");
			}
		}

		return arms.Count == 0 ? "-" : string.Join(",", arms);
	}

	private static string EscapeXml(string value)
		=> value
			.Replace("&", "&amp;", StringComparison.Ordinal)
			.Replace("<", "&lt;", StringComparison.Ordinal)
			.Replace(">", "&gt;", StringComparison.Ordinal)
			.Replace("\"", "&quot;", StringComparison.Ordinal);

	private sealed record EnergyVisualCheckCase(
		string Name,
		string Title,
		double GameTimeSec,
		SimulationTeamState TeamState,
		IReadOnlyList<EnergyVisualExpectation> Expectations,
		IReadOnlyList<string>? ExtraAssertions = null);

	private readonly record struct EnergyVisualExpectation(
		int ArmIndex,
		EnergyMechanismArmDisplayKind Kind,
		bool Pending,
		bool Completed);
}
