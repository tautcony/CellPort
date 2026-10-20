using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace CellPort.App.Services;

public enum AppTheme { System, Dark, Light }

/// <summary>Applies the shared compact CellPort palette while leaving Fluent's platform control templates intact.</summary>
public static class ThemeManager
{
    private static bool _stylesInstalled;

    public static void Install(Application app)
    {
        if (_stylesInstalled) return;
        _stylesInstalled = true;
        app.Styles.Add(StyleFor<Window>(TemplatedControl.BackgroundProperty, "AppBackground"));
        app.Styles.Add(new Style(x => x.OfType<TextBlock>())
        {
            Setters = { new Setter(TextBlock.FontSizeProperty, 13d), new Setter(TextBlock.ForegroundProperty, Resource("TextPrimary")) }
        });
        app.Styles.Add(new Style(x => x.OfType<Button>())
        {
            Setters =
            {
                new Setter(Layoutable.MinHeightProperty, 32d),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 5)),
            }
        });
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("nav-item"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Resource("SidebarSurface")),
                new Setter(TemplatedControl.ForegroundProperty, Resource("TextSecondary")),
                new Setter(TemplatedControl.BorderBrushProperty, Brushes.Transparent),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 5)),
                new Setter(TemplatedControl.FontSizeProperty, 13d),
                new Setter(Layoutable.MinHeightProperty, 34d),
                new Setter(Layoutable.HeightProperty, 34d),
            }
        });
        app.Styles.Add(new Style(x => x.OfType<Button>().Class("nav-item").Class("selected"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Resource("NavSelected")),
                new Setter(TemplatedControl.ForegroundProperty, Resource("Accent")),
                new Setter(TemplatedControl.FontWeightProperty, FontWeight.SemiBold),
            }
        });
        app.Styles.Add(new Style(x => x.OfType<TextBox>())
        {
            Setters = { new Setter(Layoutable.MinHeightProperty, 34d), new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 5)) }
        });
        app.Styles.Add(new Style(x => x.OfType<Border>())
        {
            Setters = { new Setter(Border.BorderBrushProperty, Resource("SurfaceBorder")) }
        });
        app.Styles.Add(new Style(x => x.OfType<Border>().Class("app-card"))
        {
            Setters =
            {
                new Setter(Border.BackgroundProperty, Resource("CardSurface")),
                new Setter(Border.BorderThicknessProperty, new Thickness(1)),
                new Setter(Border.CornerRadiusProperty, new CornerRadius(10)),
            }
        });
        Apply(AppTheme.Dark);
    }

    public static void Apply(AppTheme theme)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = theme switch
        {
            AppTheme.Dark => ThemeVariant.Dark,
            AppTheme.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
        var dark = theme == AppTheme.Dark || (theme == AppTheme.System && app.ActualThemeVariant == ThemeVariant.Dark);
        var colors = dark
            ? new Dictionary<string, string>
            {
                ["AppBackground"] = "#1C1C1E", ["SidebarSurface"] = "#232326", ["CardSurface"] = "#2C2C2E",
                ["HoverSurface"] = "#34343A", ["NavSelected"] = "#273B59", ["SurfaceBorder"] = "#3A3A3E", ["TextPrimary"] = "#F2F2F7",
                ["TextSecondary"] = "#98989F", ["Accent"] = "#0A84FF", ["Success"] = "#30D158",
                ["Warning"] = "#FF9F0A", ["Danger"] = "#FF453A",
            }
            : new Dictionary<string, string>
            {
                ["AppBackground"] = "#F2F2F7", ["SidebarSurface"] = "#E8E8ED", ["CardSurface"] = "#FFFFFF",
                ["HoverSurface"] = "#E5E5EA", ["NavSelected"] = "#DDEBFF", ["SurfaceBorder"] = "#D1D1D6", ["TextPrimary"] = "#1C1C1E",
                ["TextSecondary"] = "#6C6C70", ["Accent"] = "#007AFF", ["Success"] = "#248A3D",
                ["Warning"] = "#9A6700", ["Danger"] = "#D70015",
            };
        foreach (var (key, color) in colors) app.Resources[key] = new SolidColorBrush(Color.Parse(color));
    }

    private static Style StyleFor<T>(AvaloniaProperty property, string resource) where T : Control =>
        new(x => x.OfType<T>()) { Setters = { new Setter(property, Resource(resource)) } };

    private static DynamicResourceExtension Resource(string key) => new(key);
}
