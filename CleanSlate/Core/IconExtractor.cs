using System.Diagnostics;
using System.Linq;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace CleanSlate.Core;

/// <summary>
/// Extracts 48x48 high-resolution shell icons for shortcuts (.lnk, .url), executables, .ico files,
/// and Windows stock icons (such as Empty/Full Recycle Bin), freeing every HICON safely.
/// </summary>
public sealed class IconExtractor
{
    private const string RecycleBinEmptyCacheKey = "__stock_recycle_bin_empty__";
    private const string RecycleBinFullCacheKey = "__stock_recycle_bin_full__";
    private const int TargetIconSize = 48;

    private readonly ConcurrentDictionary<string, BitmapSource> _cache = new(StringComparer.OrdinalIgnoreCase);

    public BitmapSource? GetIcon(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        if (_cache.TryGetValue(filePath, out BitmapSource? cached))
        {
            return cached;
        }

        BitmapSource? extracted = ExtractFileBitmapSource(filePath);
        if (extracted is not null)
        {
            extracted = NormalizeIconPadding(extracted, TargetIconSize);
            _cache[filePath] = extracted;
        }

        return extracted;
    }

    public BitmapSource? GetRecycleBinIcon(bool isFull)
    {
        string key = isFull ? RecycleBinFullCacheKey : RecycleBinEmptyCacheKey;
        if (_cache.TryGetValue(key, out BitmapSource? cached))
        {
            return cached;
        }

        uint stockId = isFull ? NativeMethods.SIID_RECYCLERFULL : NativeMethods.SIID_RECYCLER;
        BitmapSource? extracted = ExtractStockBitmapSource(stockId);
        if (extracted is not null)
        {
            _cache[key] = extracted;
        }

        return extracted;
    }

    public BitmapSource GetSettingsIcon()
    {
        const string key = "__cleanslate_app_icon__";
        if (_cache.TryGetValue(key, out BitmapSource? cached))
        {
            return cached;
        }

        BitmapSource? extracted = LoadCleanSlateAppIcon();
        extracted ??= RenderVectorSettingsIcon(TargetIconSize);

        _cache[key] = extracted;
        return extracted;
    }

    private static BitmapSource? LoadCleanSlateAppIcon()
    {
        // 1. Try pack URI resource from WPF application
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/CleanSlate.ico");
            var streamInfo = Application.GetResourceStream(uri);
            if (streamInfo is not null)
            {
                using (streamInfo.Stream)
                {
                    var decoder = new IconBitmapDecoder(
                        streamInfo.Stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    BitmapSource? normalized = NormalizeFrameToTargetSize(decoder);
                    if (normalized is not null)
                    {
                        return normalized;
                    }
                }
            }
        }
        catch
        {
            // Outside of active WPF Application context
        }

        // 2. Try disk paths
        string[] candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "CleanSlate.ico"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "CleanSlate.ico"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", "CleanSlate.ico"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "CleanSlate.ico")
        };

        foreach (string candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    using var stream = File.OpenRead(candidate);
                    var decoder = new IconBitmapDecoder(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnLoad);
                    BitmapSource? normalized = NormalizeFrameToTargetSize(decoder);
                    if (normalized is not null)
                    {
                        return normalized;
                    }
                }
            }
            catch
            {
            }
        }

        // 3. Try main executable icon resource
        try
        {
            string? exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                BitmapSource? fromExe = ExtractFromIconResource(exe, 0);
                if (fromExe is not null)
                {
                    return fromExe;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static BitmapSource? NormalizeFrameToTargetSize(IconBitmapDecoder decoder)
    {
        if (decoder.Frames.Count == 0)
        {
            return null;
        }

        BitmapFrame? best = decoder.Frames.FirstOrDefault(f => f.PixelWidth == TargetIconSize && f.PixelHeight == TargetIconSize)
            ?? decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - TargetIconSize)).FirstOrDefault();

        if (best is null)
        {
            return null;
        }

        if (best.PixelWidth == TargetIconSize && best.PixelHeight == TargetIconSize)
        {
            if (!best.IsFrozen && best.CanFreeze)
            {
                best.Freeze();
            }
            return best;
        }

        double scale = (double)TargetIconSize / best.PixelWidth;
        var transformed = new TransformedBitmap(best, new System.Windows.Media.ScaleTransform(scale, scale));
        var wb = new WriteableBitmap(transformed);
        wb.Freeze();
        return wb;
    }

    private static BitmapSource RenderVectorSettingsIcon(int size)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (System.Windows.Media.DrawingContext dc = visual.RenderOpen())
        {
            var center = new Point(size / 2.0, size / 2.0);
            double radius = size * 0.44;
            var circleBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x35, 0x64, 0xB5, 0xF6));
            var borderPen = new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x60, 0x64, 0xB5, 0xF6)), 1.5);
            dc.DrawEllipse(circleBrush, borderPen, center, radius, radius);

            var fontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets, Segoe UI Symbol, Arial");
            var typeface = new System.Windows.Media.Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var glyphText = new System.Windows.Media.FormattedText(
                "\uE713",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                size * 0.46,
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x64, 0xB5, 0xF6)),
                1.0);

            var textPoint = new Point(
                center.X - (glyphText.Width / 2.0),
                center.Y - (glyphText.Height / 2.0));
            dc.DrawText(glyphText, textPoint);
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public void Invalidate(string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            _cache.TryRemove(filePath, out _);
        }
    }

    public void Clear() => _cache.Clear();

    public static BitmapSource NormalizeIconPadding(BitmapSource source, int targetSize)
    {
        if (source is null)
        {
            return source!;
        }

        try
        {
            BitmapSource formattedSource = source;
            if (source.Format != System.Windows.Media.PixelFormats.Bgra32 &&
                source.Format != System.Windows.Media.PixelFormats.Pbgra32)
            {
                formattedSource = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            }

            var wb = new WriteableBitmap(formattedSource);
            int w = wb.PixelWidth;
            int h = wb.PixelHeight;
            if (w <= 0 || h <= 0)
            {
                return source;
            }

            int stride = w * 4;
            byte[] pixels = new byte[h * stride];
            wb.CopyPixels(pixels, stride, 0);

            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = (y * stride) + (x * 4);
                    byte a = pixels[idx + 3];
                    if (a > 25)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            if (maxX < minX || maxY < minY)
            {
                return source;
            }

            int contentW = maxX - minX + 1;
            int contentH = maxY - minY + 1;

            double fillRatioX = (double)contentW / w;
            double fillRatioY = (double)contentH / h;

            // If an extracted icon has significant transparent padding (content occupies < 75% in both dimensions),
            // crop the content and upscale it smoothly to fill the target tile size.
            if (fillRatioX < 0.75 && fillRatioY < 0.75 && contentW >= 6 && contentH >= 6)
            {
                var cropped = new CroppedBitmap(wb, new Int32Rect(minX, minY, contentW, contentH));

                double destSize = targetSize - 4.0;
                double scale = Math.Min(destSize / contentW, destSize / contentH);
                double scaledW = Math.Round(contentW * scale);
                double scaledH = Math.Round(contentH * scale);
                double offsetX = Math.Round((targetSize - scaledW) / 2.0);
                double offsetY = Math.Round((targetSize - scaledH) / 2.0);

                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(cropped, new Rect(offsetX, offsetY, scaledW, scaledH));
                }

                var outputRtb = new RenderTargetBitmap(targetSize, targetSize, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                outputRtb.Render(dv);
                outputRtb.Freeze();
                return outputRtb;
            }
        }
        catch
        {
            // Fall back to original image on any processing error
        }

        return source;
    }

    private static BitmapSource? ExtractFileBitmapSource(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        string ext = Path.GetExtension(filePath);

        // 1. For .url files (e.g. Steam game shortcuts), parse IconFile/IconIndex first
        if (string.Equals(ext, ".url", StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
        {
            if (TryParseUrlIconLocation(filePath, out string urlIconFile, out int urlIconIndex))
            {
                if (File.Exists(urlIconFile))
                {
                    BitmapSource? directUrl = ExtractViaShellItemImageFactory(urlIconFile, TargetIconSize);
                    directUrl ??= ExtractFromIconResource(urlIconFile, urlIconIndex);
                    if (directUrl is not null)
                    {
                        return directUrl;
                    }
                }
            }

            // Fall back to extracting from the .url file itself
            BitmapSource? fromUrlFile = ExtractViaShellItemImageFactory(filePath, TargetIconSize);
            if (fromUrlFile is not null)
            {
                return fromUrlFile;
            }
        }

        // 2. For .lnk files:
        if (string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
        {
            string? lnkIconFile = null;
            int lnkIconIndex = 0;
            string? lnkTargetPath = null;
            string? lnkArguments = null;

            if (TryGetLnkDetails(filePath, out lnkIconFile, out lnkIconIndex, out lnkTargetPath, out lnkArguments))
            {
                // A. Check for Store / UWP / AUMID apps (e.g. Snapchat, WhatsApp, Windows Terminal, etc.)
                // Pattern: shell:AppsFolder\<PackageFamilyName>!...
                string? aumidCandidate = null;
                if (!string.IsNullOrWhiteSpace(lnkArguments) && lnkArguments.Contains("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
                {
                    int idx = lnkArguments.IndexOf("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase);
                    aumidCandidate = lnkArguments[idx..].Trim().Trim('"');
                }
                else if (!string.IsNullOrWhiteSpace(lnkTargetPath) && lnkTargetPath.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
                {
                    aumidCandidate = lnkTargetPath.Trim();
                }

                if (!string.IsNullOrWhiteSpace(aumidCandidate))
                {
                    BitmapSource? fromAumid = ExtractViaShellItemImageFactory(aumidCandidate, TargetIconSize);
                    if (fromAumid is not null)
                    {
                        return fromAumid;
                    }
                }

                // B. If an explicit IconFile was specified AND exists on disk, extract from it
                if (!string.IsNullOrWhiteSpace(lnkIconFile) && File.Exists(lnkIconFile))
                {
                    BitmapSource? directLnkIcon = ExtractViaShellItemImageFactory(lnkIconFile, TargetIconSize);
                    directLnkIcon ??= ExtractFromIconResource(lnkIconFile, lnkIconIndex);
                    if (directLnkIcon is not null)
                    {
                        return directLnkIcon;
                    }
                }

                // C. If target path exists on disk (and is not generic explorer.exe), extract from target executable
                if (!string.IsNullOrWhiteSpace(lnkTargetPath) && File.Exists(lnkTargetPath))
                {
                    if (!lnkTargetPath.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        BitmapSource? fromTarget = ExtractViaShellItemImageFactory(lnkTargetPath, TargetIconSize);
                        fromTarget ??= ExtractFromIconResource(lnkTargetPath, 0);
                        if (fromTarget is not null)
                        {
                            return fromTarget;
                        }
                    }
                }
            }

            // D. Try extracting from the .lnk directly (only if IconLocation wasn't pointing to a missing/deleted file)
            if (string.IsNullOrWhiteSpace(lnkIconFile) || File.Exists(lnkIconFile))
            {
                BitmapSource? lnkShell = ExtractViaShellItemImageFactory(filePath, TargetIconSize);
                if (lnkShell is not null)
                {
                    return lnkShell;
                }
            }
        }

        // 3. General shell extraction via IShellItemImageFactory for executables, documents, folders, or other files
        BitmapSource? shellItemIcon = ExtractViaShellItemImageFactory(filePath, TargetIconSize);
        if (shellItemIcon is not null)
        {
            return shellItemIcon;
        }

        // 4. Secondary legacy fallback
        return ExtractViaShellGetFileInfo(filePath);
    }

    private static BitmapSource? ExtractViaShellItemImageFactory(string parsingName, int targetSize = TargetIconSize)
    {
        if (string.IsNullOrWhiteSpace(parsingName))
        {
            return null;
        }

        NativeMethods.IShellItemImageFactory? factory = null;
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            Guid iid = NativeMethods.IID_IShellItemImageFactory;
            int hr = NativeMethods.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out factory);
            if (hr != 0 || factory is null)
            {
                return null;
            }

            var size = new NativeMethods.SIZE(targetSize, targetSize);
            hr = factory.GetImage(size, NativeMethods.SIIGBF_ICONONLY, out hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                hr = factory.GetImage(size, NativeMethods.SIIGBF_RESIZETOFIT, out hBitmap);
            }

            if (hr == 0 && hBitmap != IntPtr.Zero)
            {
                BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
        }
        catch
        {
            // Fall through to callers
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(hBitmap);
            }

            if (factory is not null)
            {
                Marshal.ReleaseComObject(factory);
            }
        }

        return null;
    }

    private static BitmapSource? ExtractFromIconResource(string iconFile, int iconIndex)
    {
        if (string.IsNullOrWhiteSpace(iconFile) || !File.Exists(iconFile))
        {
            return null;
        }

        // Pure managed .ico decoding when index == 0 preserves multi-resolution 48x48/256x256 PNG/RGBA frames cleanly.
        if (iconIndex == 0 &&
            string.Equals(Path.GetExtension(iconFile), ".ico", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using FileStream fs = new(iconFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count > 0)
                {
                    BitmapFrame bestFrame = decoder.Frames
                        .OrderBy(f => f.PixelWidth >= TargetIconSize ? 0 : 1)
                        .ThenBy(f => f.PixelWidth >= TargetIconSize ? f.PixelWidth : -f.PixelWidth)
                        .ThenByDescending(f => f.Format.BitsPerPixel)
                        .First();

                    const int maxCachedIconPx = 96;
                    BitmapSource normalized = bestFrame;
                    if (bestFrame.PixelWidth > maxCachedIconPx || bestFrame.PixelHeight > maxCachedIconPx)
                    {
                        double factor = maxCachedIconPx / (double)Math.Max(bestFrame.PixelWidth, bestFrame.PixelHeight);
                        normalized = new TransformedBitmap(
                            bestFrame,
                            new System.Windows.Media.ScaleTransform(factor, factor));
                    }

                    var wb = new WriteableBitmap(normalized);
                    wb.Freeze();
                    return wb;
                }
            }
            catch
            {
                // Fall back to Win32 SHDefExtractIconW for unusual .ico formats.
            }
        }

        IntPtr hLarge = IntPtr.Zero;
        IntPtr hSmall = IntPtr.Zero;
        try
        {
            uint sizes = (uint)((TargetIconSize & 0xFFFF) | (16 << 16));
            int hr = NativeMethods.SHDefExtractIconW(iconFile, iconIndex, 0, out hLarge, out hSmall, sizes);
            if (hr == 0 && hLarge != IntPtr.Zero)
            {
                return ConvertAndFreezeIcon(hLarge);
            }

            if (hr == 0 && hSmall != IntPtr.Zero)
            {
                return ConvertAndFreezeIcon(hSmall);
            }
        }
        catch
        {
            // Fall through to shell extraction.
        }
        finally
        {
            if (hLarge != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(hLarge);
            }

            if (hSmall != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(hSmall);
            }
        }

        return null;
    }

    private static BitmapSource? ExtractViaShellGetFileInfo(string filePath)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            bool exists = File.Exists(filePath) || Directory.Exists(filePath);
            uint baseFlags = exists ? 0u : NativeMethods.SHGFI_USEFILEATTRIBUTES;
            uint attributes = exists ? 0u : NativeMethods.FILE_ATTRIBUTE_NORMAL;

            var shfi = new NativeMethods.SHFILEINFOW();
            IntPtr res = NativeMethods.SHGetFileInfoW(
                filePath,
                attributes,
                ref shfi,
                (uint)Marshal.SizeOf<NativeMethods.SHFILEINFOW>(),
                NativeMethods.SHGFI_SYSICONINDEX | baseFlags);

            if (res != IntPtr.Zero && shfi.iIcon >= 0)
            {
                hIcon = ExtractExtraLargeIconByIndex(shfi.iIcon);
            }

            if (hIcon == IntPtr.Zero)
            {
                var fallbackInfo = new NativeMethods.SHFILEINFOW();
                IntPtr fallbackRes = NativeMethods.SHGetFileInfoW(
                    filePath,
                    attributes,
                    ref fallbackInfo,
                    (uint)Marshal.SizeOf<NativeMethods.SHFILEINFOW>(),
                    NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON | baseFlags);

                if (fallbackRes != IntPtr.Zero)
                {
                    hIcon = fallbackInfo.hIcon;
                }
            }

            return ConvertAndFreezeIcon(hIcon);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(hIcon);
            }
        }
    }

    private static bool TryParseUrlIconLocation(string urlPath, out string iconFile, out int iconIndex)
    {
        iconFile = string.Empty;
        iconIndex = 0;

        try
        {
            foreach (string rawLine in File.ReadLines(urlPath))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                {
                    string rawPath = line["IconFile=".Length..].Trim().Trim('"');
                    iconFile = Environment.ExpandEnvironmentVariables(rawPath);
                }
                else if (line.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(line["IconIndex=".Length..].Trim(), out iconIndex);
                }
            }
        }
        catch
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(iconFile);
    }

    private static bool TryGetLnkDetails(
        string lnkPath,
        out string? iconFile,
        out int iconIndex,
        out string? targetPath,
        out string? arguments)
    {
        iconFile = null;
        iconIndex = 0;
        targetPath = null;
        arguments = null;

        NativeMethods.IShellLinkW? shellLink = null;
        try
        {
            shellLink = (NativeMethods.IShellLinkW)new NativeMethods.ShellLink();
            if (shellLink is System.Runtime.InteropServices.ComTypes.IPersistFile persistFile)
            {
                persistFile.Load(lnkPath, 0);

                var iconSb = new StringBuilder(260);
                shellLink.GetIconLocation(iconSb, iconSb.Capacity, out iconIndex);
                string rawIcon = iconSb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(rawIcon))
                {
                    iconFile = Environment.ExpandEnvironmentVariables(rawIcon);
                }

                var targetSb = new StringBuilder(260);
                shellLink.GetPath(targetSb, targetSb.Capacity, IntPtr.Zero, 0);
                string rawTarget = targetSb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(rawTarget))
                {
                    targetPath = Environment.ExpandEnvironmentVariables(rawTarget);
                }

                var argsSb = new StringBuilder(1024);
                shellLink.GetArguments(argsSb, argsSb.Capacity);
                string rawArgs = argsSb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(rawArgs))
                {
                    arguments = rawArgs;
                }

                return true;
            }
        }
        catch
        {
            // Ignore malformed .lnk files and fall back to standard shell extraction.
        }
        finally
        {
            if (shellLink is not null)
            {
                Marshal.ReleaseComObject(shellLink);
            }
        }

        return false;
    }

    private static BitmapSource? ExtractStockBitmapSource(uint stockIconId)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            var sii = new NativeMethods.SHSTOCKICONINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.SHSTOCKICONINFO>()
            };

            if (NativeMethods.SHGetStockIconInfo(stockIconId, NativeMethods.SHGSI_SYSICONINDEX, ref sii) == 0 &&
                sii.iSysImageIndex >= 0)
            {
                hIcon = ExtractExtraLargeIconByIndex(sii.iSysImageIndex);
            }

            if (hIcon == IntPtr.Zero)
            {
                var fallbackInfo = new NativeMethods.SHSTOCKICONINFO
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.SHSTOCKICONINFO>()
                };

                if (NativeMethods.SHGetStockIconInfo(
                        stockIconId,
                        NativeMethods.SHGSI_ICON | NativeMethods.SHGSI_LARGEICON,
                        ref fallbackInfo) == 0)
                {
                    hIcon = fallbackInfo.hIcon;
                }
            }

            return ConvertAndFreezeIcon(hIcon);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(hIcon);
            }
        }
    }

    private static IntPtr ExtractExtraLargeIconByIndex(int systemIconIndex)
    {
        NativeMethods.IImageList? imageList = null;
        try
        {
            Guid iid = NativeMethods.IID_IImageList;
            int hr = NativeMethods.SHGetImageList(NativeMethods.SHIL_EXTRALARGE, ref iid, out imageList);
            if (hr != 0 || imageList is null)
            {
                return IntPtr.Zero;
            }

            hr = imageList.GetIcon(systemIconIndex, NativeMethods.ILD_TRANSPARENT, out IntPtr hIcon);
            return hr == 0 ? hIcon : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
        finally
        {
            if (imageList is not null)
            {
                Marshal.ReleaseComObject(imageList);
            }
        }
    }

    private static BitmapSource? ConvertAndFreezeIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero)
        {
            return null;
        }

        BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
            hIcon,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }
}
