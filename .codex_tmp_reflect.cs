using System;
using System.Linq;
using System.Reflection;
class Program
{
    static void Main()
    {
        var asm1 = Assembly.LoadFrom(Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\\.nuget\\packages\\bepuutilities\\2.4.0\\lib\\net6.0\\BepuUtilities.dll"));
        var asm2 = Assembly.LoadFrom(Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\\.nuget\\packages\\bepuphysics\\2.4.0\\lib\\net6.0\\BepuPhysics.dll"));
        var simulation = asm2.GetType("BepuPhysics.Simulation");
        foreach (var m in simulation.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Where(m => m.Name == "RayCast" || m.Name == "Create"))
            Console.WriteLine(m);
        var statics = asm2.GetType("BepuPhysics.Statics");
        foreach (var m in statics.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == "Add" || m.Name == "ApplyDescription" || m.Name == "Remove"))
            Console.WriteLine(m);
        var shapes = asm2.GetType("BepuPhysics.Collidables.Shapes");
        foreach (var m in shapes.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == "Add" || m.Name == "RemoveAndDispose"))
            Console.WriteLine(m);
    }
}
