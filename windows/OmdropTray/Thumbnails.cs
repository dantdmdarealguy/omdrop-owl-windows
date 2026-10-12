using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Omdrop.Tray;

/// <summary>
/// Thumbnails both ways: the FileIcon a sender puts in /Ask, decoded for the
/// request card (JPEG and PNG; Windows has no JPEG 2000 decoder, so those show
/// the glyph), and one of our own for images we send.
/// </summary>
public static class Thumbnails
{
    static readonly string[] Images = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".webp"];

    public static ImageSource? Decode(byte[]? data)
    {
        if (data == null || data.Length < 16)
            return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 128;
            img.StreamSource = new MemoryStream(data);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or InvalidOperationException)
        {
            Log.Debug($"thumbnail: can't decode (starts {Convert.ToHexString(data, 0, Math.Min(12, data.Length))})");
            return null;
        }
    }

    public static ImageSource? FromFile(string path)
    {
        try
        {
            return IsImage(path) ? Decode(File.ReadAllBytes(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static bool IsImage(string path) => Array.IndexOf(Images, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// A JPEG of at most `max` pixels on its longer side, for /Ask's FileIcon;
    /// null if Windows can't read the image.
    public static byte[]? Jpeg(string path, int max = 512)
    {
        if (!IsImage(path))
            return null;
        try
        {
            var src = new BitmapImage();
            src.BeginInit();
            src.CacheOption = BitmapCacheOption.OnLoad;
            src.UriSource = new Uri(path);
            src.DecodePixelWidth = max; // decoders keep the aspect ratio
            src.EndInit();
            BitmapSource bmp = src;
            if (bmp.PixelHeight > max)
            {
                double k = (double)max / bmp.PixelHeight;
                bmp = new TransformedBitmap(bmp, new ScaleTransform(k, k));
            }
            var enc = new JpegBitmapEncoder { QualityLevel = 82 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
