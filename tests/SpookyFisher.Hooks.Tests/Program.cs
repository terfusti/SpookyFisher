using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

internal static class Program
{
    private static Assembly game;
    private static string gameDirectory;

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: SpookyFisher.Hooks.Tests.exe <SpookyFisher.Managed.dll> <Terraria.exe>");
            return 1;
        }
        var harmony = new Harmony("spookyfisher.hook.tests");
        AppDomain.CurrentDomain.AssemblyResolve += ResolveGameDependency;
        try
        {
            // Patch only the assemblies loaded into this isolated test process. Never start
            // Terraria or the plugin runtime, and never attach to the running game.
            gameDirectory = Path.GetDirectoryName(Path.GetFullPath(args[1]));
            game = Assembly.LoadFrom(Path.GetFullPath(args[1]));
            var managed = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            harmony.PatchAll(managed);
            var patched = harmony.GetPatchedMethods().ToArray();
            string[] expected = { "Update", "Draw", "ItemCheck", "MouseInput", "UpdateInput",
                "GetInputText", "DrawCursor", "DrawThickCursor" };
            foreach (string name in expected)
                Assert(patched.Any(method => method.Name == name), "Missing patch: " + name);

            var mouseInput = patched.OfType<MethodInfo>().Single(method => method.Name == "MouseInput");
            Assert(mouseInput.ReturnType == typeof(void), "Installed MouseInput signature changed; review the mouse hook.");
            var postfix = managed.GetType("SpookyFisher.MenuMouseInputHook")
                .GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);
            Assert(!postfix.GetParameters().Any(parameter => parameter.Name == "__result"),
                "The void MouseInput method cannot supply a Harmony __result parameter.");
            Console.WriteLine("PASS all " + patched.Length + " Harmony hooks install against Terraria " + game.GetName().Version);
            harmony.UnpatchAll(harmony.Id);
            Assert(!harmony.GetPatchedMethods().Any(), "Test hooks remained installed after cleanup.");
            Console.WriteLine("PASS void MouseInput postfix and hook cleanup");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveGameDependency;
        }
    }

    private static Assembly ResolveGameDependency(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name;
        if (game == null) return null;
        string resource = game.GetManifestResourceNames().FirstOrDefault(value =>
            value.EndsWith("." + name + ".dll", StringComparison.OrdinalIgnoreCase));
        if (resource != null)
        {
            using (var stream = game.GetManifestResourceStream(resource))
            using (var bytes = new MemoryStream())
            {
                stream.CopyTo(bytes);
                return Assembly.Load(bytes.ToArray());
            }
        }
        string path = Path.Combine(gameDirectory, name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
