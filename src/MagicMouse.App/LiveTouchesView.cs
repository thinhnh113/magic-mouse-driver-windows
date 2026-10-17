using System.Drawing.Drawing2D;
using MagicMouse.Core.Protocol;

namespace MagicMouse.App;

/// <summary>Top-down view of the mouse surface with the current touches (front of the mouse at the top).</summary>
internal sealed class LiveTouchesView : Control
{
    private TouchFrame? _frame;
    private static readonly Color[] Palette =
        { Color.DodgerBlue, Color.OrangeRed, Color.SeaGreen, Color.MediumOrchid, Color.Goldenrod, Color.Teal };

    public LiveTouchesView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        BackColor = SystemColors.Window;
    }

    public void SetFrame(TouchFrame f)
    {
        _frame = f;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var r = new RectangleF(10, 10, Width - 20, Height - 40);
        using (var path = new GraphicsPath())
        {
            float d = r.Width;
            path.AddArc(r.X, r.Y, d, d, 180, 180);
            path.AddArc(r.X, r.Bottom - d, d, d, 0, 180);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(245, 245, 245));
            using var pen = new Pen(Color.Silver, 2);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        var f = _frame;
        if (f == null) return;
        foreach (var t in f.Touches.Where(t => t.IsDown))
        {
            float x = r.X + (t.X - Surface.MinX) * r.Width / Surface.Width;
            float y = r.Y + (t.Y - Surface.MinY) * r.Height / Surface.Height;
            float w = Math.Max(10, t.Major * r.Width / 255f), h = Math.Max(10, t.Minor * r.Width / 255f);
            var state = g.Save();
            g.TranslateTransform(x, y);
            g.RotateTransform(t.Orientation * 90f / 32f);
            using var b = new SolidBrush(Color.FromArgb(170, Palette[t.Id % Palette.Length]));
            g.FillEllipse(b, -w / 2, -h / 2, w, h);
            g.Restore(state);
            g.DrawString(t.Id.ToString(), Font, Brushes.Black, x + w / 2, y - h / 2);
        }
        string info = $"buttons: {f.Buttons}   motion: {f.Dx},{f.Dy}   touches: {f.Touches.Count(t => t.IsDown)}";
        g.DrawString(info, Font, SystemBrushes.ControlText, 10, Height - 24);
    }
}
