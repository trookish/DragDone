using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DragDone.Core;
using DragDone.Localization;

namespace DragDone.UI;

/// <summary>First-run explanation: the three steps and the three keys, next to a live menu.</summary>
public sealed class WelcomeWindow : FluentWindow
{
    public WelcomeWindow(Action? dismissed)
    {
        Title = Loc.T("welcome.title");
        Width = 860;
        MinWidth = 760;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Build();
        Closed += (_, _) => dismissed?.Invoke();
    }

    /// <summary>Rebuilds the window in the current language.</summary>
    protected override void OnLanguageReloaded()
    {
        Title = Loc.T("welcome.title");
        Build();
    }

    void Build()
    {
        var grid = new Grid { Margin = new Thickness(28, 24, 28, 22) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        var heading = Heading(Loc.T("welcome.heading"), 26);
        // The heading must never be cut short: let it shrink to fit and wrap if it has to.
        heading.TextWrapping = TextWrapping.Wrap;
        heading.TextTrimming = TextTrimming.None;
        text.Children.Add(heading);
        text.Children.Add(Muted(Loc.T("welcome.lead"), 13.5));
        text.Children.Add(new Border { Height = 14 });

        var n = 1;
        foreach (var key in new[] { "welcome.step1", "welcome.step2", "welcome.step3" })
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var badge = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = Theme.Brush(Theme.Current.Accent),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = Loc.Num(n++),
                    Foreground = Theme.Brush(Theme.On(Theme.Current.Accent)),
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            row.Children.Add(badge);
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = Loc.T(key), FontSize = 14.5, FontWeight = FontWeights.SemiBold });
            body.Children.Add(Muted(Loc.T(key + ".desc")));
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            text.Children.Add(row);
        }

        var keys = new StackPanel { Margin = new Thickness(38, 0, 0, 14) };
        foreach (var (k, tab) in new[] { ("Shift", "tab.convert"), ("Ctrl", "tab.tools"), ("Alt", "tab.actions") })
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var cap = KeyCap(k);
            cap.MinWidth = 50;
            r.Children.Add(cap);
            r.Children.Add(new TextBlock { Text = Loc.T(tab), VerticalAlignment = VerticalAlignment.Center });
            keys.Children.Add(r);
        }
        text.Children.Add(keys);
        text.Children.Add(Muted(Loc.T("welcome.tray"), 12));

        var ok = AccentButton(Loc.T("welcome.ok"), (_, _) => Close());
        ok.HorizontalAlignment = HorizontalAlignment.Left;
        ok.Margin = new Thickness(0, 16, 0, 0);
        ok.IsDefault = true;
        text.Children.Add(ok);
        grid.Children.Add(text);

        // A real menu, rendered by the same control the overlay uses.
        var demo = new RadialMenu { IsHitTestVisible = false, Width = 300, Height = 300 };
        var sample = new DragPayload(new[] { Path.Combine(Path.GetTempPath(), "photo.heic") });
        demo.Items = ActionCatalog.Build(sample, MenuTab.Convert, Capabilities.All);
        demo.SetHub(Loc.KindName(FileKind.Image, 1), Loc.Size(2_400_000), 1);
        demo.HoverIndex = 1;
        var holder = new Border
        {
            Child = demo,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0),
        };
        Grid.SetColumn(holder, 1);
        grid.Children.Add(holder);

        Content = grid;
    }
}
