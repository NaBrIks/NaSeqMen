// SPDX-License-Identifier: MIT
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace NaSeqMen.Editor;

internal static class TablerIcons
{
    // Original SVG files and MIT notice are embedded alongside this renderer.
    public static ImageSource Load(string name, Brush stroke)
    {
        var uri = new Uri($"pack://application:,,,/NaSeqMen.Editor;component/Assets/Tabler/{name}.svg");
        using var stream = Application.GetResourceStream(uri)!.Stream;
        var svg = XDocument.Load(stream);
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        var pen = new Pen(stroke, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        foreach (var path in svg.Root!.Elements().Where(e => e.Name.LocalName == "path"))
            drawing.Children.Add(new GeometryDrawing(null, pen, Geometry.Parse(path.Attribute("d")!.Value)));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
