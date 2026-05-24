using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Waifu16K.Models;

namespace Waifu16K.Services;

public sealed record AdjustmentSettings(
    double Brightness,
    double Contrast,
    double Saturation,
    double Denoise,
    double Sharpen);

public static class BitmapEditor
{
    public static BitmapSource LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.EndInit();
        image.Freeze();
        return ToBgra32(image);
    }

    public static BitmapSource ToBgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32)
        {
            if (!source.IsFrozen && source.CanFreeze)
            {
                source.Freeze();
            }

            return source;
        }

        var converted = new FormatConvertedBitmap();
        converted.BeginInit();
        converted.Source = source;
        converted.DestinationFormat = PixelFormats.Bgra32;
        converted.EndInit();
        converted.Freeze();
        return converted;
    }

    public static BitmapSource Rotate(BitmapSource source, double angle)
    {
        var transform = new TransformedBitmap(ToBgra32(source), new RotateTransform(angle));
        transform.Freeze();
        return ToBgra32(transform);
    }

    public static BitmapSource FlipHorizontal(BitmapSource source)
    {
        return Flip(source, horizontal: true);
    }

    public static BitmapSource FlipVertical(BitmapSource source)
    {
        return Flip(source, horizontal: false);
    }

    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        rect = ClampRect(rect, source.PixelWidth, source.PixelHeight);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return source;
        }

        var cropped = new CroppedBitmap(ToBgra32(source), rect);
        cropped.Freeze();
        return ToBgra32(cropped);
    }

    public static BitmapSource ApplySelectionAlpha(BitmapSource source, Int32Rect rect, bool keepInside, int feather)
    {
        source = ToBgra32(source);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        feather = Math.Clamp(feather, 0, 256);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double coverage = SelectionCoverage(x, y, rect, feather);
                double alphaFactor = keepInside ? coverage : 1.0 - coverage;
                int index = (y * stride) + (x * 4);
                pixels[index + 3] = ClampToByte(pixels[index + 3] * alphaFactor);
            }
        }

        return CreateBitmap(pixels, width, height, source.DpiX, source.DpiY);
    }

    public static BitmapSource CreateSelectionMask(
        int width,
        int height,
        Int32Rect rect,
        int feather,
        double dpiX = 96,
        double dpiY = 96)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        feather = Math.Clamp(feather, 0, 256);
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = ClampToByte(SelectionCoverage(x, y, rect, feather) * 255);
                int index = (y * stride) + (x * 4);
                pixels[index] = value;
                pixels[index + 1] = value;
                pixels[index + 2] = value;
                pixels[index + 3] = 255;
            }
        }

        return CreateBitmap(pixels, width, height, dpiX, dpiY);
    }

    public static BitmapSource Resize(BitmapSource source, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        double scaleX = (double)width / source.PixelWidth;
        double scaleY = (double)height / source.PixelHeight;
        var transform = new TransformedBitmap(ToBgra32(source), new ScaleTransform(scaleX, scaleY));
        transform.Freeze();
        return ToBgra32(transform);
    }

    public static BitmapSource ApplyAdjustments(BitmapSource source, AdjustmentSettings settings)
    {
        source = ToBgra32(source);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        double contrast = Math.Clamp(settings.Contrast, -100, 100) * 2.55;
        double contrastFactor = (259 * (contrast + 255)) / (255 * (259 - contrast));
        double brightness = Math.Clamp(settings.Brightness, -100, 100) * 2.55;
        double saturation = Math.Clamp(settings.Saturation, -100, 100) / 100.0;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0)
            {
                continue;
            }

            double b = ApplyTone(pixels[i], brightness, contrastFactor);
            double g = ApplyTone(pixels[i + 1], brightness, contrastFactor);
            double r = ApplyTone(pixels[i + 2], brightness, contrastFactor);

            if (Math.Abs(saturation) > 0.001)
            {
                double gray = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
                double factor = 1.0 + saturation;
                r = gray + ((r - gray) * factor);
                g = gray + ((g - gray) * factor);
                b = gray + ((b - gray) * factor);
            }

            pixels[i] = ClampToByte(b);
            pixels[i + 1] = ClampToByte(g);
            pixels[i + 2] = ClampToByte(r);
        }

        double denoise = Math.Clamp(settings.Denoise, 0, 100) / 100.0;
        if (denoise > 0.001)
        {
            byte[] blurred = BoxBlur(pixels, width, height, radius: 1);
            Blend(pixels, blurred, denoise * 0.65);
        }

        double sharpen = Math.Clamp(settings.Sharpen, 0, 100) / 100.0;
        if (sharpen > 0.001)
        {
            byte[] blurred = BoxBlur(pixels, width, height, radius: 1);
            ApplyUnsharpMask(pixels, blurred, sharpen * 1.35);
        }

        return CreateBitmap(pixels, width, height, source.DpiX, source.DpiY);
    }

    public static BitmapSource AutoEnhance(BitmapSource source)
    {
        source = ToBgra32(source);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        int[] min = [255, 255, 255];
        int[] max = [0, 0, 0];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 10)
            {
                continue;
            }

            for (int c = 0; c < 3; c++)
            {
                min[c] = Math.Min(min[c], pixels[i + c]);
                max[c] = Math.Max(max[c], pixels[i + c]);
            }
        }

        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0)
            {
                continue;
            }

            for (int c = 0; c < 3; c++)
            {
                int range = Math.Max(1, max[c] - min[c]);
                pixels[i + c] = ClampToByte(((pixels[i + c] - min[c]) * 255.0 / range) * 0.92 + 10);
            }
        }

        return CreateBitmap(pixels, width, height, source.DpiX, source.DpiY);
    }

    public static BitmapSource ApplyAlphaBrush(BitmapSource source, int centerX, int centerY, int radius, bool restore)
    {
        source = ToBgra32(source);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        radius = Math.Clamp(radius, 2, 400);
        int minX = Math.Max(0, centerX - radius);
        int maxX = Math.Min(width - 1, centerX + radius);
        int minY = Math.Max(0, centerY - radius);
        int maxY = Math.Min(height - 1, centerY + radius);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                double distance = Math.Sqrt(((x - centerX) * (x - centerX)) + ((y - centerY) * (y - centerY)));
                if (distance > radius)
                {
                    continue;
                }

                double pressure = 1.0 - Math.Clamp(distance / radius, 0, 1);
                pressure = Math.Pow(pressure, 0.65);
                int index = (y * stride) + (x * 4) + 3;

                if (restore)
                {
                    pixels[index] = ClampToByte(pixels[index] + (255 * pressure));
                }
                else
                {
                    pixels[index] = ClampToByte(pixels[index] * (1.0 - pressure));
                }
            }
        }

        return CreateBitmap(pixels, width, height, source.DpiX, source.DpiY);
    }

    public static BitmapSource ApplyAutoRestoreBrush(BitmapSource target, BitmapSource restoreSource, int centerX, int centerY, int radius, int sensitivity, double strength)
    {
        target = ToBgra32(target);
        restoreSource = ToBgra32(restoreSource);

        if (target.PixelWidth != restoreSource.PixelWidth || target.PixelHeight != restoreSource.PixelHeight)
        {
            throw new InvalidOperationException("자동 복원은 원본과 현재 이미지 크기가 같을 때만 사용할 수 있습니다.");
        }

        int width = target.PixelWidth;
        int height = target.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        byte[] restorePixels = new byte[stride * height];
        target.CopyPixels(pixels, stride, 0);
        restoreSource.CopyPixels(restorePixels, stride, 0);
        byte[] alphaSnapshot = (byte[])pixels.Clone();

        radius = Math.Clamp(radius, 2, 400);
        sensitivity = Math.Clamp(sensitivity, 20, 100);
        strength = Math.Clamp(strength, 0.2, 1.0);
        int minX = Math.Max(0, centerX - radius);
        int maxX = Math.Min(width - 1, centerX + radius);
        int minY = Math.Max(0, centerY - radius);
        int maxY = Math.Min(height - 1, centerY + radius);
        int localWidth = maxX - minX + 1;
        int localHeight = maxY - minY + 1;
        bool[] inBrush = new bool[localWidth * localHeight];
        double[] pressureMap = new double[inBrush.Length];
        double[] candidateMap = new double[inBrush.Length];
        double[] confidenceMap = new double[inBrush.Length];
        int centerIndex = (Math.Clamp(centerY, 0, height - 1) * stride) + (Math.Clamp(centerX, 0, width - 1) * 4);
        int supportRadius = Math.Clamp(radius / 10, 3, 18);
        int profileStep = radius > 150 ? 5 : radius > 96 ? 4 : radius > 48 ? 3 : 2;
        double colorTolerance = Math.Clamp(52 + (sensitivity * 1.65), 86, 218);
        double propagationKeep = 0.84 + (sensitivity / 100.0 * 0.11);

        var foregroundSeeds = new List<ColorSeed>
        {
            new(restorePixels[centerIndex], restorePixels[centerIndex + 1], restorePixels[centerIndex + 2])
        };
        var backgroundSeeds = new List<ColorSeed>();

        for (int y = minY; y <= maxY; y += profileStep)
        {
            for (int x = minX; x <= maxX; x += profileStep)
            {
                double distance = Math.Sqrt(((x - centerX) * (x - centerX)) + ((y - centerY) * (y - centerY)));
                if (distance > radius)
                {
                    continue;
                }

                double pressure = Math.Pow(1.0 - Math.Clamp(distance / radius, 0, 1), 0.65);
                int index = (y * stride) + (x * 4);
                double alpha = alphaSnapshot[index + 3] / 255.0;

                if (alpha > 0.45)
                {
                    AddColorSeed(foregroundSeeds, restorePixels, index, 64);
                }
                else if (alpha < 0.04 && (distance > radius * 0.68 || NeighborAlphaSupport(alphaSnapshot, width, height, stride, x, y, supportRadius) < 0.12))
                {
                    AddColorSeed(backgroundSeeds, restorePixels, index, 72);
                }
            }
        }

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                double distance = Math.Sqrt(((x - centerX) * (x - centerX)) + ((y - centerY) * (y - centerY)));
                if (distance > radius)
                {
                    continue;
                }

                double pressure = 1.0 - Math.Clamp(distance / radius, 0, 1);
                pressure = Math.Pow(pressure, 0.65);
                int index = (y * stride) + (x * 4);
                int localIndex = ((y - minY) * localWidth) + (x - minX);
                double currentAlpha = alphaSnapshot[index + 3] / 255.0;
                double foregroundDistance = NearestColorDistance(restorePixels, index, foregroundSeeds);
                double foregroundScore = 1.0 - Math.Clamp((foregroundDistance - (colorTolerance * 0.18)) / colorTolerance, 0, 1);
                double centerDistance = ColorDistance(restorePixels, index, restorePixels[centerIndex], restorePixels[centerIndex + 1], restorePixels[centerIndex + 2]);
                double centerScore = 1.0 - Math.Clamp((centerDistance - (colorTolerance * 0.25)) / colorTolerance, 0, 1);
                double backgroundSeparation = 1.0;

                if (backgroundSeeds.Count > 0)
                {
                    double backgroundDistance = NearestColorDistance(restorePixels, index, backgroundSeeds);
                    backgroundSeparation = Math.Clamp((backgroundDistance - foregroundDistance + (colorTolerance * 0.42)) / colorTolerance, 0, 1);
                }

                double colorConfidence = Math.Clamp((foregroundScore * 0.68) + (centerScore * 0.32), 0, 1);
                if (backgroundSeeds.Count > 0)
                {
                    colorConfidence *= Math.Clamp((backgroundSeparation * 0.75) + 0.25, 0, 1);
                }

                inBrush[localIndex] = true;
                pressureMap[localIndex] = pressure;
                candidateMap[localIndex] = colorConfidence;

                if (currentAlpha > 0.45)
                {
                    confidenceMap[localIndex] = Math.Max(confidenceMap[localIndex], currentAlpha);
                }
                else if (distance < Math.Max(4, radius * 0.18) && centerScore > 0.35)
                {
                    confidenceMap[localIndex] = Math.Max(confidenceMap[localIndex], centerScore * pressure * 0.78);
                }
            }
        }

        int iterations = Math.Clamp(radius / 7, 6, 28);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            double[] nextConfidence = (double[])confidenceMap.Clone();
            for (int ly = 0; ly < localHeight; ly++)
            {
                int y = minY + ly;
                for (int lx = 0; lx < localWidth; lx++)
                {
                    int localIndex = (ly * localWidth) + lx;
                    if (!inBrush[localIndex] || candidateMap[localIndex] < 0.02)
                    {
                        continue;
                    }

                    int x = minX + lx;
                    int index = (y * stride) + (x * 4);
                    double bestNeighbor = 0;
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        int ny = ly + oy;
                        if (ny < 0 || ny >= localHeight)
                        {
                            continue;
                        }

                        for (int ox = -1; ox <= 1; ox++)
                        {
                            if (ox == 0 && oy == 0)
                            {
                                continue;
                            }

                            int nx = lx + ox;
                            if (nx < 0 || nx >= localWidth)
                            {
                                continue;
                            }

                            int neighborLocalIndex = (ny * localWidth) + nx;
                            double neighborConfidence = confidenceMap[neighborLocalIndex];
                            if (neighborConfidence <= 0)
                            {
                                continue;
                            }

                            int neighborX = minX + nx;
                            int neighborY = minY + ny;
                            int neighborIndex = (neighborY * stride) + (neighborX * 4);
                            double colorAffinity = 1.0 - Math.Clamp(ColorDistanceBetween(restorePixels, index, neighborIndex) / colorTolerance, 0, 1);
                            double diagonalPenalty = ox != 0 && oy != 0 ? 0.92 : 1.0;
                            bestNeighbor = Math.Max(bestNeighbor, neighborConfidence * colorAffinity * diagonalPenalty);
                        }
                    }

                    double proposed = bestNeighbor * candidateMap[localIndex] * propagationKeep;
                    if (proposed > nextConfidence[localIndex])
                    {
                        nextConfidence[localIndex] = proposed;
                    }
                }
            }

            confidenceMap = nextConfidence;
        }

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int localIndex = ((y - minY) * localWidth) + (x - minX);
                if (!inBrush[localIndex])
                {
                    continue;
                }

                int index = (y * stride) + (x * 4);
                double currentAlpha = pixels[index + 3] / 255.0;
                double restoreConfidence = Math.Clamp(confidenceMap[localIndex], 0, 1);
                double amount = pressureMap[localIndex] * restoreConfidence * strength;
                if (currentAlpha > 0.92)
                {
                    amount *= 0.22;
                }

                if (amount < 0.015)
                {
                    continue;
                }

                double targetAlpha = Math.Max(
                    pixels[index + 3],
                    restorePixels[index + 3] * Math.Clamp(restoreConfidence * (0.55 + (pressureMap[localIndex] * 0.45)), 0, 1));

                pixels[index] = ClampToByte((pixels[index] * (1.0 - amount)) + (restorePixels[index] * amount));
                pixels[index + 1] = ClampToByte((pixels[index + 1] * (1.0 - amount)) + (restorePixels[index + 1] * amount));
                pixels[index + 2] = ClampToByte((pixels[index + 2] * (1.0 - amount)) + (restorePixels[index + 2] * amount));
                pixels[index + 3] = ClampToByte((pixels[index + 3] * (1.0 - amount)) + (targetAlpha * amount));
            }
        }

        return CreateBitmap(pixels, width, height, target.DpiX, target.DpiY);
    }

    public static BitmapSource TrimTransparent(BitmapSource source, byte threshold = 8)
    {
        source = ToBgra32(source);
        return Crop(source, GetTransparentBounds(source, threshold));
    }

    public static Int32Rect GetTransparentBounds(BitmapSource source, byte threshold = 8)
    {
        source = ToBgra32(source);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        int left = width;
        int top = height;
        int right = -1;
        int bottom = -1;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte alpha = pixels[(y * stride) + (x * 4) + 3];
                if (alpha <= threshold)
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < left || bottom < top)
        {
            return new Int32Rect(0, 0, width, height);
        }

        return new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }

    public static BitmapSource CreateTransparent(int width, int height, double dpiX = 96, double dpiY = 96)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        return CreateBitmap(new byte[width * height * 4], width, height, dpiX, dpiY);
    }

    public static BitmapSource CreateMask(int width, int height, byte value = 255, double dpiX = 96, double dpiY = 96)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = value;
            pixels[i + 1] = value;
            pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }

        return CreateBitmap(pixels, width, height, dpiX, dpiY);
    }

    public static BitmapSource ApplyMaskBrush(BitmapSource mask, int centerX, int centerY, int radius, bool reveal, double strength)
    {
        mask = ToBgra32(mask);
        int width = mask.PixelWidth;
        int height = mask.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        mask.CopyPixels(pixels, stride, 0);

        radius = Math.Clamp(radius, 2, 400);
        strength = Math.Clamp(strength, 0.02, 1.0);
        int minX = Math.Max(0, centerX - radius);
        int maxX = Math.Min(width - 1, centerX + radius);
        int minY = Math.Max(0, centerY - radius);
        int maxY = Math.Min(height - 1, centerY + radius);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                double distance = Math.Sqrt(((x - centerX) * (x - centerX)) + ((y - centerY) * (y - centerY)));
                if (distance > radius)
                {
                    continue;
                }

                double pressure = Math.Pow(1.0 - Math.Clamp(distance / radius, 0, 1), 0.65) * strength;
                int index = (y * stride) + (x * 4);
                double current = pixels[index] / 255.0;
                double target = reveal ? 1.0 : 0.0;
                byte value = ClampToByte((current + ((target - current) * pressure)) * 255.0);
                pixels[index] = value;
                pixels[index + 1] = value;
                pixels[index + 2] = value;
                pixels[index + 3] = 255;
            }
        }

        return CreateBitmap(pixels, width, height, mask.DpiX, mask.DpiY);
    }

    public static BitmapSource InvertMask(BitmapSource mask)
    {
        mask = ToBgra32(mask);
        int width = mask.PixelWidth;
        int height = mask.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        mask.CopyPixels(pixels, stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte value = ClampToByte(255 - pixels[i]);
            pixels[i] = value;
            pixels[i + 1] = value;
            pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }

        return CreateBitmap(pixels, width, height, mask.DpiX, mask.DpiY);
    }

    public static BitmapSource CompositeLayers(
        IEnumerable<(BitmapSource Bitmap, double Opacity, ImageBlendMode BlendMode, BitmapSource? Mask, int OffsetX, int OffsetY)> layers,
        int width,
        int height,
        double dpiX,
        double dpiY)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        int outputStride = width * 4;
        byte[] output = new byte[outputStride * height];

        foreach ((BitmapSource layerSource, double opacityValue, ImageBlendMode blendMode, BitmapSource? maskSource, int offsetX, int offsetY) in layers)
        {
            double opacity = Math.Clamp(opacityValue, 0, 1);
            if (opacity <= 0)
            {
                continue;
            }

            BitmapSource layer = ToBgra32(layerSource);
            int layerStride = layer.PixelWidth * 4;
            byte[] input = new byte[layerStride * layer.PixelHeight];
            layer.CopyPixels(input, layerStride, 0);
            BitmapSource? mask = maskSource is null ? null : ToBgra32(maskSource);
            int maskStride = mask is null ? 0 : mask.PixelWidth * 4;
            byte[]? maskPixels = null;
            if (mask is not null)
            {
                maskPixels = new byte[maskStride * mask.PixelHeight];
                mask.CopyPixels(maskPixels, maskStride, 0);
            }

            for (int y = 0; y < layer.PixelHeight; y++)
            {
                int outputY = y + offsetY;
                if (outputY < 0 || outputY >= height)
                {
                    continue;
                }

                int sourceRow = y * layerStride;
                int outputRow = outputY * outputStride;
                for (int x = 0; x < layer.PixelWidth; x++)
                {
                    int outputX = x + offsetX;
                    if (outputX < 0 || outputX >= width)
                    {
                        continue;
                    }

                    int sourceIndex = sourceRow + (x * 4);
                    int outputIndex = outputRow + (outputX * 4);
                    double maskAlpha = GetMaskAlpha(maskPixels, mask, maskStride, x, y);
                    double sourceAlpha = (input[sourceIndex + 3] / 255.0) * opacity * maskAlpha;
                    if (sourceAlpha <= 0)
                    {
                        continue;
                    }

                    double targetAlpha = output[outputIndex + 3] / 255.0;
                    double outAlpha = sourceAlpha + (targetAlpha * (1.0 - sourceAlpha));
                    if (outAlpha <= 0)
                    {
                        continue;
                    }

                    for (int channel = 0; channel < 3; channel++)
                    {
                        byte blendedChannel = BlendChannel(input[sourceIndex + channel], output[outputIndex + channel], blendMode, targetAlpha);
                        double sourceColor = blendedChannel * sourceAlpha;
                        double targetColor = output[outputIndex + channel] * targetAlpha * (1.0 - sourceAlpha);
                        output[outputIndex + channel] = ClampToByte((sourceColor + targetColor) / outAlpha);
                    }

                    output[outputIndex + 3] = ClampToByte(outAlpha * 255);
                }
            }
        }

        return CreateBitmap(output, width, height, dpiX, dpiY);
    }

    private static double GetMaskAlpha(byte[]? maskPixels, BitmapSource? mask, int maskStride, int x, int y)
    {
        if (maskPixels is null || mask is null)
        {
            return 1.0;
        }

        if (x < 0 || y < 0 || x >= mask.PixelWidth || y >= mask.PixelHeight)
        {
            return 0.0;
        }

        int index = (y * maskStride) + (x * 4);
        double gray = (maskPixels[index] + maskPixels[index + 1] + maskPixels[index + 2]) / 765.0;
        return gray * (maskPixels[index + 3] / 255.0);
    }

    private static byte BlendChannel(byte source, byte target, ImageBlendMode blendMode, double targetAlpha)
    {
        if (targetAlpha <= 0)
        {
            return source;
        }

        double s = source / 255.0;
        double t = target / 255.0;
        double blended = blendMode switch
        {
            ImageBlendMode.Multiply => s * t,
            ImageBlendMode.Screen => 1.0 - ((1.0 - s) * (1.0 - t)),
            ImageBlendMode.Overlay => t < 0.5 ? 2.0 * s * t : 1.0 - (2.0 * (1.0 - s) * (1.0 - t)),
            ImageBlendMode.Darken => Math.Min(s, t),
            ImageBlendMode.Lighten => Math.Max(s, t),
            _ => s
        };

        return ClampToByte(blended * 255.0);
    }

    public static void Save(BitmapSource source, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);

        string extension = Path.GetExtension(path).ToLowerInvariant();
        BitmapEncoder encoder = extension switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 96 },
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };

        BitmapSource frameSource = extension is ".jpg" or ".jpeg"
            ? FlattenOnColor(source, Colors.White)
            : ToBgra32(source);

        encoder.Frames.Add(BitmapFrame.Create(frameSource));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapSource Flip(BitmapSource source, bool horizontal)
    {
        source = ToBgra32(source);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        byte[] input = new byte[stride * height];
        byte[] output = new byte[input.Length];
        source.CopyPixels(input, stride, 0);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sourceX = horizontal ? width - 1 - x : x;
                int sourceY = horizontal ? y : height - 1 - y;
                Buffer.BlockCopy(input, (sourceY * stride) + (sourceX * 4), output, (y * stride) + (x * 4), 4);
            }
        }

        return CreateBitmap(output, width, height, source.DpiX, source.DpiY);
    }

    private static BitmapSource FlattenOnColor(BitmapSource source, Color background)
    {
        source = ToBgra32(source);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            double alpha = pixels[i + 3] / 255.0;
            pixels[i] = ClampToByte((pixels[i] * alpha) + (background.B * (1 - alpha)));
            pixels[i + 1] = ClampToByte((pixels[i + 1] * alpha) + (background.G * (1 - alpha)));
            pixels[i + 2] = ClampToByte((pixels[i + 2] * alpha) + (background.R * (1 - alpha)));
            pixels[i + 3] = 255;
        }

        return CreateBitmap(pixels, width, height, source.DpiX, source.DpiY);
    }

    private static Int32Rect ClampRect(Int32Rect rect, int width, int height)
    {
        int x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        int y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        int right = Math.Clamp(rect.X + rect.Width, x + 1, width);
        int bottom = Math.Clamp(rect.Y + rect.Height, y + 1, height);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    private static double SelectionCoverage(int x, int y, Int32Rect rect, int feather)
    {
        double px = x + 0.5;
        double py = y + 0.5;
        double left = rect.X;
        double top = rect.Y;
        double right = rect.X + rect.Width;
        double bottom = rect.Y + rect.Height;
        bool inside = px >= left && px < right && py >= top && py < bottom;
        if (feather <= 0)
        {
            return inside ? 1.0 : 0.0;
        }

        if (inside)
        {
            double edgeDistance = Math.Min(
                Math.Min(px - left, right - px),
                Math.Min(py - top, bottom - py));
            double t = Math.Clamp(edgeDistance / feather, 0, 1);
            return 0.5 + (0.5 * SmoothStep(t));
        }

        double dx = px < left ? left - px : px >= right ? px - right : 0;
        double dy = py < top ? top - py : py >= bottom ? py - bottom : 0;
        double outsideDistance = Math.Sqrt((dx * dx) + (dy * dy));
        if (outsideDistance >= feather)
        {
            return 0.0;
        }

        return 0.5 * (1.0 - SmoothStep(outsideDistance / feather));
    }

    private static double SmoothStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - (2 * value));
    }

    private static double ApplyTone(byte value, double brightness, double contrastFactor)
    {
        return (contrastFactor * (value - 128)) + 128 + brightness;
    }

    private static double NeighborAlphaSupport(byte[] pixels, int width, int height, int stride, int x, int y, int radius)
    {
        radius = Math.Clamp(radius, 1, 16);
        int step = Math.Max(1, radius / 3);
        int maxAlpha = pixels[(y * stride) + (x * 4) + 3];

        for (int yy = Math.Max(0, y - radius); yy <= Math.Min(height - 1, y + radius); yy += step)
        {
            for (int xx = Math.Max(0, x - radius); xx <= Math.Min(width - 1, x + radius); xx += step)
            {
                int alpha = pixels[(yy * stride) + (xx * 4) + 3];
                if (alpha > maxAlpha)
                {
                    maxAlpha = alpha;
                }
            }
        }

        return maxAlpha / 255.0;
    }

    private static double ColorDistance(byte[] pixels, int index, double b, double g, double r)
    {
        double db = pixels[index] - b;
        double dg = pixels[index + 1] - g;
        double dr = pixels[index + 2] - r;
        return Math.Sqrt((db * db) + (dg * dg) + (dr * dr));
    }

    private static double ColorDistanceBetween(byte[] pixels, int firstIndex, int secondIndex)
    {
        double db = pixels[firstIndex] - pixels[secondIndex];
        double dg = pixels[firstIndex + 1] - pixels[secondIndex + 1];
        double dr = pixels[firstIndex + 2] - pixels[secondIndex + 2];
        return Math.Sqrt((db * db) + (dg * dg) + (dr * dr));
    }

    private static double NearestColorDistance(byte[] pixels, int index, IReadOnlyList<ColorSeed> seeds)
    {
        if (seeds.Count == 0)
        {
            return 441.7;
        }

        double nearest = double.MaxValue;
        for (int i = 0; i < seeds.Count; i++)
        {
            ColorSeed seed = seeds[i];
            nearest = Math.Min(nearest, ColorDistance(pixels, index, seed.B, seed.G, seed.R));
        }

        return nearest;
    }

    private static void AddColorSeed(List<ColorSeed> seeds, byte[] pixels, int index, int maxCount)
    {
        if (seeds.Count >= maxCount)
        {
            return;
        }

        if (seeds.Count > 8 && NearestColorDistance(pixels, index, seeds) < 18)
        {
            return;
        }

        seeds.Add(new ColorSeed(pixels[index], pixels[index + 1], pixels[index + 2]));
    }

    private static byte[] BoxBlur(byte[] pixels, int width, int height, int radius)
    {
        int stride = width * 4;
        byte[] output = new byte[pixels.Length];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int count = 0;
                int[] sum = [0, 0, 0, 0];

                for (int yy = Math.Max(0, y - radius); yy <= Math.Min(height - 1, y + radius); yy++)
                {
                    for (int xx = Math.Max(0, x - radius); xx <= Math.Min(width - 1, x + radius); xx++)
                    {
                        int index = (yy * stride) + (xx * 4);
                        sum[0] += pixels[index];
                        sum[1] += pixels[index + 1];
                        sum[2] += pixels[index + 2];
                        sum[3] += pixels[index + 3];
                        count++;
                    }
                }

                int outputIndex = (y * stride) + (x * 4);
                output[outputIndex] = (byte)(sum[0] / count);
                output[outputIndex + 1] = (byte)(sum[1] / count);
                output[outputIndex + 2] = (byte)(sum[2] / count);
                output[outputIndex + 3] = (byte)(sum[3] / count);
            }
        }

        return output;
    }

    private static void Blend(byte[] target, byte[] source, double amount)
    {
        for (int i = 0; i < target.Length; i += 4)
        {
            target[i] = ClampToByte((target[i] * (1 - amount)) + (source[i] * amount));
            target[i + 1] = ClampToByte((target[i + 1] * (1 - amount)) + (source[i + 1] * amount));
            target[i + 2] = ClampToByte((target[i + 2] * (1 - amount)) + (source[i + 2] * amount));
        }
    }

    private static void ApplyUnsharpMask(byte[] target, byte[] blurred, double amount)
    {
        for (int i = 0; i < target.Length; i += 4)
        {
            target[i] = ClampToByte(target[i] + ((target[i] - blurred[i]) * amount));
            target[i + 1] = ClampToByte(target[i + 1] + ((target[i + 1] - blurred[i + 1]) * amount));
            target[i + 2] = ClampToByte(target[i + 2] + ((target[i + 2] - blurred[i + 2]) * amount));
        }
    }

    private static BitmapSource CreateBitmap(byte[] pixels, int width, int height, double dpiX, double dpiY)
    {
        var bitmap = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte ClampToByte(double value)
    {
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private readonly record struct ColorSeed(double B, double G, double R);
}
