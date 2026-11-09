using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Blish_HUD {

    /// <summary>
    /// Wine/Proton only.  Publishes Blish HUD's rendered overlay frame into shared memory so that the Nexus addon
    /// "Blish HUD Nexus Render Bridge" (https://github.com/okolvik/Blish-HUD-NexusRenderBridge), loaded inside the game,
    /// can draw it as part of the game's own frame.  Used instead of a separate transparent window, which is slow under
    /// Wine because every update has to go through XWayland.
    /// </summary>
    /// <remarks>
    /// The shared block layout and the lock protocol must match <c>NexusRenderBridge.cpp</c> in the addon repository.
    /// </remarks>
    internal static unsafe class NexusBridge {

        private static readonly Logger Logger = Logger.GetLogger(typeof(NexusBridge));

        private const string MAP_NAME   = "BlishHudNexusRenderBridge_Frame";
        private const string READY_NAME = "BlishHudNexusRenderBridge_Ready";
        private const string MUTEX_NAME = "BlishHudNexusRenderBridge_Lock";

        private const uint MAGIC        = 0x58484E42; // 'B','N','H','X'
        private const int  VERSION      = 2;
        private const int  PIXEL_OFFSET = 4096;
        private const int  MAX_WIDTH    = 5120;
        private const int  MAX_HEIGHT   = 2880;

        // Header field offsets.
        private const int OFF_MAGIC = 0, OFF_VERSION = 4, OFF_WIDTH = 8, OFF_HEIGHT = 12, OFF_STRIDE = 16, OFF_PIXEL_OFFSET = 20,
                          OFF_LOCK = 24, OFF_SEQ = 28, OFF_DIRTY = 32 /* left, top, right, bottom */, OFF_HEARTBEAT = 48,
                          OFF_GEN = 52, OFF_VISIBLE = 56;

        private const uint SYNCHRONIZE       = 0x00100000;
        private const uint PAGE_READWRITE    = 0x04;
        private const uint FILE_MAP_ALL_ACCESS = 0x000F001F;

        private const uint WAIT_OBJECT_0  = 0;
        private const uint WAIT_ABANDONED = 0x80;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateMutexW(IntPtr attrs, bool initialOwner, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReleaseMutex(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenEventW(uint access, bool inherit, string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr attrs, uint protect, uint sizeHigh, uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHigh, uint offLow, UIntPtr bytes);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern uint GetTickCount();

        [DllImport("kernel32.dll")]
        private static extern bool UnmapViewOfFile(IntPtr address);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION {
            public IntPtr  BaseAddress, AllocationBase;
            public uint    AllocationProtect;
            public UIntPtr RegionSize;
            public uint    State, Protect, Type;
        }

        [DllImport("kernel32.dll")]
        private static extern UIntPtr VirtualQuery(IntPtr address, out MEMORY_BASIC_INFORMATION info, UIntPtr length);

        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int memcmp(byte* a, byte* b, UIntPtr count);

        private static IntPtr _map;
        private static IntPtr _mutex;
        private static byte*  _view;
        private static uint   _nextProbeTicks;
        private static bool   _probed;
        private static bool   _active;
        private static bool   _needFull = true;

        private static int[] _spanLeft  = new int[0];
        private static int[] _spanRight = new int[0];

        /// <summary>True when the addon is loaded in the game and frames are being published to it.</summary>
        internal static bool Active => _active;

        /// <summary>
        /// Call once per overlay frame: looks for the addon (once a second) and keeps the heartbeat the addon uses to tell
        /// that Blish HUD is alive.
        /// </summary>
        internal static void Tick() {
            uint now = GetTickCount(); // the same clock the addon uses for the heartbeat

            if (!_probed || (int) (now - _nextProbeTicks) >= 0) {
                _probed         = true;
                _nextProbeTicks = now + 1000;

                IntPtr readyEvent  = OpenEventW(SYNCHRONIZE, false, READY_NAME);
                bool   addonLoaded = readyEvent != IntPtr.Zero;
                if (addonLoaded) CloseHandle(readyEvent);

                if (addonLoaded && !_active && EnsureMapping()) {
                    _active   = true;
                    _needFull = true;
                    Logger.Info("Nexus render bridge addon detected; publishing overlay frames to the game instead of using an overlay window.");
                } else if (!addonLoaded && _active) {
                    _active = false;
                    if (_view != null) *(int*) (_view + OFF_VISIBLE) = 0;
                    Logger.Info("Nexus render bridge addon went away; falling back to the overlay window.");
                }
            }

            if (_active && _view != null) {
                *(int*) (_view + OFF_HEARTBEAT) = (int) now;
                *(int*) (_view + OFF_VISIBLE)   = 1;
            }
        }

        private static bool EnsureMapping() {
            if (_view != null) return true;

            long size = PIXEL_OFFSET + (long) MAX_WIDTH * MAX_HEIGHT * 4;
            _map = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE, (uint) (size >> 32), (uint) (size & 0xFFFFFFFF), MAP_NAME);
            if (_map == IntPtr.Zero) return false;

            IntPtr view = MapViewOfFile(_map, FILE_MAP_ALL_ACCESS, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero) {
                CloseHandle(_map);
                _map = IntPtr.Zero;
                return false;
            }

            // The block is guarded by a named mutex shared with the addon, created before the header becomes valid.  A mutex is
            // used (not a lock word with a timeout) because the OS reports abandonment only when the owning thread has really
            // died; a lock that merely looks old may belong to a paused but live owner.
            if (_mutex == IntPtr.Zero) _mutex = CreateMutexW(IntPtr.Zero, false, MUTEX_NAME);
            if (_mutex == IntPtr.Zero) {
                UnmapViewOfFile(view);
                CloseHandle(_map);
                _map = IntPtr.Zero;
                return false;
            }

            // The addon keeps this block alive across a Blish HUD restart, so it may be an older, smaller one.
            long required = PIXEL_OFFSET + (long) MAX_WIDTH * MAX_HEIGHT * 4;
            if (VirtualQuery(view, out MEMORY_BASIC_INFORMATION info, (UIntPtr) Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == UIntPtr.Zero
             || (long) info.RegionSize < required) {
                Logger.Warn("The shared frame block left by the render bridge addon is too small for this Blish HUD build; restart the game to use the bridge.");
                UnmapViewOfFile(view);
                CloseHandle(_map);
                _map = IntPtr.Zero;
                return false;
            }

            _view = (byte*) view;

            // Neither the mutex nor the dirty rectangle is touched here (the addon may be using them right now): _needFull makes
            // the first publish a full one, and the addon clamps whatever rectangle it finds.
            *(uint*) (_view + OFF_VERSION)      = VERSION;
            *(uint*) (_view + OFF_PIXEL_OFFSET) = PIXEL_OFFSET;
            *(uint*) (_view + OFF_MAGIC)        = MAGIC; // last: the addon only accepts a block whose magic is set
            return true;
        }

        /// <summary>
        /// Publishes a BGRA frame (stride = width * 4) by comparing it against the previous frame, which is simply what is
        /// already in the shared pixel buffer, and copying only the pixels that changed.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the frame is now reflected in the shared buffer (including "nothing changed"); <c>false</c> if the
        /// bridge could not be used right now (inactive, frame too large, or the addon currently holds the mutex).  In that
        /// case nothing was modified, so the next call recomputes against the unchanged buffer and no change is lost.
        /// </returns>
        internal static bool Publish(byte[] bgraFrame, int width, int height) {
            if (!CanPublish(width, height)) return false;

            fixed (byte* frame = bgraFrame) {
                return Publish(frame, width, height);
            }
        }

        /// <summary>True if the bridge is active and a frame of this size fits in the shared block.</summary>
        internal static bool CanPublish(int width, int height) => _active && _view != null && width <= MAX_WIDTH && height <= MAX_HEIGHT;

        private static bool Publish(byte* frame, int width, int height) {
            int   stride = width * 4;
            byte* pixels = _view + PIXEL_OFFSET;
            bool  full   = _needFull || *(int*) (_view + OFF_WIDTH) != width || *(int*) (_view + OFF_HEIGHT) != height;

            int top = -1, bottom = -1, left = int.MaxValue, right = -1;

            if (!full) {
                if (_spanLeft.Length < height) {
                    _spanLeft  = new int[height];
                    _spanRight = new int[height];
                }

                // Pass 1 (no lock needed; only Blish HUD writes the pixels): the changed span of every row.
                int pairs = stride / 8; // two pixels at a time
                for (int y = 0; y < height; y++) {
                    byte* a = frame  + (long) y * stride;
                    byte* b = pixels + (long) y * stride;

                    if (memcmp(a, b, (UIntPtr) stride) == 0) {
                        _spanLeft[y] = -1;
                        continue;
                    }

                    ulong* pa = (ulong*) a;
                    ulong* pb = (ulong*) b;

                    int first = 0;
                    while (first < pairs && pa[first] == pb[first]) first++;

                    int spanLeft, spanRight;
                    if (first == pairs) {
                        // Only the trailing pixel of an odd-width frame can differ.
                        spanLeft  = width - 1;
                        spanRight = width;
                    } else {
                        int last = pairs - 1;
                        while (last > first && pa[last] == pb[last]) last--;

                        spanLeft  = first * 2;
                        spanRight = Math.Min(width, last * 2 + 2);
                        if ((width & 1) != 0 && ((uint*) a)[width - 1] != ((uint*) b)[width - 1]) spanRight = width;
                    }

                    _spanLeft[y]  = spanLeft;
                    _spanRight[y] = spanRight;

                    if (top < 0) top = y;
                    bottom = y;
                    if (spanLeft  < left)  left  = spanLeft;
                    if (spanRight > right) right = spanRight;
                }

                if (top < 0) return true; // identical frame
            }

            // Pass 2: copy what changed and publish it.  Non-blocking: if the addon is reading right now, try again next frame.
            uint wait = WaitForSingleObject(_mutex, 0);
            if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) return false;

            // Abandoned: the addon's thread died while reading, which cannot tear what we wrote, but the dirty rectangle it was
            // consuming may be lost, so refresh everything.
            if (wait == WAIT_ABANDONED) full = true;

            try {
                if (full) {
                    *(int*) (_view + OFF_WIDTH)  = width;
                    *(int*) (_view + OFF_HEIGHT) = height;
                    *(int*) (_view + OFF_STRIDE) = stride;
                    *(int*) (_view + OFF_GEN)   += 1;
                    _needFull = false;

                    long total = (long) height * stride;
                    Buffer.MemoryCopy(frame, pixels, total, total);

                    left = 0; top = 0; right = width; bottom = height - 1;
                } else {
                    for (int y = top; y <= bottom; y++) {
                        int spanLeft = _spanLeft[y];
                        if (spanLeft < 0) continue;

                        long offset = (long) y * stride + (long) spanLeft * 4;
                        long length = (long) (_spanRight[y] - spanLeft) * 4;
                        Buffer.MemoryCopy(frame + offset, pixels + offset, length, length);
                    }
                }

                // Union into the accumulated dirty rectangle, which the addon consumes and resets.
                int* dirty = (int*) (_view + OFF_DIRTY);
                if (left       < dirty[0]) dirty[0] = left;
                if (top        < dirty[1]) dirty[1] = top;
                if (right      > dirty[2]) dirty[2] = right;
                if (bottom + 1 > dirty[3]) dirty[3] = bottom + 1;

                *(int*) (_view + OFF_SEQ) += 1;
            } finally {
                ReleaseMutex(_mutex);
            }

            return true;
        }

    }
}
