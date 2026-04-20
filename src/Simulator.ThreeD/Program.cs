using System.Windows.Forms;

namespace Simulator.ThreeD;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Simulator3dOptions options = Simulator3dOptions.Parse(args);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Form form = CreateEntryForm(options);
        Application.Run(form);
    }

    private static Form CreateEntryForm(Simulator3dOptions options)
    {
        return options.OpenEditor switch
        {
            "appearance" => new AppearanceEditorForm(),
            "terrain" => new TerrainEditorForm(),
            "rules" => new RuleEditorForm(),
            "behavior" => new BehaviorEditorForm(),
            "functional" => new FunctionalEditorForm(),
            _ => new Simulator3dForm(options),
        };
    }
}
