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

    public static bool CreateDirectoryPrefix(string directoryPath) =>
        directoryPath.Contains("://", StringComparison.Ordinal);
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
