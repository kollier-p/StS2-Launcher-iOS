#!/usr/bin/env python3
"""Fail CI if the launcher migration removes the JIT mod-patching contract.

These checks intentionally cover the native executable-memory backend, the
managed Harmony interception path, and the user-controlled mod directory.
They are source-contract checks, not a substitute for a real iOS JIT test.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]


def read(path: str) -> str:
    return (ROOT / path).read_text()


class JitModContract(unittest.TestCase):
    def test_native_patch_entrypoint_and_writable_alias(self):
        source = read("ios/jit/RuntimeHost.mm")
        self.assertIn('sts2_jit_patch(', source)
        self.assertIn('sts2_jit_writable(target, size)', source)
        self.assertIn('sys_icache_invalidate(target, size)', source)
        self.assertIn('memcpy(alias, source, size)', source)
        self.assertIn('CheckJitPermission()', source)
        self.assertIn('coreclr_create_delegate(', source)
        self.assertIn('"STS2Jit", "STS2Jit.Entry", "Start"', source)

    def test_runtime_keeps_modification_capable_jit(self):
        source = read("scripts/ios/jit.py")
        self.assertIn('MonoJitMemory', source)
        self.assertIn('RuntimeHost.mm', source)
        self.assertIn('JitGate.mm', source)
        self.assertIn('STS2Jit.dll', source)

    def test_harmony_is_not_removed_from_game_boot(self):
        source = read("src/STS2Jit/Entry.cs")
        self.assertIn('new Harmony(', source)
        self.assertIn('harmony.Patch(', source)
        self.assertIn('nameof(ModManager.Initialize)', source)
        self.assertIn('"ReadModsInDirRecursive"', source)
        self.assertIn('InitializeMods', source)
        self.assertIn('ScanMods', source)
        self.assertIn('Path.Combine(OS.GetUserDataDir(), "mods")', source)

    def test_mods_remain_user_controlled_and_boot_checks_jit(self):
        gate = read("ios/jit/JitGate.mm")
        self.assertIn('@"mods"', gate)
        self.assertIn('csops(getpid()', gate)
        self.assertIn('0x10000000', gate)
        self.assertIn('originalLaunch(', gate)

    def test_game_assembly_does_not_get_published_before_jit_hooks(self):
        host = read("ios/jit/RuntimeHost.mm")
        initialize = host.index('"GodotPlugins.Game.Main", "InitializeFromGameProject"')
        hooks = host.index('"STS2Jit", "STS2Jit.Entry", "Start"')
        self.assertLess(initialize, hooks)
        self.assertRegex(host, re.compile(r'return result == 0 && start && start\(\) == 0;'))


if __name__ == "__main__":
    unittest.main()
