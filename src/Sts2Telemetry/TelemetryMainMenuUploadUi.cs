using Godot;

namespace Sts2Telemetry;

internal static class TelemetryMainMenuUploadUi
{
    private static CanvasLayer? _layer;
    private static PanelContainer? _panel;
    private static RichTextLabel? _body;
    private static Button? _copyButton;
    private static string _telemetryBaseDirectory = "";
    private static string _latestRedeemCode = "";

    public static bool Show(string telemetryBaseDirectory)
    {
        _telemetryBaseDirectory = telemetryBaseDirectory;

        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree)
                return false;

            if (_layer?.IsInsideTree() == true)
            {
                Refresh();
                return true;
            }

            _layer = new CanvasLayer
            {
                Name = "STS2TelemetryMainMenuUploadUi",
                Layer = 120
            };

            Control root = BuildRoot();
            _layer.AddChild(root);
            tree.Root.AddChild(_layer);
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Telemetry] main menu upload status UI unavailable: {ex.Message}");
            return false;
        }
    }

    public static void Hide()
    {
        try
        {
            _layer?.QueueFree();
        }
        catch
        {
        }
        finally
        {
            _layer = null;
            _panel = null;
            _body = null;
            _copyButton = null;
            _latestRedeemCode = "";
        }
    }

    private static Control BuildRoot()
    {
        var root = new Control
        {
            Name = "STS2TelemetryMainMenuUploadRoot",
            AnchorLeft = 0,
            AnchorTop = 0,
            AnchorRight = 1,
            AnchorBottom = 1,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        var button = new Button
        {
            Name = "STS2TelemetryMainMenuButton",
            Text = "上传记录",
            AnchorLeft = 1,
            AnchorTop = 0,
            AnchorRight = 1,
            AnchorBottom = 0,
            OffsetLeft = -164,
            OffsetTop = 24,
            OffsetRight = -24,
            OffsetBottom = 64,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        button.Pressed += TogglePanel;
        root.AddChild(button);

        _panel = BuildPanel();
        root.AddChild(_panel);
        return root;
    }

    private static PanelContainer BuildPanel()
    {
        var panel = new PanelContainer
        {
            Name = "STS2TelemetryUploadRewardPanel",
            AnchorLeft = 1,
            AnchorTop = 0,
            AnchorRight = 1,
            AnchorBottom = 0,
            OffsetLeft = -760,
            OffsetTop = 76,
            OffsetRight = -24,
            OffsetBottom = 604,
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false
        };

        var box = new VBoxContainer
        {
            Name = "STS2TelemetryUploadRewardPanelBody",
            CustomMinimumSize = new Vector2(712, 504)
        };

        var buttons = new HBoxContainer
        {
            Name = "STS2TelemetryUploadRewardPanelButtons"
        };

        var refresh = new Button
        {
            Text = "刷新",
            CustomMinimumSize = new Vector2(120, 36)
        };
        refresh.Pressed += Refresh;
        buttons.AddChild(refresh);

        _copyButton = new Button
        {
            Text = "复制兑换码",
            CustomMinimumSize = new Vector2(130, 36),
            Disabled = true
        };
        _copyButton.Pressed += CopyLatestRedeemCode;
        buttons.AddChild(_copyButton);

        var close = new Button
        {
            Text = "关闭",
            CustomMinimumSize = new Vector2(100, 36)
        };
        close.Pressed += () =>
        {
            if (_panel != null)
                _panel.Visible = false;
        };
        buttons.AddChild(close);
        box.AddChild(buttons);

        _body = new RichTextLabel
        {
            Name = "STS2TelemetryUploadRewardText",
            CustomMinimumSize = new Vector2(700, 452),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        box.AddChild(_body);
        panel.AddChild(box);
        return panel;
    }

    private static void TogglePanel()
    {
        if (_panel == null)
            return;

        _panel.Visible = !_panel.Visible;
        if (_panel.Visible)
            Refresh();
    }

    private static void Refresh()
    {
        if (_body == null || string.IsNullOrWhiteSpace(_telemetryBaseDirectory))
            return;

        try
        {
            TelemetryUploadStatusView view = TelemetryUploadStatusReader.Build(_telemetryBaseDirectory, maxRuns: 8);
            _body.Text = TelemetryUploadStatusRenderer.RenderPlainText(view);
            _latestRedeemCode = TelemetryUploadStatusRenderer.LatestGeneratedRedeemCode(view);
            if (_copyButton != null)
                _copyButton.Disabled = string.IsNullOrWhiteSpace(_latestRedeemCode);
        }
        catch (Exception ex)
        {
            _body.Text = $"遥测上传 / 奖励\n\n状态不可用：{ex.Message}";
            _latestRedeemCode = "";
            if (_copyButton != null)
                _copyButton.Disabled = true;
        }
    }

    private static void CopyLatestRedeemCode()
    {
        if (string.IsNullOrWhiteSpace(_latestRedeemCode))
            return;

        try
        {
            DisplayServer.ClipboardSet(_latestRedeemCode);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Telemetry] failed to copy redeem code: {ex.Message}");
        }
    }
}
