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

    public string RenderText(byte[] rgb, int columns, int rows)
    {
        if (rgb.Length < columns * rows * 3)
            return string.Empty;

        var builder = new StringBuilder((columns + Environment.NewLine.Length) * rows);
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                int source = (y * columns + x) * 3;
                builder.Append(GetGlyph(rgb[source], rgb[source + 1], rgb[source + 2]));
            }

            if (y < rows - 1)
                builder.AppendLine();
        }

        return builder.ToString();
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

                    double sourceLuminance = GetSourceLuminance(r, g, b);
                    double displayLuminance = Math.Pow(sourceLuminance, 0.72);
                    char glyph = GetGlyphFromDisplayLuminance(displayLuminance);
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

    private static char GetGlyph(byte r, byte g, byte b)
    {
        double displayLuminance = Math.Pow(GetSourceLuminance(r, g, b), 0.72);
        return GetGlyphFromDisplayLuminance(displayLuminance);
    }

    private static char GetGlyphFromDisplayLuminance(double displayLuminance)
    {
        int rampIndex = (int)Math.Round(displayLuminance * (Ramp.Length - 1));
        rampIndex = Math.Clamp(rampIndex, 0, Ramp.Length - 1);
        return Ramp[rampIndex];
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
}
