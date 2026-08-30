using System.Text;
using QRCoder;

namespace FxDeck.Web;

/// <summary>QR codes for the deck URL: PNG for the admin UI, half-block text for the console.</summary>
public static class QrRenderer
{
    public static byte[] ToPng(string content, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }

    /// <summary>
    /// Renders the code with ▀ ▄ █ so two module rows fit in one text line.
    /// Light modules are drawn as blocks, so on the usual dark terminal the code has its normal polarity.
    /// </summary>
    public static string ToConsoleString(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);

        var matrix = data.ModuleMatrix; // includes a 4-module quiet zone
        var size = matrix.Count;
        var sb = new StringBuilder();
        for (var y = 0; y < size; y += 2)
        {
            for (var x = 0; x < size; x++)
            {
                var topLight = !matrix[y][x];
                var bottomLight = y + 1 < size ? !matrix[y + 1][x] : true;
                sb.Append((topLight, bottomLight) switch
                {
                    (true, true) => '█',
                    (true, false) => '▀',
                    (false, true) => '▄',
                    _ => ' ',
                });
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
