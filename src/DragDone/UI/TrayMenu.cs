using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace DragDone.UI;

/// <summary>
/// The notification-area menu. A ContextMenu is a popup, so it sits outside every window and
/// inherits none of the app's colours: it is dressed from the live palette here instead, and
/// laid out in the reading direction of the current language.
/// </summary>
public static class TrayMenu
{
    /// <summary>Dresses a menu built by the caller: colours, type, and the item template.</summary>
    public static void Apply(ContextMenu menu, bool rtl)
    {
        var palette = Theme.Current;
        menu.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        menu.Background = Theme.Brush(palette.SurfaceAlt);
        menu.Foreground = Theme.Brush(palette.Text);
        menu.BorderBrush = Theme.Brush(palette.Stroke);
        menu.BorderThickness = new Thickness(1);
        menu.Padding = new Thickness(4);
        menu.MinWidth = 224;
        menu.FontFamily = Theme.UiFont;
        menu.FontSize = 13;
        menu.HasDropShadow = true;

        // The template reaches for these by name, so the same menu can be re-dressed later.
        menu.Resources["TraySurface"] = Theme.Brush(palette.SurfaceAlt);
        menu.Resources["TrayStroke"] = Theme.Brush(palette.Stroke);
        menu.Resources["TrayText"] = Theme.Brush(palette.Text);
        menu.Resources["TrayDim"] = Theme.Brush(palette.TextDim);
        menu.Resources["TrayAccent"] = Theme.Brush(palette.Accent);
        menu.Resources["TrayHover"] = Theme.Brush(Color.FromArgb(0x2E, palette.Accent.R, palette.Accent.G, palette.Accent.B));

        // An implicit style reaches the submenu items too, which is why it is not an
        // ItemContainerStyle: those stop at the first level.
        menu.Resources[typeof(MenuItem)] = ItemStyle(rtl);
        menu.Resources[typeof(Separator)] = SeparatorStyle();
    }

    static Style ItemStyle(bool rtl)
    {
        // The submenu opens towards the reading direction, and its arrow points that way.
        var xaml = Template
            .Replace("__PLACEMENT__", rtl ? "Left" : "Right")
            .Replace("__ARROW__", rtl ? "\uE76B" : "\uE76C");
        return (Style)XamlReader.Parse(xaml);
    }

    static Style SeparatorStyle() => new(typeof(Separator))
    {
        Setters =
        {
            new Setter(FrameworkElement.MarginProperty, new Thickness(6, 3, 6, 3)),
            new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(Line)),
        },
    };

    /// <summary>
    /// One row: a tick column, the label, and an arrow for a row that opens a submenu. Colours
    /// come from the menu's resources so a palette change can re-dress an open menu.
    /// </summary>
    const string Template = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="MenuItem">
            <Setter Property="Foreground" Value="{DynamicResource TrayText}" />
            <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI, Tahoma" />
            <Setter Property="FontSize" Value="13" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="MenuItem">
                        <Grid>
                            <Border x:Name="Shell" Background="Transparent" CornerRadius="5" Margin="2,1,2,1" Padding="9,6,11,7">
                                <Grid>
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="20" />
                                        <ColumnDefinition Width="*" />
                                        <ColumnDefinition Width="14" />
                                    </Grid.ColumnDefinitions>
                                    <TextBlock x:Name="Tick" Grid.Column="0" Text="&#xE73E;"
                                               FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="11"
                                               VerticalAlignment="Center" Foreground="{DynamicResource TrayAccent}"
                                               Visibility="Collapsed" />
                                    <ContentPresenter Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True"
                                                      VerticalAlignment="Center" />
                                    <TextBlock x:Name="Arrow" Grid.Column="2" Text="__ARROW__"
                                               FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets" FontSize="10"
                                               VerticalAlignment="Center" HorizontalAlignment="Center"
                                               Opacity="0.7" Visibility="Collapsed" />
                                </Grid>
                            </Border>
                            <Popup x:Name="PART_Popup" Placement="__PLACEMENT__" HorizontalOffset="-6" VerticalOffset="-8"
                                   IsOpen="{TemplateBinding IsSubmenuOpen}" AllowsTransparency="True"
                                   Focusable="False" PopupAnimation="Fade">
                                <Border Background="{DynamicResource TraySurface}" BorderBrush="{DynamicResource TrayStroke}"
                                        BorderThickness="1" CornerRadius="8" Padding="4" Margin="0,0,12,12" MinWidth="160">
                                    <Border.Effect>
                                        <DropShadowEffect BlurRadius="18" ShadowDepth="3" Opacity="0.35" Color="Black" />
                                    </Border.Effect>
                                    <ItemsPresenter />
                                </Border>
                            </Popup>
                        </Grid>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsHighlighted" Value="True">
                                <Setter TargetName="Shell" Property="Background" Value="{DynamicResource TrayHover}" />
                            </Trigger>
                            <Trigger Property="IsChecked" Value="True">
                                <Setter TargetName="Tick" Property="Visibility" Value="Visible" />
                            </Trigger>
                            <Trigger Property="Role" Value="SubmenuHeader">
                                <Setter TargetName="Arrow" Property="Visibility" Value="Visible" />
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Foreground" Value="{DynamicResource TrayDim}" />
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """;

    const string Line = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Separator">
            <Border Height="1" Background="{DynamicResource TrayStroke}" />
        </ControlTemplate>
        """;
}
