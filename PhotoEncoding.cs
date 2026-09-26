using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Observer;

internal static class PhotoEncoding
{
    public static byte[] Encode(Bitmap frame)
    {
        const int maximumSide = 1920;
        double scale = Math.Min(1, maximumSide / (double)Math.Max(frame.Width, frame.Height));
        int width = Math.Max(1, (int)Math.Round(frame.Width * scale));
        int height = Math.Max(1, (int)Math.Round(frame.Height * scale));
        using var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(result))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(frame, 0, 0, width, height);
        }
        using var stream = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(x => x.MimeType == "image/jpeg");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 87L);
        result.Save(stream, encoder, parameters);
        return stream.ToArray();
    }

    public static string Mime(byte[] image) => image.Length >= 3 && image[0] == 0xFF && image[1] == 0xD8 && image[2] == 0xFF
        ? "image/jpeg" : "image/png";
    public static string FileName(byte[] image) => Mime(image) == "image/jpeg" ? "observer.jpg" : "observer.png";
}
