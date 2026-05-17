using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Waifu16K.Services;

public static class BackgroundRemovalService
{
    public static BitmapSource RemoveBorderBackground(BitmapSource source, int tolerance, int softness)
    {
        source = BitmapEditor.ToBgra32(source);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        Color edgeColor = EstimateEdgeColor(pixels, width, height, stride);
        bool[] background = FloodFillBorder(pixels, width, height, stride, edgeColor, tolerance);

        byte[] output = (byte[])pixels.Clone();
        int softLimit = Math.Max(tolerance + 1, tolerance + softness);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int pixelIndex = (y * stride) + (x * 4);
                int maskIndex = (y * width) + x;

                if (background[maskIndex])
                {
                    output[pixelIndex + 3] = 0;
                    continue;
                }

                if (softness <= 0 || !TouchesBackground(background, width, height, x, y))
                {
                    continue;
                }

                double distance = ColorDistance(output[pixelIndex], output[pixelIndex + 1], output[pixelIndex + 2], edgeColor);
                if (distance <= softLimit)
                {
                    double keep = Math.Clamp((distance - tolerance) / Math.Max(1, softness), 0, 1);
                    output[pixelIndex + 3] = (byte)(output[pixelIndex + 3] * keep);
                }
            }
        }

        return Create(output, width, height, source.DpiX, source.DpiY);
    }

    public static BitmapSource ChromaKey(BitmapSource source, Color keyColor, int tolerance, int softness)
    {
        source = BitmapEditor.ToBgra32(source);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        int softLimit = Math.Max(tolerance + 1, tolerance + softness);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            double distance = ColorDistance(pixels[i], pixels[i + 1], pixels[i + 2], keyColor);
            if (distance <= tolerance)
            {
                pixels[i + 3] = 0;
            }
            else if (distance <= softLimit)
            {
                double keep = Math.Clamp((distance - tolerance) / Math.Max(1, softness), 0, 1);
                pixels[i + 3] = (byte)(pixels[i + 3] * keep);
            }
        }

        return Create(pixels, width, height, source.DpiX, source.DpiY);
    }

    public static BitmapSource FeatherAlpha(BitmapSource source, int radius)
    {
        source = BitmapEditor.ToBgra32(source);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        byte[] output = new byte[pixels.Length];
        source.CopyPixels(pixels, stride, 0);
        Buffer.BlockCopy(pixels, 0, output, 0, pixels.Length);

        radius = Math.Clamp(radius, 1, 8);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sum = 0;
                int count = 0;

                for (int yy = Math.Max(0, y - radius); yy <= Math.Min(height - 1, y + radius); yy++)
                {
                    for (int xx = Math.Max(0, x - radius); xx <= Math.Min(width - 1, x + radius); xx++)
                    {
                        sum += pixels[(yy * stride) + (xx * 4) + 3];
                        count++;
                    }
                }

                output[(y * stride) + (x * 4) + 3] = (byte)(sum / Math.Max(1, count));
            }
        }

        return Create(output, width, height, source.DpiX, source.DpiY);
    }

    public static BitmapSource Defringe(BitmapSource source, int amount)
    {
        source = BitmapEditor.ToBgra32(source);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        amount = Math.Clamp(amount, 0, 100);
        if (amount == 0)
        {
            return source;
        }

        for (int i = 0; i < pixels.Length; i += 4)
        {
            double alpha = pixels[i + 3] / 255.0;
            if (alpha is <= 0.02 or >= 0.98)
            {
                continue;
            }

            double pull = (1 - alpha) * (amount / 100.0);
            byte max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
            pixels[i] = (byte)(pixels[i] + ((max - pixels[i]) * pull * 0.25));
            pixels[i + 1] = (byte)(pixels[i + 1] + ((max - pixels[i + 1]) * pull * 0.25));
            pixels[i + 2] = (byte)(pixels[i + 2] + ((max - pixels[i + 2]) * pull * 0.25));
        }

        return Create(pixels, width, height, source.DpiX, source.DpiY);
    }

    private static bool[] FloodFillBorder(byte[] pixels, int width, int height, int stride, Color edgeColor, int tolerance)
    {
        var visited = new bool[width * height];
        var queue = new Queue<(int X, int Y)>();

        void TryEnqueue(int x, int y)
        {
            int maskIndex = (y * width) + x;
            if (visited[maskIndex])
            {
                return;
            }

            int pixelIndex = (y * stride) + (x * 4);
            if (pixels[pixelIndex + 3] == 0)
            {
                visited[maskIndex] = true;
                return;
            }

            if (ColorDistance(pixels[pixelIndex], pixels[pixelIndex + 1], pixels[pixelIndex + 2], edgeColor) <= tolerance)
            {
                visited[maskIndex] = true;
                queue.Enqueue((x, y));
            }
        }

        for (int x = 0; x < width; x++)
        {
            TryEnqueue(x, 0);
            TryEnqueue(x, height - 1);
        }

        for (int y = 0; y < height; y++)
        {
            TryEnqueue(0, y);
            TryEnqueue(width - 1, y);
        }

        while (queue.Count > 0)
        {
            (int x, int y) = queue.Dequeue();

            if (x > 0)
            {
                TryEnqueue(x - 1, y);
            }

            if (x < width - 1)
            {
                TryEnqueue(x + 1, y);
            }

            if (y > 0)
            {
                TryEnqueue(x, y - 1);
            }

            if (y < height - 1)
            {
                TryEnqueue(x, y + 1);
            }
        }

        return visited;
    }

    private static Color EstimateEdgeColor(byte[] pixels, int width, int height, int stride)
    {
        long b = 0;
        long g = 0;
        long r = 0;
        long count = 0;

        void Sample(int x, int y)
        {
            int index = (y * stride) + (x * 4);
            if (pixels[index + 3] < 8)
            {
                return;
            }

            b += pixels[index];
            g += pixels[index + 1];
            r += pixels[index + 2];
            count++;
        }

        for (int x = 0; x < width; x++)
        {
            Sample(x, 0);
            Sample(x, height - 1);
        }

        for (int y = 1; y < height - 1; y++)
        {
            Sample(0, y);
            Sample(width - 1, y);
        }

        if (count == 0)
        {
            return Colors.White;
        }

        return Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
    }

    private static bool TouchesBackground(bool[] background, int width, int height, int x, int y)
    {
        for (int yy = Math.Max(0, y - 1); yy <= Math.Min(height - 1, y + 1); yy++)
        {
            for (int xx = Math.Max(0, x - 1); xx <= Math.Min(width - 1, x + 1); xx++)
            {
                if (background[(yy * width) + xx])
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static double ColorDistance(byte b, byte g, byte r, Color color)
    {
        double db = b - color.B;
        double dg = g - color.G;
        double dr = r - color.R;
        return Math.Sqrt((db * db) + (dg * dg) + (dr * dr));
    }

    private static BitmapSource Create(byte[] pixels, int width, int height, double dpiX, double dpiY)
    {
        var bitmap = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
