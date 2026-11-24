using Godot;

namespace STS2MobileIos;

/// <summary>
/// Minimal game UI used by the public no-game-at-build-time JIT variant.
/// The full local Mac build retains the complete mobile UI and features.
/// Both variants keep the original game's dynamic Mono/Harmony mod loader.
/// </summary>
public static class MobileUi
{
    private static bool _installed;

    public static void Install(Node game)
    {
        if (_installed) return;
        _installed = true;
        Callable.From(() =>
        {
            var layer = new CanvasLayer { Layer = 100, ProcessMode = Node.ProcessModeEnum.Always };
            game.AddChild(layer);
            var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            layer.AddChild(root);
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var button = new Button { Text = "JIT", CustomMinimumSize = new Vector2(76, 44) };
            root.AddChild(button);
            button.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
            button.OffsetLeft = -92;
            button.OffsetRight = -16;
            button.OffsetTop = 96;
            button.OffsetBottom = 140;
            var panel = new PanelContainer { Visible = false };
            root.AddChild(panel);
            panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
            panel.OffsetLeft = -370;
            panel.OffsetRight = -16;
            panel.OffsetTop = 150;
            panel.OffsetBottom = 300;
            var label = new Label
            {
                Text = "StS2 iOS Mono JIT\n\nManaged mods: Documents/mods\nEnable JIT on every restart.",
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            panel.AddChild(label);
            button.Pressed += () => panel.Visible = !panel.Visible;
        }).CallDeferred();
    }

    public static void SetSuspended(bool suspended) { }
}
