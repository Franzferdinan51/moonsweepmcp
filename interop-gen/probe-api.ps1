$core = "C:\Program Files (x86)\Steam\steamapps\common\Moon Sweepers\BepInEx\core"
$asm = [Reflection.Assembly]::LoadFrom("$core\Cpp2IL.Core.dll")
"--- Cpp2IL.Core types of interest ---"
$asm.GetExportedTypes() | Where-Object { $_.Name -match "InstructionSet|ProcessingLayer|Cpp2IlApi|OutputFormat" } | ForEach-Object { $_.FullName }
"--- LibCpp2IL types of interest ---"
$l = [Reflection.Assembly]::LoadFrom("$core\LibCpp2IL.dll")
$l.GetExportedTypes() | Where-Object { $_.Name -match "InstructionSet|BinaryRegistry|UnityVersion" } | ForEach-Object { $_.FullName }
"--- AssetRipper.Primitives UnityVersion ---"
$a = [Reflection.Assembly]::LoadFrom("$core\AssetRipper.Primitives.dll")
$a.GetExportedTypes() | Where-Object { $_.Name -match "UnityVersion" } | ForEach-Object { $_.FullName }
"--- Cpp2IlApi.Initialize* signatures ---"
$t = $asm.GetType("Cpp2IL.Core.Cpp2IlApi")
$t.GetMethods() | Where-Object { $_.Name -match "Initialize" } | ForEach-Object { $_.ToString() }
"--- AttributeInjectorProcessingLayer base chain ---"
$al = $asm.GetType("Cpp2IL.Core.ProcessingLayers.AttributeInjectorProcessingLayer")
$cur = $al
while ($cur) { $cur.FullName; $cur = $cur.BaseType }
"--- AsmResolverDllOutputFormatDefault ---"
$asm.GetExportedTypes() | Where-Object { $_.Name -match "AsmResolver" } | ForEach-Object { $_.FullName }
