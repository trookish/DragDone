using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DragDone.Localization;
using DragDone.Processors;

namespace DragDone.UI;

/// <summary>Inline rename: the name is pre-selected without its extension, as in Explorer.</summary>
public sealed class RenameWindow : FluentWindow
{
    readonly TextBox _box = new() { MinWidth = 320, FontSize = 14, Padding = new Thickness(6, 4, 6, 5) };
    readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.LightSalmon, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
    readonly TextBlock _label = new();
    readonly string _path;
    Button? _okButton;
    Button? _cancelButton;

    public string? NewName { get; private set; }

    public RenameWindow(string path)
    {
        _path = path;
        var name = Path.GetFileName(path.TrimEnd('\\'));
        Title = Loc.T("rename.title");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        _box.Text = name;
        // File names are mostly Latin or mixed; let the text decide its own direction.
        _box.FlowDirection = DocumentOps.FirstStrongIsRtl(name) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _box.TextChanged += (_, _) => Validate();

        var ok = AccentButton(Loc.T("rename.ok"), (_, _) => Apply());
        ok.IsDefault = true;
        var cancel = PlainButton(Loc.T("common.cancel"), (_, _) => Close());
        cancel.Margin = new Thickness(8, 0, 0, 0);
        _okButton = ok;
        _cancelButton = cancel;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(_label);
        root.Children.Add(new Border { Height = 6 });
        root.Children.Add(_box);
        root.Children.Add(_error);
        root.Children.Add(buttons);
        Content = root;
        _label.Text = Loc.T("rename.label");

        Loaded += (_, _) =>
        {
            if (ShowActivated) Activate();
            _box.Focus();
            var stem = Directory.Exists(path) ? name.Length : Path.GetFileNameWithoutExtension(name).Length;
            _box.Select(0, stem);
            Keyboard.Focus(_box);
        };
    }

    /// <summary>Re-writes the labels that were placed in the current language.</summary>
    protected override void OnLanguageReloaded()
    {
        Title = Loc.T("rename.title");
        _label.Text = Loc.T("rename.label");
        if (_okButton is not null) _okButton.Content = Loc.T("rename.ok");
        if (_cancelButton is not null) _cancelButton.Content = Loc.T("common.cancel");
        Validate();
    }

    bool Validate()
    {
        var text = _box.Text.Trim();
        string? problem = null;
        if (text.Length == 0) problem = Loc.T("rename.empty");
        else if (text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) problem = Loc.T("rename.invalid");
        else if (!string.Equals(text, Path.GetFileName(_path.TrimEnd('\\')), StringComparison.OrdinalIgnoreCase))
        {
            var target = Path.Combine(Path.GetDirectoryName(_path.TrimEnd('\\'))!, text);
            if (File.Exists(target) || Directory.Exists(target)) problem = Loc.T("rename.exists");
        }
        _error.Text = problem ?? "";
        _error.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        return problem is null;
    }

    void Apply()
    {
        if (!Validate()) return;
        NewName = _box.Text.Trim();
        DialogResult = true;
    }
}
