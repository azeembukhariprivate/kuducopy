using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Kuducopy.Controls
{
    /// <summary>The KUDUCOPY mark: two pages and an arrow. Uses the text colour.</summary>
    public class LogoMark : Viewbox
    {
        public LogoMark(double size = 40, double strokeThickness = 3.5)
        {
            Width = size;
            Height = size;
            Focusable = false;
            IsHitTestVisible = false;

            var canvas = new Canvas { Width = 64, Height = 64 };
            canvas.Children.Add(Stroke("M26,43 H15 A4,4 0 0 1 11,39 V13 A4,4 0 0 1 15,9 H34 A4,4 0 0 1 38,13 V21", strokeThickness));
            canvas.Children.Add(Stroke("M30,21 H49 A4,4 0 0 1 53,25 V51 A4,4 0 0 1 49,55 H30 A4,4 0 0 1 26,51 V25 A4,4 0 0 1 30,21 Z", strokeThickness));
            canvas.Children.Add(Stroke("M32.5,38 H46 M41,33 L46,38 L41,43", strokeThickness));
            Child = canvas;
        }

        private Path Stroke(string data, double thickness)
        {
            var p = new Path
            {
                Data = Geometry.Parse(data),
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            p.SetBinding(Shape.StrokeProperty, new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), Source = this });
            return p;
        }
    }
}
