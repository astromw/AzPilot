using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AzPilot.Core;
using System.Globalization;

namespace AzPilot.App;

/// <summary>Looking-up all-sky view: horizon at the rim, zenith at the centre, east left and west right.</summary>
public sealed class MountDial : Control
{
    public static readonly StyledProperty<double> AzimuthProperty = AvaloniaProperty.Register<MountDial, double>(nameof(Azimuth));
    public static readonly StyledProperty<double> AltitudeProperty = AvaloniaProperty.Register<MountDial, double>(nameof(Altitude));
    public double Azimuth { get => GetValue(AzimuthProperty); set => SetValue(AzimuthProperty, value); }
    public double Altitude { get => GetValue(AltitudeProperty); set => SetValue(AltitudeProperty, value); }
    static MountDial() { AffectsRender<MountDial>(AzimuthProperty, AltitudeProperty); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Math.Min(Bounds.Width, Bounds.Height) - 24;
        if (size < 40) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2;
        var horizon = new Pen(new SolidColorBrush(Color.Parse("#48617C")), 1.5);
        var grid = new Pen(new SolidColorBrush(Color.Parse("#263B53")), 1);
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#091626")), horizon, center, radius, radius);
        context.DrawEllipse(null, grid, center, radius * 2 / 3, radius * 2 / 3); // 30° altitude
        context.DrawEllipse(null, grid, center, radius / 3, radius / 3);         // 60° altitude
        context.DrawLine(grid, new Point(center.X - radius, center.Y), new Point(center.X + radius, center.Y));
        context.DrawLine(grid, new Point(center.X, center.Y - radius), new Point(center.X, center.Y + radius));

        DrawLabel(context, "N", center.WithY(center.Y - radius + 13), "#A5B4C8", 11);
        DrawLabel(context, "S", center.WithY(center.Y + radius - 13), "#A5B4C8", 11);
        DrawLabel(context, "W", center.WithX(center.X + radius - 12), "#A5B4C8", 11);
        DrawLabel(context, "E", center.WithX(center.X - radius + 12), "#A5B4C8", 11);
        context.DrawLine(grid, new Point(center.X - 4, center.Y), new Point(center.X + 4, center.Y));
        context.DrawLine(grid, new Point(center.X, center.Y - 4), new Point(center.X, center.Y + 4));
        DrawLabel(context, "ZENITH", center.WithY(center.Y + 15), "#64748B", 8);

        var altitude = Math.Clamp(Altitude, 0, 90);
        var distance = radius * (90 - altitude) / 90;
        var angle = (-Azimuth - 90) * Math.PI / 180;
        var point = new Point(center.X + Math.Cos(angle) * distance, center.Y + Math.Sin(angle) * distance);
        var glow = new SolidColorBrush(Color.Parse("#4067E8F9"));
        var cyan = new SolidColorBrush(Color.Parse("#67E8F9"));
        context.DrawEllipse(glow, null, point, 11, 11);
        context.DrawEllipse(cyan, new Pen(new SolidColorBrush(Colors.White), 1.2), point, 5.5, 5.5);
        DrawLabel(context, $"{Altitude:0.0}° ALT", new Point(center.X, Bounds.Height - 5), "#BAE6FD", 10);
    }

    private static void DrawLabel(DrawingContext context, string text, Point center, string color, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Inter"), size, new SolidColorBrush(Color.Parse(color)));
        context.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }
}

/// <summary>A filled solar disc with eight evenly spaced rays.</summary>
public sealed class SunIcon : Control
{
    public static readonly StyledProperty<IBrush> StrokeProperty =
        AvaloniaProperty.Register<SunIcon, IBrush>(nameof(Stroke), Brushes.Black);

    public IBrush Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    static SunIcon() { AffectsRender<SunIcon>(StrokeProperty); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 4) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var discRadius = size * 0.19;
        var innerRay = size * 0.30;
        var outerRay = size * 0.44;
        var pen = new Pen(Stroke, Math.Max(1.5, size / 11), lineCap: PenLineCap.Round);
        context.DrawEllipse(Stroke, null, center, discRadius, discRadius);
        for (var ray = 0; ray < 8; ray++)
        {
            var angle = ray * Math.PI / 4;
            var direction = new Vector(Math.Cos(angle), Math.Sin(angle));
            context.DrawLine(pen, center + direction * innerRay, center + direction * outerRay);
        }
    }
}

/// <summary>Geometrically identical arrows in all four directions.</summary>
public sealed class ArrowIcon : Control
{
    public static readonly StyledProperty<GuideDirection> DirectionProperty =
        AvaloniaProperty.Register<ArrowIcon, GuideDirection>(nameof(Direction), GuideDirection.Right);
    public static readonly StyledProperty<IBrush> StrokeProperty =
        AvaloniaProperty.Register<ArrowIcon, IBrush>(nameof(Stroke), Brushes.Black);

    public GuideDirection Direction { get => GetValue(DirectionProperty); set => SetValue(DirectionProperty, value); }
    public IBrush Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    static ArrowIcon() { AffectsRender<ArrowIcon>(DirectionProperty, StrokeProperty); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 4) return;
        var (x, y) = Direction switch
        {
            GuideDirection.Up => (0.0, -1.0),
            GuideDirection.Down => (0.0, 1.0),
            GuideDirection.Left => (-1.0, 0.0),
            _ => (1.0, 0.0)
        };
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var direction = new Vector(x, y);
        var perpendicular = new Vector(-y, x);
        var length = size * 0.40;
        var head = size * 0.34;
        var start = center - direction * length;
        var end = center + direction * length;
        var headBase = end - direction * head;
        var pen = new Pen(Stroke, Math.Max(1.5, size / 12));
        context.DrawLine(pen, start, end);
        context.DrawLine(pen, headBase + perpendicular * head, end);
        context.DrawLine(pen, headBase - perpendicular * head, end);
    }
}
