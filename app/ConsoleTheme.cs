using System.Windows.Media;

namespace RoutePilot;

// AWG CITA sector-console palette. Keep operational state separate from branding.
internal static class ConsoleTheme
{
    public static Brush Brush(string colour)=>new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour));
    public static readonly Brush Text=Brush("#F0EADB"), Dim=Brush("#AAA297"), Red=Brush("#E8443F"),
        Ok=Brush("#97C59D"), Warn=Brush("#D7AD67"), Blue=Brush("#86A9C8"), Line=Brush("#282426");
}
