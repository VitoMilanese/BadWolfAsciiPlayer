using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BadWolfAsciiPlayer.Core;

public sealed class AsciiRenderer
{
    public const int CellWidth = 6;
    public const int CellHeight = 10;

    // Ordered from the least to the most ink coverage.  Starting with a dot
    // instead of a space keeps dark-but-visible picture detail from vanishing.
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

    public unsafe void Render(byte[] rgb, int columns, int rows, AsciiMode mode)
    {
        WriteableBitmap bitmap = EnsureBitmap(columns, rows);
        if (rgb.Length < columns * rows * 3)
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
                    int source = (cellY * columns + cellX) * 3;
                    byte r = rgb[source];
                    byte g = rgb[source + 1];
                    byte b = rgb[source + 2];

                    double sourceLuminance = (r * 0.2126 + g * 0.7152 + b * 0.0722) / 255.0;

                    // Human vision and display gamma make a direct linear mapping look much
                    // darker when only a fraction of each ASCII cell contains foreground ink.
                    // Lift mid-tones before selecting the glyph, then compensate the foreground
                    // intensity for that glyph's measured ink coverage.
                    double displayLuminance = Math.Pow(sourceLuminance, 0.72);
                    int rampIndex = (int)Math.Round(displayLuminance * (Ramp.Length - 1));
                    rampIndex = Math.Clamp(rampIndex, 0, Ramp.Length - 1);

                    char glyph = Ramp[rampIndex];
                    byte[] mask = _glyphMasks[glyph];
                    double coverage = _glyphCoverage[glyph];
                    double coverageGain = Math.Clamp(TargetGlyphCoverage / coverage, 1.0, 3.2);
                    double brightnessGain = Math.Clamp(1.12 * coverageGain, 1.12, 3.4);

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
                        // Preserve hue while recovering the luminance lost to the black area
                        // surrounding each glyph.  A small floor keeps saturated dark colours
                        // visible without turning true blacks grey.
                        double colorGain = sourceLuminance < 0.015 ? 1.0 : brightnessGain;
                        fgR = ToByte(r * colorGain);
                        fgG = ToByte(g * colorGain);
                        fgB = ToByte(b * colorGain);
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
}
