using Simulator.Assets;
using Simulator.Core;
var layout = new ProjectLayout(@"e:\Artinx\260111new\Simulator");
var svc = new MapPresetService();
var preset = svc.LoadPreset(layout, "rmuc2026");
foreach (var facility in preset.Facilities.Where(f => f.Type is "base" or "outpost" or "energy_mechanism"))
{
    Console.WriteLine($"{facility.Id}|{facility.Type}|{facility.Team}|{facility.X1:0.##},{facility.Y1:0.##},{facility.X2:0.##},{facility.Y2:0.##}|h={facility.HeightM:0.###}");
}
