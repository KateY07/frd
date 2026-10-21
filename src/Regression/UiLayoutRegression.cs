using System.Drawing;
using System.Text;

namespace Frd;

/// <summary>
/// Headless layout check for the floating panels: lays them out for realistic window sizes and states and reports controls that overlap
/// each other or stick out of the card. Runs without a window, so it also works over SSH or in a service session.
/// </summary>
static class UiLayoutRegression
{
    static bool Overlaps(RectangleF a, RectangleF b) => RectangleF.Intersect(a, b) is { Width: > 1, Height: > 1 };

    static IEnumerable<string> Check(string scenario, UiGlassPanel panel, IEnumerable<UiElement> elements)
    {
        var visible = elements.Where(element => element.IsVisible && element.Bounds.Width > 0).ToArray();
        var card = panel.CurrentRect;
        foreach (var element in visible)
            if (element.Bounds.Left < card.Left - .5f || element.Bounds.Right > card.Right + .5f || element.Bounds.Top < card.Top - .5f || element.Bounds.Bottom > card.Bottom + .5f)
                yield return $"{scenario}: {Describe(element)} {element.Bounds} sticks out of the card {card}";
        for (var i = 0; i < visible.Length; i++)
            for (var j = i + 1; j < visible.Length; j++)
                if (Overlaps(visible[i].Bounds, visible[j].Bounds))
                    yield return $"{scenario}: {Describe(visible[i])} {visible[i].Bounds} overlaps {Describe(visible[j])} {visible[j].Bounds}";
    }

    static string Describe(UiElement element) => element switch
    {
        UiButton button => $"button '{(button.Label.Length > 0 ? button.Label : button.Icon)}'",
        UiLabel label => $"label '{label.Text}'",
        UiChoice => "dropdown",
        UiSlider => "slider",
        UiSwatch => "swatch",
        _ => element.GetType().Name
    };

    public static int Run(string reportPath)
    {
        UiIcons.Register();
        var problems = new List<string>();
        var checks = 0;
        using var devices = new UiDevices();
        var text = new UiText(devices.DWrite);
        var gate = new object();

        foreach (var english in new[] { false, true })
        {
        Loc.English = english; var lang = english ? "en" : "zh";
        // ---- controller panel
        foreach (var width in new[] { 1280, 900, 680, 520 })
            foreach (var more in new[] { false, true })
                foreach (var annotate in new[] { false, true })
                    foreach (var listOpen in new[] { false, true })
                    {
                        if (listOpen && !more) continue;
                        var panel = new UiControlPanel(gate);
                        panel.StatusName.Text = "192.168.100.200"; panel.Latency.Text = "123.4 ms";
                        panel.Operation.Text = Loc.T("已连接：mf_h264 / 2.5 Mbps / 原始分辨率 2560×1440");
                        panel.BitrateLabel.Text = Loc.T("码率上限 · 2.5 Mbps");
                        foreach (var name in new[] { Loc.T("H.264 / Windows 内置 · 硬件优先"), Loc.T("H.264 / Windows 内置 · 软件"), Loc.T("H.264 / FFmpeg · libx264rgb 低延迟") })
                            panel.Presets.Items.Add(new UiChoiceItem(name, name, true, null));
                        panel.Presets.SelectedIndex = 0;
                        panel.Input.IsEnabled = panel.Clipboard.IsEnabled = panel.Annotate.IsEnabled = true;
                        panel.Input.IsChecked = panel.Clipboard.IsChecked = true;
                        panel.More.IsChecked = more; panel.Annotate.IsChecked = annotate; panel.Presets.IsOpen = listOpen;
                        panel.Layout(text, width, 800);
                        checks++;
                        problems.AddRange(Check($"{lang} controller w={width} more={more} annotate={annotate} list={listOpen}", panel, panel.AllElements));
                    }

        // ---- host panel
        foreach (var controlled in new[] { false, true })
            foreach (var more in new[] { false, true })
                foreach (var install in new[] { false, true })
                    foreach (var transfer in new[] { false, true })
                    {
                        var panel = new UiHostPanel(gate) { PanelFraction = new(1, 1), OrbFraction = new(1, 1) };
                        panel.ListenValue.Text = "169.254.73.74:5204, [fe80::e3db:feec:10a2:bd9f]:5204";
                        panel.Status.Text = controlled ? Loc.T("被 169.254.73.74 控制") : Loc.T("等待主控");
                        panel.Controlled = controlled; panel.More.IsChecked = more;
                        panel.SetInstallVisible(install);
                        if (transfer) panel.Transfer = new ClipboardTransferProgress(false, 123_000_000, 400_000_000, 2, 3, "a-fairly-long-file-name.bin");
                        panel.Layout(text, 384, 300);
                        checks++;
                        problems.AddRange(Check($"{lang} host controlled={controlled} more={more} install={install} transfer={transfer}", panel, panel.AllElements));
                    }
        }
        Loc.English = false;

        var report = new StringBuilder();
        report.AppendLine($"{{\"Passed\": {(problems.Count == 0 ? "true" : "false")}, \"Scenarios\": {checks}, \"Problems\": {problems.Count}, \"Details\": [");
        report.AppendLine(string.Join(",\n", problems.Distinct().Take(200).Select(p => "  \"" + p.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")));
        report.AppendLine("]}");
        var path = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, report.ToString());
        Console.WriteLine($"UI layout: {checks} scenarios, {problems.Distinct().Count()} distinct problems. Report: {path}");
        foreach (var problem in problems.Distinct().Take(25)) Console.WriteLine("  " + problem);
        return problems.Count == 0 ? 0 : 1;
    }
}
