using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace STS2SteamBootstrap;

/// <summary>
/// Adapt the exact supported Steam Harmony binary *after* it has been downloaded
/// and verified. The original stays on disk; this copy calls the app's patched
/// Mono executable-page alias backend when a mod applies a Harmony detour.
/// The transformation matches STS2JitPrepare's tested Harmony 2.4.2.0 adapter.
/// </summary>
public static class HarmonyAdapter
{
    private const string KnownHash = "ef1898322c9f5c86dc1b0758b272a9c440823b4a41ca9a0b82a3aa6b3d206387";

    public static void Adapt(string original, string supportDll, string destination)
    {
        if (Path.GetFullPath(original) == Path.GetFullPath(destination))
            throw new ArgumentException("Never replace the Steam original.");
        using (var input = File.OpenRead(original))
            if (!Convert.ToHexStringLower(SHA256.HashData(input)).Equals(KnownHash, StringComparison.Ordinal))
                throw new NotSupportedException("The downloaded game uses an untested Harmony build. Refusing to patch it.");
        if (!File.Exists(supportDll))
            throw new FileNotFoundException("The launcher JIT support library is not installed.", supportDll);

        using var assembly = AssemblyDefinition.ReadAssembly(original);
        if (assembly.Name.Version != new Version(2, 4, 2, 0))
            throw new NotSupportedException("The JIT detour backend supports Harmony 2.4.2.0 only.");
        var module = assembly.MainModule;
        MethodDefinition Method(string type, string name) =>
            module.GetType(type).Methods.Single(m => m.Name == name);
        ILProcessor Replace(MethodDefinition method)
        {
            method.Body = new Mono.Cecil.Cil.MethodBody(method);
            return method.Body.GetILProcessor();
        }

        const string system = "MonoMod.Core.Platforms.Systems.MacOSSystem";
        int arm64 = (int)module.GetType("MonoMod.Utils.ArchitectureKind")
            .Fields.Single(f => f.Name == "Arm64").Constant!;
        var il = Replace(Method("MonoMod.Utils.PlatformDetection", "get_Architecture"));
        il.Emit(OpCodes.Ldc_I4, arm64);
        il.Emit(OpCodes.Ret);
        il = Replace(Method("MonoMod.Core.Platforms.PlatformTriple", "CreateCurrentSystem"));
        il.Emit(OpCodes.Newobj, Method(system, ".ctor"));
        il.Emit(OpCodes.Ret);
        il = Replace(Method(system, "get_NativeExceptionHelper"));
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        using var support = AssemblyDefinition.ReadAssembly(supportDll);
        var patch = support.MainModule.GetType("STS2JitSupport.NativePatch")
            .Methods.Single(m => m.Name == "PatchData");
        il = Replace(Method(system, "PatchData"));
        for (int i = 1; i <= 4; i++) il.Emit(OpCodes.Ldarg, i);
        il.Emit(OpCodes.Call, module.ImportReference(patch));
        il.Emit(OpCodes.Ret);

        var fix = support.MainModule.GetType("STS2JitSupport.SignatureFix")
            .Methods.Single(m => m.Name == "PreserveReturnModifiers");
        var importer = module.GetType("MonoMod.Utils.MMReflectionImporter").Methods.Single(m =>
            m.Name == "_ImportReference" &&
            m.Parameters[0].ParameterType.FullName == "System.Reflection.MethodBase");
        var setter = importer.Body.Instructions.Single(i =>
            i.Operand is MethodReference called && called.Name == "set_ReturnType");
        var processor = importer.Body.GetILProcessor();
        Instruction cursor = setter;
        void Append(Instruction instruction)
        {
            processor.InsertAfter(cursor, instruction);
            cursor = instruction;
        }
        Append(Instruction.Create(OpCodes.Ldarg_0));
        Append(Instruction.Create(OpCodes.Ldarg_1));
        Append(Instruction.Create(OpCodes.Ldloc,
            importer.Body.Variables.First(v => v.VariableType.FullName == "Mono.Cecil.MethodReference")));
        Append(Instruction.Create(OpCodes.Call, module.ImportReference(fix)));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        string tmp = destination + ".preparing";
        try
        {
            assembly.Write(tmp);
            File.Move(tmp, destination, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
