using System.Drawing;
using System.Drawing.Drawing2D;

namespace FxDeck.Tray;

/// <summary>Tray icons drawn at runtime (no image assets): a 2×2 tile deck, grey when the game is not connected.</summary>
internal static class TrayIcons
{
    public static Icon Create(bool connected, int size = 32)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var background = new SolidBrush(Color.FromArgb(230, 20, 20, 20)))
            {
                g.FillPath(background, RoundedRect(0, 0, size, size, size * 0.22f));
            }

            Color[] colours = connected
                ? [Color.FromArgb(0x2F, 0x6F, 0xDB), Color.FromArgb(0xC2, 0x40, 0x8F), Color.FromArgb(0x3C, 0x8D, 0x5A), Color.FromArgb(0xD0, 0x8A, 0x2A)]
                : [Color.FromArgb(0x70, 0x70, 0x70), Color.FromArgb(0x60, 0x60, 0x60), Color.FromArgb(0x60, 0x60, 0x60), Color.FromArgb(0x70, 0x70, 0x70)];

            var pad = size * 0.16f;
            var gap = size * 0.08f;
            var tile = (size - pad * 2 - gap) / 2;
            for (var i = 0; i < 4; i++)
            {
                var x = pad + (i % 2) * (tile + gap);
                var y = pad + (i / 2) * (tile + gap);
                using var brush = new SolidBrush(colours[i]);
                g.FillPath(brush, RoundedRect(x, y, tile, tile, tile * 0.25f));
            }
        }

        // The HICON handle is intentionally kept alive for the lifetime of the process (two icons only).
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        var d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
