using Godot;

namespace STS2MobileIos.Patches;

/// <summary>
/// Minimal safe bootstrap patch set for building without proprietary game DLLs.
/// The full mobile patch set stays available in builds with local game references.
/// Runtime Harmony still applies every patch and the original ModManager hooks.
/// </summary>
public static class PlatformPatches
{
    public static bool InitializePlatformPrefix(ref Task<bool> __result)
    {
        __result = Task.FromResult(true);
        return false;
    }

    public static bool LogSystemInfoPrefix(ref Task __result)
    {
        __result = Task.CompletedTask;
        return false;
    }

    public static bool SkipPrefix() => false;

    public static bool ReturnFalsePrefix(ref bool __result)
    {
        __result = false;
        return false;
    }

    // GodotFileIo.ResolvePath is an instance method on the downloaded game's
    // class. Use reflection rather than a compile-time game type, while keeping
    // the same full-path check as the original tested iPad patch.
    public static bool CreateDirectoryPrefix(object __instance, string directoryPath)
    {
        if (__instance == null) return false;
        var candidate = __instance.GetType()
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "GetFullPath" &&
                method.ReturnType == typeof(string) && method.GetParameters().Length == 1 &&
                method.GetParameters()[0].ParameterType == typeof(string));
        if (candidate == null) return false;
        try
        {
            string? path = candidate.Invoke(__instance, [directoryPath]) as string;
            return path?.Contains("://", StringComparison.Ordinal) == true;
        }
        catch (System.Reflection.TargetInvocationException) { return false; }
    }
}

public static class GameHooks
{
    public static void EnterTreePostfix(Node __instance) => MobileUi.Install(__instance);

    public static void ReadyPostfix(Node __instance)
    {
        try { NativeBridge.ActivateAudio(); }
        catch (Exception) { /* Audio activation is optional, not a boot blocker. */ }
    }
}
