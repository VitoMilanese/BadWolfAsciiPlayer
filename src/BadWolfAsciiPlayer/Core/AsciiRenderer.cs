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
    private const double EdgeThreshold = 0.035;
    private const double EdgeRange = 0.20;

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
                double edgeAmount = GetEdgeAmount(cell.EdgeScore, edgeStrength);
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
                    double edgeAmount = GetEdgeAmount(cell.EdgeScore, edgeStrength);
                    double displayLuminance = GetDisplayLuminance(cell.Luminance, edgeAmount);
                    char glyph = GetGlyph(cell.Luminance, edgeAmount);
                    byte[] mask = _glyphMasks[glyph];
                    double coverage = _glyphCoverage[glyph];
                    double coverageGain = Math.Clamp(TargetGlyphCoverage / coverage, 1.0, 3.2);
                    double brightnessGain = Math.Clamp(
                        1.12 * coverageGain * (1.0 + edgeAmount * 0.32),
                        1.12,
                        3.8);

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
                        double highlight = edgeAmount * 0.16;
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

        double[] luminance = new double[sourcePixelCount];
        for (int i = 0; i < sourcePixelCount; i++)
        {
            int source = i * 3;
            luminance[i] = GetSourceLuminance(rgb[source], rgb[source + 1], rgb[source + 2]);
        }

        double[] blurred = GaussianBlur3x3(luminance, sourceWidth, sourceHeight);
        EdgeSample[] edges = BuildEdgeMap(blurred, sourceWidth, sourceHeight);
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
                double edgeMagnitudeSum = 0;
                double edgeGxSum = 0;
                double edgeGySum = 0;
                double maxMagnitude = 0;
                int pixelCount = 0;
                int strongEdgeCount = 0;

                for (int y = y0; y < y1; y++)
                {
                    int rowOffset = y * sourceWidth;
                    for (int x = x0; x < x1; x++)
                    {
                        int index = rowOffset + x;
                        int source = index * 3;
                        sumR += rgb[source];
                        sumG += rgb[source + 1];
                        sumB += rgb[source + 2];
                        sumLuma += luminance[index];
                        pixelCount++;

                        EdgeSample edge = edges[index];
                        if (edge.Magnitude <= EdgeThreshold * 0.55)
                            continue;

                        strongEdgeCount++;
                        edgeMagnitudeSum += edge.Magnitude;
                        edgeGxSum += edge.Gx;
                        edgeGySum += edge.Gy;
                        maxMagnitude = Math.Max(maxMagnitude, edge.Magnitude);
                    }
                }

                if (pixelCount == 0)
                    continue;

                double meanStrongEdge = strongEdgeCount == 0 ? 0 : edgeMagnitudeSum / strongEdgeCount;
                double coherence = edgeMagnitudeSum <= 0
                    ? 0
                    : Math.Clamp(Math.Sqrt(edgeGxSum * edgeGxSum + edgeGySum * edgeGySum) / edgeMagnitudeSum, 0.0, 1.0);
                double occupancy = strongEdgeCount / (double)pixelCount;

                // Real contours tend to be both strong and directionally coherent. Random texture
                // can have a large Sobel response too, but its directions cancel and therefore
                // receive a much smaller score here.
                double edgeScore = (maxMagnitude * 0.58 + meanStrongEdge * 0.42)
                    * (0.38 + coherence * 0.62)
                    * Math.Clamp(occupancy * 2.4, 0.45, 1.0);

                cells[cellY * columns + cellX] = new CellAnalysis(
                    ToByte(sumR / pixelCount),
                    ToByte(sumG / pixelCount),
                    ToByte(sumB / pixelCount),
                    sumLuma / pixelCount,
                    Math.Clamp(edgeScore, 0.0, 1.0));
            }
        }

        return new FrameAnalysis(cells);
    }

    private static double[] GaussianBlur3x3(double[] source, int width, int height)
    {
        var result = new double[source.Length];
        if (width < 3 || height < 3)
        {
            Array.Copy(source, result, source.Length);
            return result;
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double sum = 0;
                double weightSum = 0;

                for (int ky = -1; ky <= 1; ky++)
                {
                    int sy = Math.Clamp(y + ky, 0, height - 1);
                    double wy = ky == 0 ? 2.0 : 1.0;
                    for (int kx = -1; kx <= 1; kx++)
                    {
                        int sx = Math.Clamp(x + kx, 0, width - 1);
                        double wx = kx == 0 ? 2.0 : 1.0;
                        double weight = wx * wy;
                        sum += source[sy * width + sx] * weight;
                        weightSum += weight;
                    }
                }

                result[y * width + x] = sum / weightSum;
            }
        }

        return result;
    }

    private static EdgeSample[] BuildEdgeMap(double[] luminance, int width, int height)
    {
        var result = new EdgeSample[luminance.Length];
        if (width < 3 || height < 3)
            return result;

        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                double tl = luminance[(y - 1) * width + x - 1];
                double tc = luminance[(y - 1) * width + x];
                double tr = luminance[(y - 1) * width + x + 1];
                double ml = luminance[y * width + x - 1];
                double mr = luminance[y * width + x + 1];
                double bl = luminance[(y + 1) * width + x - 1];
                double bc = luminance[(y + 1) * width + x];
                double br = luminance[(y + 1) * width + x + 1];

                double gx = (-tl + tr - 2.0 * ml + 2.0 * mr - bl + br) / 4.0;
                double gy = (-tl - 2.0 * tc - tr + bl + 2.0 * bc + br) / 4.0;
                double magnitude = Math.Clamp(Math.Sqrt(gx * gx + gy * gy), 0.0, 1.0);
                result[y * width + x] = new EdgeSample(magnitude, gx, gy);
            }
        }

        return result;
    }

    private static char GetGlyph(double sourceLuminance, double edgeAmount)
    {
        double displayLuminance = GetDisplayLuminance(sourceLuminance, edgeAmount);
        int rampIndex = (int)Math.Round(displayLuminance * (Ramp.Length - 1));
        rampIndex = Math.Clamp(rampIndex, 0, Ramp.Length - 1);
        return Ramp[rampIndex];
    }

    private static double GetDisplayLuminance(double sourceLuminance, double edgeAmount) =>
        Math.Clamp(Math.Pow(sourceLuminance, 0.72) + edgeAmount * 0.24, 0.0, 1.0);

    private static double GetEdgeAmount(double edgeScore, double edgeStrength)
    {
        if (edgeStrength <= 0 || edgeScore <= EdgeThreshold)
            return 0;

        double normalized = Math.Clamp((edgeScore - EdgeThreshold) / EdgeRange, 0.0, 1.0);
        return Math.Clamp(normalized * edgeStrength, 0.0, 1.0);
    }

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

    private readonly record struct EdgeSample(double Magnitude, double Gx, double Gy);
    private readonly record struct CellAnalysis(byte R, byte G, byte B, double Luminance, double EdgeScore);
    private readonly record struct FrameAnalysis(CellAnalysis[] Cells)
    {
        public static FrameAnalysis Empty { get; } = new(Array.Empty<CellAnalysis>());
    }
}
