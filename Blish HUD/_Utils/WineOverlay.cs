using System;
using System.Diagnostics;
using Microsoft.Xna.Framework.Graphics;

namespace Blish_HUD {

    /// <summary>
    /// Wine/Proton only.  Under Wine a transparent overlay window cannot show the DirectX swapchain with per-pixel alpha, so
    /// Blish HUD renders each frame off-screen, reads it back, and hands it to one of two presenters:
    /// <list type="bullet">
    ///   <item><description>the Nexus render bridge addon (<see cref="NexusBridge"/>), which draws the frame inside the game, or</description></item>
    ///   <item><description>a layered window (<see cref="LayeredWindowPresenter"/>), used when the addon isn't loaded.</description></item>
    /// </list>
    /// </summary>
    internal sealed class WineOverlay {

        private static readonly Logger Logger = Logger.GetLogger<WineOverlay>();

        // Redraw caps.  The bridge composites the overlay every game frame, so a higher rate keeps moving content (such as
        // Pathing trails) in step with the camera.  BLISH_WINE_FPS overrides both.
        private const int BRIDGE_FPS = 60;
        private const int WINDOW_FPS = 30;

        private readonly int _fpsOverride = int.TryParse(Environment.GetEnvironmentVariable("BLISH_WINE_FPS"), out int fps) && fps > 0 ? fps : 0;

        private RenderTarget2D _target;
        private byte[]         _frame;
        private byte[]         _previousFrame;
        private bool           _bgra;
        private bool           _wasBridge;
        private long           _lastDrawTicks;

        private int MaxFps => _fpsOverride > 0 ? _fpsOverride : (NexusBridge.Active ? BRIDGE_FPS : WINDOW_FPS);

        /// <summary>
        /// Prepares the off-screen target for this frame.  Returns <c>false</c> if the frame should be skipped (frame cap).
        /// </summary>
        internal bool BeginFrame(GraphicsDevice device) {
            NexusBridge.Tick(); // looks for the bridge addon and keeps its heartbeat alive

            // The game loop ticks at a fixed rate, so accept a frame slightly early rather than skipping to the tick after
            // (which would quietly halve the rate), and advance the deadline by whole intervals to avoid drift.
            long interval = Stopwatch.Frequency / this.MaxFps;
            long now      = Stopwatch.GetTimestamp();
            if (now - _lastDrawTicks < interval - interval / 10) return false;
            _lastDrawTicks = now - _lastDrawTicks > 2 * interval ? now : _lastDrawTicks + interval;

            using (GameService.Graphics.LendGraphicsDeviceContext()) {
                device.SetRenderTarget(GetTarget(device));
            }
            return true;
        }

        /// <summary>
        /// Reads the finished frame back from the GPU and presents it.
        /// </summary>
        internal void EndFrame(GraphicsDevice device, IntPtr formHandle) {
            using (GameService.Graphics.LendGraphicsDeviceContext()) {
                device.SetRenderTarget(null);
                _target.GetData(_frame);
            }

            // The bridge needs BGRA (if the platform only gave us RGBA the layered window path does the channel swap), and the
            // frame must fit the shared block; otherwise fall back to the window.
            bool useBridge = _bgra && NexusBridge.CanPublish(_target.Width, _target.Height);

            if (useBridge) {
                if (!_wasBridge) LayeredWindowPresenter.Clear(formHandle); // don't leave a stale frame in the window
                NexusBridge.Publish(_frame, _target.Width, _target.Height);
            } else {
                if (_wasBridge) LayeredWindowPresenter.Reset();            // redraw the whole frame after falling back
                if (_previousFrame == null || _previousFrame.Length != _frame.Length) _previousFrame = new byte[_frame.Length];
                LayeredWindowPresenter.Present(formHandle, _frame, _previousFrame, _target.Width, _target.Height, _bgra);
            }

            _wasBridge = useBridge;
        }

        private RenderTarget2D GetTarget(GraphicsDevice device) {
            var pp = device.PresentationParameters;
            int w  = pp.BackBufferWidth;
            int h  = pp.BackBufferHeight;

            if (_target == null || _target.Width != w || _target.Height != h) {
                _target?.Dispose();

                try {
                    // BGRA matches the layered window / bridge pixel layout, so no per-pixel channel swap is needed.
                    _target = new RenderTarget2D(device, w, h, false, SurfaceFormat.Bgra32, DepthFormat.Depth24Stencil8, 0, RenderTargetUsage.PreserveContents);
                    _bgra   = true;
                } catch (Exception ex) {
                    Logger.Warn(ex, "BGRA render target unavailable; falling back to RGBA with a channel swap.");
                    _target = new RenderTarget2D(device, w, h, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8, 0, RenderTargetUsage.PreserveContents);
                    _bgra   = false;
                }

                _frame         = new byte[w * h * 4];
                _previousFrame = null; // only needed by the window fallback; allocated on first use
            }

            return _target;
        }

    }
}
