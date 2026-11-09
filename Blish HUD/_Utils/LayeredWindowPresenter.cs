using System;
using System.Runtime.InteropServices;

namespace Blish_HUD {

    /// <summary>
    /// Wine/Proton only.  Presents Blish HUD's off-screen overlay frame through a layered window with per-pixel alpha
    /// (<c>UpdateLayeredWindow</c>).  This is the fallback used when the Nexus render bridge addon isn't loaded: it works,
    /// but every update has to go through XWayland, so large changing areas (e.g. Pathing trails) can hurt the game.
    /// Only the changed rectangle is copied and reported to Wine as dirty, and identical frames are skipped.
    /// </summary>
    internal static unsafe class LayeredWindowPresenter {

        private static readonly Logger Logger = Logger.GetLogger(typeof(LayeredWindowPresenter));

        #region Win32

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER {
            public uint   biSize;
            public int    biWidth;
            public int    biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint   biCompression;
            public uint   biSizeImage;
            public int    biXPelsPerMeter;
            public int    biYPelsPerMeter;
            public uint   biClrUsed;
            public uint   biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Size {
            public int cx;
            public int cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinRect {
            public int left, top, right, bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UpdateLayeredWindowInfo {
            public uint   cbSize;
            public IntPtr hdcDst;
            public IntPtr pptDst;
            public IntPtr psize;
            public IntPtr hdcSrc;
            public IntPtr pptSrc;
            public uint   crKey;
            public IntPtr pblend;
            public uint   dwFlags;
            public IntPtr prcDirty;
        }

        private const uint ULW_ALPHA = 2;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref Win32Size psize, IntPtr hdcSrc, ref System.Drawing.Point pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindowIndirect(IntPtr hwnd, ref UpdateLayeredWindowInfo info);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr ho);

        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int memcmp(byte* a, byte* b, UIntPtr count);

        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr memcpy(void* dst, void* src, UIntPtr count);

        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr memset(void* dst, int value, UIntPtr count);

        #endregion

        private static IntPtr _dc, _dib, _oldBitmap, _bits;
        private static int    _width, _height;

        /// <summary>
        /// Wine only gives a layered window a 32-bit (alpha capable) X11 visual when it is driven through
        /// <c>UpdateLayeredWindow</c>; <c>SetLayeredWindowAttributes</c> yields an opaque 24-bit one.  Pushing a single
        /// fully transparent premultiplied-alpha pixel is enough to get the alpha visual.
        /// </summary>
        internal static void InitializeArgbSurface(IntPtr windowHandle) {
            Logger.Info("Running under Wine: giving the overlay an ARGB layered surface via UpdateLayeredWindow.");

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc    = CreateCompatibleDC(screenDc);

            var bmi = new BITMAPINFOHEADER {
                biSize     = (uint) Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                biWidth    = 1,
                biHeight   = -1,
                biPlanes   = 1,
                biBitCount = 32
            };

            IntPtr dib = CreateDIBSection(screenDc, ref bmi, 0, out _, IntPtr.Zero, 0);
            IntPtr old = SelectObject(memDc, dib);

            var size  = new Win32Size { cx = 1, cy = 1 };
            var src   = System.Drawing.Point.Empty;
            var blend = new BLENDFUNCTION { BlendOp = 0 /* AC_SRC_OVER */, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };

            if (!UpdateLayeredWindow(windowHandle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA)) {
                Logger.Warn($"{nameof(UpdateLayeredWindow)} failed with error code {Marshal.GetLastWin32Error()}.");
            }

            SelectObject(memDc, old);
            DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        /// <summary>
        /// Pushes a rendered, premultiplied-alpha frame into the overlay's layered window.  <paramref name="current"/> and
        /// <paramref name="previous"/> are width * height * 4 bytes; <paramref name="previous"/> is updated here.
        /// <paramref name="bgra"/> says whether the frame is already BGRA (otherwise it is RGBA and gets swizzled).
        /// </summary>
        internal static void Present(IntPtr windowHandle, byte[] current, byte[] previous, int width, int height, bool bgra) {
            bool resized = EnsureSurface(width, height);

            int stride = width * 4;
            int top = -1, bottom = -1, left = width, right = -1;

            fixed (byte* cur = current, prev = previous) {
                if (resized) {
                    top = 0; bottom = height - 1;
                    left = 0; right = width - 1;
                } else {
                    // Which rows changed?
                    for (int y = 0; y < height; y++) {
                        if (memcmp(cur + (long) y * stride, prev + (long) y * stride, (UIntPtr) stride) != 0) {
                            if (top < 0) top = y;
                            bottom = y;
                        }
                    }

                    if (top < 0) return; // nothing changed

                    // Tighten to the exact changed columns within the changed rows.
                    int pairs = stride / 8; // two pixels at a time
                    for (int y = top; y <= bottom; y++) {
                        ulong* a = (ulong*) (cur  + (long) y * stride);
                        ulong* b = (ulong*) (prev + (long) y * stride);

                        int first = 0;
                        while (first < pairs && a[first] == b[first]) first++;

                        if (first == pairs) {
                            // Only a trailing odd pixel (if any) can differ.
                            if ((width & 1) != 0 && ((uint*) a)[width - 1] != ((uint*) b)[width - 1]) {
                                left  = Math.Min(left, width - 1);
                                right = width - 1;
                            }
                            continue;
                        }

                        int last = pairs - 1;
                        while (last > first && a[last] == b[last]) last--;

                        left  = Math.Min(left, first * 2);
                        right = Math.Max(right, last * 2 + 1);
                        if ((width & 1) != 0 && ((uint*) a)[width - 1] != ((uint*) b)[width - 1]) right = width - 1;
                    }

                    if (right < left) { left = 0; right = width - 1; }
                }

                byte* dst  = (byte*) _bits;
                long  span = (long) (right - left + 1) * 4;

                for (int y = top; y <= bottom; y++) {
                    long off = (long) y * stride + (long) left * 4;

                    if (bgra) {
                        memcpy(dst + off, cur + off, (UIntPtr) span);
                    } else {
                        uint* s = (uint*) (cur + off);
                        uint* d = (uint*) (dst + off);
                        long  n = span / 4;
                        for (long i = 0; i < n; i++) {
                            uint c = s[i];
                            d[i] = (c & 0xFF00FF00u) | ((c & 0x00FF0000u) >> 16) | ((c & 0x000000FFu) << 16);
                        }
                    }

                    memcpy(prev + off, cur + off, (UIntPtr) span);
                }
            }

            PushToWindow(windowHandle, width, height, new WinRect { left = left, top = top, right = right + 1, bottom = bottom + 1 });
        }

        /// <summary>
        /// Blanks the window (fully transparent).  Used when the Nexus bridge takes over so the last frame shown here
        /// doesn't stay on screen underneath the in-game overlay.
        /// </summary>
        internal static void Clear(IntPtr windowHandle) {
            if (_dib == IntPtr.Zero) return;

            memset((void*) _bits, 0, (UIntPtr) ((long) _width * _height * 4));
            PushToWindow(windowHandle, _width, _height, new WinRect { left = 0, top = 0, right = _width, bottom = _height });
        }

        /// <summary>
        /// Forces the next <see cref="Present"/> to redraw the whole frame (used when falling back from the bridge).
        /// </summary>
        internal static void Reset() {
            _width = 0;
            _height = 0;
        }

        private static bool EnsureSurface(int width, int height) {
            if (_dib != IntPtr.Zero && _width == width && _height == height) return false;

            if (_dib != IntPtr.Zero) {
                SelectObject(_dc, _oldBitmap);
                DeleteObject(_dib);
                DeleteDC(_dc);
            }

            IntPtr screenDc = GetDC(IntPtr.Zero);
            _dc = CreateCompatibleDC(screenDc);

            var bmi = new BITMAPINFOHEADER {
                biSize     = (uint) Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                biWidth    = width,
                biHeight   = -height,
                biPlanes   = 1,
                biBitCount = 32
            };

            _dib       = CreateDIBSection(screenDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            _oldBitmap = SelectObject(_dc, _dib);
            _width     = width;
            _height    = height;
            ReleaseDC(IntPtr.Zero, screenDc);

            return true;
        }

        private static void PushToWindow(IntPtr windowHandle, int width, int height, WinRect dirty) {
            var size  = new Win32Size { cx = width, cy = height };
            var src   = System.Drawing.Point.Empty;
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };

            IntPtr pSize  = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Win32Size)));
            IntPtr pSrc   = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(System.Drawing.Point)));
            IntPtr pBlend = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(BLENDFUNCTION)));
            IntPtr pDirty = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinRect)));

            try {
                Marshal.StructureToPtr(size,  pSize,  false);
                Marshal.StructureToPtr(src,   pSrc,   false);
                Marshal.StructureToPtr(blend, pBlend, false);
                Marshal.StructureToPtr(dirty, pDirty, false);

                var info = new UpdateLayeredWindowInfo {
                    cbSize   = (uint) Marshal.SizeOf(typeof(UpdateLayeredWindowInfo)),
                    psize    = pSize,
                    hdcSrc   = _dc,
                    pptSrc   = pSrc,
                    pblend   = pBlend,
                    dwFlags  = ULW_ALPHA,
                    prcDirty = pDirty
                };

                if (!UpdateLayeredWindowIndirect(windowHandle, ref info)) {
                    Logger.Warn($"{nameof(UpdateLayeredWindowIndirect)} failed with error code {Marshal.GetLastWin32Error()}.");
                }
            } finally {
                Marshal.FreeHGlobal(pSize);
                Marshal.FreeHGlobal(pSrc);
                Marshal.FreeHGlobal(pBlend);
                Marshal.FreeHGlobal(pDirty);
            }
        }

    }
}
