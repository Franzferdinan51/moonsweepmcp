// interop-gen (stage B): generate BepInEx Il2CppInterop assemblies for
// Moon Sweepers (metadata v108) from Cpp2IL dummy DLLs produced by the
// standalone dev Cpp2IL (which parses v108; BepInEx be.791's bundled
// LibCpp2IL caps at metadata 106, so in-game generation can never work
// for this game). Output goes to BepInEx/interop + assembly-hash.txt, and
// BepInEx.cfg gets UpdateInteropAssemblies=false, so the game loads these
// assemblies instead of trying (and failing) to regenerate them.
//
// Known delta vs BepInEx's own pipeline: the AttributeInjector processing
// layer runs inside Cpp2IL's analysis context in the official flow; the
// standalone dummy DLLs don't carry its injections. Basic interop is
// unaffected; if a specific attribute-driven feature misbehaves, that's
// the first place to look.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AsmResolver.DotNet;
using Cpp2IL.Core;
using Il2CppInterop.Common;
using Il2CppInterop.Generator;
using Il2CppInterop.Generator.Runners;
using Microsoft.Extensions.Logging;

internal static class Program
{
    private static void Main(string[] args)
    {
        var gameRoot = args.Length > 0
            ? args[0]
            : @"C:\Program Files (x86)\Steam\steamapps\common\Moon Sweepers";
        var dummyDir = args.Length > 1
            ? args[1]
            : @"work\interop-test";
        var gaPath = Path.Combine(gameRoot, "GameAssembly.dll");
        var bepRoot = Path.Combine(gameRoot, "BepInEx");
        var interopDir = Path.Combine(bepRoot, "interop");
        var unityLibsDir = Path.Combine(bepRoot, "unity-libs");

        var assemblies = new List<AssemblyDefinition>();
        foreach (var file in Directory.GetFiles(dummyDir, "*.dll"))
        {
            assemblies.Add(AssemblyDefinition.FromFile(file));
        }
        Console.WriteLine("Loaded " + assemblies.Count + " Cpp2IL dummy assemblies from " + dummyDir);

        Directory.CreateDirectory(interopDir);
        var opts = new GeneratorOptions
        {
            GameAssemblyPath = gaPath, // ScanMethodRefs defaults to true on x64
            Source = assemblies,
            OutputDir = interopDir,
            UnityBaseLibsDir = Directory.Exists(unityLibsDir) ? unityLibsDir : null,
        };

        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        Console.WriteLine("Running Il2CppInterop generator -> " + interopDir);
        Il2CppInteropGenerator.Create(opts)
            .AddLogger(loggerFactory.CreateLogger("Il2CppInteropGen"))
            .AddInteropAssemblyGenerator()
            .Run();

        var count = Directory.GetFiles(interopDir, "*.dll").Length;
        Console.WriteLine("Interop assemblies written: " + count);

        // Replicate Il2CppInteropManager.ComputeHash so BepInEx accepts the set.
        var hash = ComputeHash(gaPath, unityLibsDir, bepRoot);
        File.WriteAllText(Path.Combine(interopDir, "assembly-hash.txt"), hash);
        Console.WriteLine("assembly-hash.txt: " + hash);
    }

    private static string ComputeHash(string gaPath, string unityLibsDir, string bepRoot)
    {
        using var md5 = MD5.Create();

        void HashFile(string file)
        {
            using var fs = File.OpenRead(file);
            var buffer = new byte[81920];
            int read;
            while ((read = fs.Read(buffer)) > 0)
                md5.TransformBlock(buffer, 0, read, buffer, 0);
        }

        void HashString(string str)
        {
            var buffer = Encoding.UTF8.GetBytes(str);
            md5.TransformBlock(buffer, 0, buffer.Length, buffer, 0);
        }

        HashFile(gaPath);

        if (Directory.Exists(unityLibsDir))
            foreach (var file in Directory.EnumerateFiles(unityLibsDir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                HashString(Path.GetFileName(file));
                HashFile(file);
            }

        var renameMap = Path.Combine(bepRoot, "DeobfuscationMap.csv.gz");
        if (File.Exists(renameMap))
            HashFile(renameMap);

        HashString(typeof(InteropAssemblyGenerator).Assembly.GetName().Version.ToString());
        HashString(typeof(Cpp2IlApi).Assembly.GetName().Version.ToString());

        md5.TransformFinalBlock(new byte[0], 0, 0);
        return BitConverter.ToString(md5.Hash).Replace("-", "").ToLowerInvariant();
    }
}
