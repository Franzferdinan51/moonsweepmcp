// interop-gen10: full-fidelity BepInEx interop generation for Moon Sweepers
// (metadata v108), replicating BepInEx be.791's Il2CppInteropManager pipeline
// step for step — Cpp2IlApi init, AttributeInjectorProcessingLayer, assembly
// build, Il2CppInterop generator — but running under .NET 10 with the dev
// Cpp2IL family, whose LibCpp2IL parses v108 (be.791's bundled net6 build
// caps at metadata 106, so the in-game generation can never succeed).
// Output: BepInEx/interop + assembly-hash.txt (same hash recipe as the
// manager's ComputeHash; BepInEx.cfg also pins UpdateInteropAssemblies=false).
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AssetRipper.Primitives;
using Cpp2IL.Core;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;
using Il2CppInterop.Common;
using Il2CppInterop.Generator;
using Il2CppInterop.Generator.Runners;
using LibCpp2IL;
using Microsoft.Extensions.Logging;

internal static class Program
{
    private static void Main(string[] args)
    {
        var gameRoot = args.Length > 0
            ? args[0]
            : @"C:\Program Files (x86)\Steam\steamapps\common\Moon Sweepers";
        var gaPath = Path.Combine(gameRoot, "GameAssembly.dll");
        var metaPath = Path.Combine(gameRoot, "MoonSweepers_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        var bepRoot = Path.Combine(gameRoot, "BepInEx");
        var interopDir = Path.Combine(bepRoot, "interop");
        var unityLibsDir = Path.Combine(bepRoot, "unity-libs");

        // Same registrations as BepInEx's Il2CppInteropManager static ctor.
        InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_32);
        InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_64);
        LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport();

        Console.WriteLine("Initializing LibCpp2IL (dev build, metadata v108)...");
        Cpp2IlApi.InitializeLibCpp2Il(gaPath, metaPath, new UnityVersion(6000, 6, 0), false);
        Console.WriteLine("LibCpp2IL initialized.");

        var layers = new List<Cpp2IlProcessingLayer> { new AttributeInjectorProcessingLayer() };
        foreach (var layer in layers) layer.PreProcess(Cpp2IlApi.CurrentAppContext, layers);
        foreach (var layer in layers) layer.Process(Cpp2IlApi.CurrentAppContext);
        Console.WriteLine("AttributeInjector layer applied.");

        Console.WriteLine("Building source assemblies via Cpp2IL...");
        var assemblies = new AsmResolverDllOutputFormatDefault().BuildAssemblies(Cpp2IlApi.CurrentAppContext);
        Console.WriteLine("Source assemblies: " + assemblies.Count);

        LibCpp2IlMain.Reset();
        Cpp2IlApi.CurrentAppContext = null;

        // Stage 1 ends here: write the attribute-treated assemblies out for
        // stage 2 (the net6 harness with be.791's own AsmResolver), because
        // be.791's generator cannot run against the dev AsmResolver in-process
        // (interface drift -> TypeLoadException).
        var treatedDir = @"work\interop-treated";
        Directory.CreateDirectory(treatedDir);
        foreach (var old in Directory.GetFiles(treatedDir, "*.dll")) File.Delete(old);
        foreach (var asmDef in assemblies)
        {
            asmDef.Write(Path.Combine(treatedDir, asmDef.Name + ".dll"));
        }
        Console.WriteLine("Treated assemblies written: " + assemblies.Count + " -> " + treatedDir);
        return;

    }

    // Replica of Il2CppInteropManager.ComputeHash (MD5; GameAssembly, then
    // unity-libs name+bytes in enumeration order, rename map if present, then
    // the generator and Cpp2IL assembly version strings).
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
