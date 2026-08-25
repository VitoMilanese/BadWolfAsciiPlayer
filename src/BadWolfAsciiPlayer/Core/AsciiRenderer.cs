using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BadWolfAsciiPlayer.Core;

public sealed class AsciiRenderer
{
    public const int CellWidth = 6;
    public const int CellHeight = 10;

    private const string Ramp = ".:-=+*#%@";
    private const double TargetGlyphCoverage = 0.58;

    private readonly Dictionary<char, byte[]> _glyphMasks = new();
    private readonly Dictionary<char, double> _glyphCoverage = new();
    private WriteableBitmap? _bitmap;
    private int _columns;
    private int _rows;

    public AsciiRenderer()
    {
        foreach (char c in Ramp)
        {
            byte[] mask = CreateGlyphMask(c);
            _glyphMasks[c] = mask;
            _glyphCoverage[c] = Math.Max(0.08, mask.Average(value => value / 255.0));
        }
    }

    public WriteableBitmap EnsureBitmap(int columns, int rows)
    {
        if (_bitmap is null || _columns != columns || _rows != rows)
        {
            _columns = columns;
            _rows = rows;
            _bitmap = new WriteableBitmap(
                columns * CellWidth,
                rows * CellHeight,
                96,
                96,
                PixelFormats.Bgra32,
                null);
        }

        return _bitmap;
    }

    public string RenderText(
        byte[] rgb,
        int sourceWidth,
        int sourceHeight,
        int columns,
        int rows,
        double edgeStrength = 0)
    {
        FrameAnalysis analysis = AnalyzeFrame(rgb, sourceWidth, sourceHeight, columns, rows);
        if (analysis.Cells.Length == 0)
            return string.Empty;

        var builder = new StringBuilder((columns + Environment.NewLine.Length) * rows);
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                CellAnalysis cell = analysis.Cells[y * columns + x];
                double edgeAmount = Math.Clamp(cell.EdgeScore * edgeStrength, 0.0, 1.0);
                builder.Append(GetGlyph(cell.Luminance, edgeAmount));
            }

            if (y < rows - 1)
                builder.AppendLine();
        }

        return builder.ToString();
    }

    public unsafe void Render(
        byte[] rgb,
        int sourceWidth,
        int sourceHeight,
        int columns,
        int rows,
        AsciiMode mode,
        double edgeStrength = 0)
    {
        WriteableBitmap bitmap = EnsureBitmap(columns, rows);
        FrameAnalysis analysis = AnalyzeFrame(rgb, sourceWidth, sourceHeight, columns, rows);
        if (analysis.Cells.Length == 0)
            return;

        bitmap.Lock();
        try
        {
            byte* basePtr = (byte*)bitmap.BackBuffer.ToPointer();
            int stride = bitmap.BackBufferStride;

            for (int y = 0; y < rows * CellHeight; y++)
                new Span<byte>(basePtr + y * stride, columns * CellWidth * 4).Clear();

            for (int cellY = 0; cellY < rows; cellY++)
            {
                for (int cellX = 0; cellX < columns; cellX++)
                {
                    CellAnalysis cell = analysis.Cells[cellY * columns + cellX];
                    double edgeAmount = Math.Clamp(cell.EdgeScore * edgeStrength, 0.0, 1.0);
                    double displayLuminance = GetDisplayLuminance(cell.Luminance, edgeAmount);
                    char glyph = GetGlyph(cell.Luminance, edgeAmount);
                    byte[] mask = _glyphMasks[glyph];
                    double coverage = _glyphCoverage[glyph];
                    double coverageGain = Math.Clamp(TargetGlyphCoverage / coverage, 1.0, 3.2);
                    double brightnessGain = Math.Clamp(
                        1.12 * coverageGain * (1.0 + edgeAmount * 0.42),
                        1.12,
                        4.0);

                    byte fgR;
                    byte fgG;
                    byte fgB;
                    if (mode == AsciiMode.Mono)
                    {
                        byte mono = ToByte(255.0 * Math.Clamp(displayLuminance * brightnessGain, 0.0, 1.0));
                        fgR = mono;
                        fgG = mono;
                        fgB = mono;
                    }
                    else
                    {
                        double colorGain = cell.Luminance < 0.015 ? 1.0 : brightnessGain;
                        double highlight = edgeAmount * 0.22;
                        fgR = ToByte((cell.R * colorGain) * (1.0 - highlight) + 255.0 * highlight);
                        fgG = ToByte((cell.G * colorGain) * (1.0 - highlight) + 255.0 * highlight);
                        fgB = ToByte((cell.B * colorGain) * (1.0 - highlight) + 255.0 * highlight);
                    }

                    int originX = cellX * CellWidth;
                    int originY = cellY * CellHeight;

                    for (int glyphY = 0; glyphY < CellHeight; glyphY++)
                    {
                        byte* rowPtr = basePtr + (originY + glyphY) * stride + originX * 4;
                        int maskRow = glyphY * CellWidth;
                        for (int glyphX = 0; glyphX < CellWidth; glyphX++)
                        {
                            byte alpha = mask[maskRow + glyphX];
                            int pixel = glyphX * 4;
                            rowPtr[pixel] = (byte)(fgB * alpha / 255);
                            rowPtr[pixel + 1] = (byte)(fgG * alpha / 255);
                            rowPtr[pixel + 2] = (byte)(fgR * alpha / 255);
                            rowPtr[pixel + 3] = 255;
                        }
                    }
                }
            }

            bitmap.AddDirtyRect(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        }
        finally
        {
            bitmap.Unlock();
        }
    }

    private static FrameAnalysis AnalyzeFrame(
        byte[] rgb,
        int sourceWidth,
        int sourceHeight,
        int columns,
        int rows)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || columns <= 0 || rows <= 0)
            return FrameAnalysis.Empty;

        int sourcePixelCount = checked(sourceWidth * sourceHeight);
        if (rgb.Length < sourcePixelCount * 3)
            return FrameAnalysis.Empty;

        double[] r = new double[sourcePixelCount];
        double[] g = new double[sourcePixelCount];
        double[] b = new double[sourcePixelCount];
        double[] yChannel = new double[sourcePixelCount];
        double[] cb = new double[sourcePixelCount];
        double[] cr = new double[sourcePixelCount];

        for (int i = 0; i < sourcePixelCount; i++)
        {
            int source = i * 3;
            double rr = rgb[source] / 255.0;
            double gg = rgb[source + 1] / 255.0;
            double bb = rgb[source + 2] / 255.0;
            double yy = rr * 0.2126 + gg * 0.7152 + bb * 0.0722;

            r[i] = rr;
            g[i] = gg;
            b[i] = bb;
            yChannel[i] = yy;
            cb[i] = (bb - yy) * 0.65;
            cr[i] = (rr - yy) * 0.65;
        }

        r = GaussianBlur5x5(r, sourceWidth, sourceHeight);
        g = GaussianBlur5x5(g, sourceWidth, sourceHeight);
        b = GaussianBlur5x5(b, sourceWidth, sourceHeight);
        yChannel = GaussianBlur5x5(yChannel, sourceWidth, sourceHeight);
        cb = GaussianBlur5x5(cb, sourceWidth, sourceHeight);
        cr = GaussianBlur5x5(cr, sourceWidth, sourceHeight);

        GradientSample[] yGradient = BuildScharrGradient(yChannel, sourceWidth, sourceHeight);
        GradientSample[] cbGradient = BuildScharrGradient(cb, sourceWidth, sourceHeight);
        GradientSample[] crGradient = BuildScharrGradient(cr, sourceWidth, sourceHeight);
        GradientSample[] combinedGradient = CombineGradients(yGradient, cbGradient, crGradient);
        double[] suppressed = NonMaximumSuppression(combinedGradient, sourceWidth, sourceHeight);
        bool[] edgeMask = HysteresisEdges(suppressed, sourceWidth, sourceHeight);

        var cells = new CellAnalysis[columns * rows];

        for (int cellY = 0; cellY < rows; cellY++)
        {
            int y0 = cellY * sourceHeight / rows;
            int y1 = Math.Max(y0 + 1, (cellY + 1) * sourceHeight / rows);
            y1 = Math.Min(y1, sourceHeight);

            for (int cellX = 0; cellX < columns; cellX++)
            {
                int x0 = cellX * sourceWidth / columns;
                int x1 = Math.Max(x0 + 1, (cellX + 1) * sourceWidth / columns);
                x1 = Math.Min(x1, sourceWidth);

                double sumR = 0;
                double sumG = 0;
                double sumB = 0;
                double sumLuma = 0;
                double edgeSum = 0;
                double edgePeak = 0;
                int edgeCount = 0;
                int pixelCount = 0;

                for (int yy = y0; yy < y1; yy++)
                {
                    int rowOffset = yy * sourceWidth;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int index = rowOffset + xx;
                        int source = index * 3;
                        sumR += rgb[source];
                        sumG += rgb[source + 1];
                        sumB += rgb[source + 2];
                        sumLuma += GetSourceLuminance(rgb[source], rgb[source + 1], rgb[source + 2]);
                        pixelCount++;

                        if (!edgeMask[index])
                            continue;

                        double magnitude = suppressed[index];
                        edgeCount++;
                        edgeSum += magnitude;
                        edgePeak = Math.Max(edgePeak, magnitude);
                    }
                }

                if (pixelCount == 0)
                    continue;

                double edgeScore = 0;
                if (edgeCount > 0)
                {
                    double mean = edgeSum / edgeCount;
                    double coverage = edgeCount / (double)pixelCount;
                    double coverageWeight = Math.Clamp(coverage * 2.8, 0.55, 1.0);
                    edgeScore = Math.Clamp((edgePeak * 0.62 + mean * 0.38) * coverageWeight * 2.15, 0.0, 1.0);
                }

                cells[cellY * columns + cellX] = new CellAnalysis(
                    ToByte(sumR / pixelCount),
                    ToByte(sumG / pixelCount),
                    ToByte(sumB / pixelCount),
                    sumLuma / pixelCount,
                    edgeScore);
            }
        }

        return new FrameAnalysis(cells);
    }

    private static double[] GaussianBlur5x5(double[] source, int width, int height)
    {
        if (source.Length == 0)
            return Array.Empty<double>();

        double[] horizontal = new double[source.Length];
        double[] result = new double[source.Length];
        int[] kernel = [1, 4, 6, 4, 1];

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                double sum = 0;
                for (int k = -2; k <= 2; k++)
                {
                    int sx = Math.Clamp(x + k, 0, width - 1);
                    sum += source[row + sx] * kernel[k + 2];
                }
                horizontal[row + x] = sum / 16.0;
            }
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double sum = 0;
                for (int k = -2; k <= 2; k++)
                {
                    int sy = Math.Clamp(y + k, 0, height - 1);
                    sum += horizontal[sy * width + x] * kernel[k + 2];
                }
                result[y * width + x] = sum / 16.0;
            }
        }

        return result;
    }

    private static GradientSample[] BuildScharrGradient(double[] source, int width, int height)
    {
        var result = new GradientSample[source.Length];
        if (width < 3 || height < 3)
            return result;

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                double tl = source[(y - 1) * width + x - 1];
                double tc = source[(y - 1) * width + x];
                double tr = source[(y - 1) * width + x + 1];
                double ml = source[y * width + x - 1];
                double mr = source[y * width + x + 1];
                double bl = source[(y + 1) * width + x - 1];
                double bc = source[(y + 1) * width + x];
                double br = source[(y + 1) * width + x + 1];

                double gx = (-3 * tl + 3 * tr - 10 * ml + 10 * mr - 3 * bl + 3 * br) / 32.0;
                double gy = (-3 * tl - 10 * tc - 3 * tr + 3 * bl + 10 * bc + 3 * br) / 32.0;
                double magnitude = Math.Sqrt(gx * gx + gy * gy);
                result[y * width + x] = new GradientSample(magnitude, gx, gy);
            }
        }

        return result;
    }

    private static GradientSample[] CombineGradients(
        GradientSample[] luminance,
        GradientSample[] cb,
        GradientSample[] cr)
    {
        var result = new GradientSample[luminance.Length];

        for (int i = 0; i < result.Length; i++)
        {
            GradientSample best = luminance[i];
            double bestWeighted = best.Magnitude;

            double cbWeighted = cb[i].Magnitude * 1.15;
            if (cbWeighted > bestWeighted)
            {
                best = new GradientSample(cbWeighted, cb[i].Gx * 1.15, cb[i].Gy * 1.15);
                bestWeighted = cbWeighted;
            }

            double crWeighted = cr[i].Magnitude * 1.15;
            if (crWeighted > bestWeighted)
                best = new GradientSample(crWeighted, cr[i].Gx * 1.15, cr[i].Gy * 1.15);

            result[i] = best;
        }

        return result;
    }

    private static double[] NonMaximumSuppression(GradientSample[] gradient, int width, int height)
    {
        var result = new double[gradient.Length];

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int index = y * width + x;
                GradientSample current = gradient[index];
                if (current.Magnitude <= 0)
                    continue;

                double angle = Math.Atan2(current.Gy, current.Gx) * 180.0 / Math.PI;
                if (angle < 0)
                    angle += 180.0;

                double before;
                double after;

                if (angle < 22.5 || angle >= 157.5)
                {
                    before = gradient[index - 1].Magnitude;
                    after = gradient[index + 1].Magnitude;
                }
                else if (angle < 67.5)
                {
                    before = gradient[(y - 1) * width + x + 1].Magnitude;
                    after = gradient[(y + 1) * width + x - 1].Magnitude;
                }
                else if (angle < 112.5)
                {
                    before = gradient[(y - 1) * width + x].Magnitude;
                    after = gradient[(y + 1) * width + x].Magnitude;
                }
                else
                {
                    before = gradient[(y - 1) * width + x - 1].Magnitude;
                    after = gradient[(y + 1) * width + x + 1].Magnitude;
                }

                if (current.Magnitude >= before && current.Magnitude >= after)
                    result[index] = current.Magnitude;
            }
        }

        return result;
    }

    private static bool[] HysteresisEdges(double[] suppressed, int width, int height)
    {
        var values = suppressed.Where(v => v > 0).OrderBy(v => v).ToArray();
        if (values.Length == 0)
            return new bool[suppressed.Length];

        double p85 = Percentile(values, 0.85);
        double p95 = Percentile(values, 0.95);
        double high = Math.Max(0.018, p85 * 0.72 + p95 * 0.18);
        double low = high * 0.42;

        bool[] edges = new bool[suppressed.Length];
        bool[] visited = new bool[suppressed.Length];
        var queue = new Queue<int>();

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int index = y * width + x;
                if (suppressed[index] >= high)
                {
                    edges[index] = true;
                    visited[index] = true;
                    queue.Enqueue(index);
                }
            }
        }

        int[] offsets = [-width - 1, -width, -width + 1, -1, 1, width - 1, width, width + 1];
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            int cx = current % width;
            int cy = current / width;

            foreach (int offset in offsets)
            {
                int next = current + offset;
                if (next <= 0 || next >= suppressed.Length - 1 || visited[next])
                    continue;

                int nx = next % width;
                int ny = next / width;
                if (Math.Abs(nx - cx) > 1 || Math.Abs(ny - cy) > 1)
                    continue;

                visited[next] = true;
                if (suppressed[next] < low)
                    continue;

                edges[next] = true;
                queue.Enqueue(next);
            }
        }

        return edges;
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
            return 0;

        double position = Math.Clamp(percentile, 0.0, 1.0) * (sortedValues.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return sortedValues[lower];

        double fraction = position - lower;
        return sortedValues[lower] * (1.0 - fraction) + sortedValues[upper] * fraction;
    }

    private static char GetGlyph(double sourceLuminance, double edgeAmount)
    {
        double displayLuminance = GetDisplayLuminance(sourceLuminance, edgeAmount);
        int rampIndex = (int)Math.Round(displayLuminance * (Ramp.Length - 1));
        rampIndex = Math.Clamp(rampIndex, 0, Ramp.Length - 1);
        return Ramp[rampIndex];
    }

    private static double GetDisplayLuminance(double sourceLuminance, double edgeAmount) =>
        Math.Clamp(Math.Pow(sourceLuminance, 0.72) + edgeAmount * 0.34, 0.0, 1.0);

    private static double GetSourceLuminance(byte r, byte g, byte b) =>
        (r * 0.2126 + g * 0.7152 + b * 0.0722) / 255.0;

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static byte[] CreateGlyphMask(char c)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var text = new FormattedText(
                c.ToString(),
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                9,
                Brushes.White,
                1.0);

            double x = Math.Max(0, (CellWidth - text.WidthIncludingTrailingWhitespace) / 2.0);
            double y = Math.Max(-1, (CellHeight - text.Height) / 2.0 - 0.5);
            dc.DrawText(text, new Point(x, y));
        }

        var render = new RenderTargetBitmap(CellWidth, CellHeight, 96, 96, PixelFormats.Pbgra32);
        render.Render(visual);
        byte[] pixels = new byte[CellWidth * CellHeight * 4];
        render.CopyPixels(pixels, CellWidth * 4, 0);

        byte[] mask = new byte[CellWidth * CellHeight];
        for (int i = 0; i < mask.Length; i++)
        {
            byte alpha = pixels[i * 4 + 3];
            byte luminance = Math.Max(pixels[i * 4], Math.Max(pixels[i * 4 + 1], pixels[i * 4 + 2]));
            mask[i] = Math.Max(alpha, luminance);
        }

        return mask;
    }

    private readonly record struct GradientSample(double Magnitude, double Gx, double Gy);
    private readonly record struct CellAnalysis(byte R, byte G, byte B, double Luminance, double EdgeScore);
    private readonly record struct FrameAnalysis(CellAnalysis[] Cells)
    {
        public static FrameAnalysis Empty { get; } = new(Array.Empty<CellAnalysis>());
    }
}
