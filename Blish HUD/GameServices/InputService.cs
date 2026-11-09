using Blish_HUD.Input;
using Microsoft.Xna.Framework;

namespace Blish_HUD {
    public class InputService : GameService {

        private static readonly Logger Logger = Logger.GetLogger<InputService>();

        private readonly IHookManager _hookManager;

        /// <summary>
        /// Provides details about the current mouse state.
        /// </summary>
        public MouseHandler Mouse { get; }

        /// <summary>
        /// Provides details about the current keyboard state.
        /// </summary>
        public KeyboardHandler Keyboard { get; }

        internal InputService() {
            this.Mouse    = new MouseHandler();
            this.Keyboard = new KeyboardHandler();

            if (ApplicationSettings.Instance.DebugEnabled) {
                _hookManager = new DebugHelperHookManager();
            } else {
                _hookManager = new WinApiHookManager();
            }
        }

        private readonly object _hookLock = new object();
        private volatile bool   _hooksWanted;
        private bool            _hooksActive;
        private bool            _unloaded;

        // All state transitions happen under _hookLock.
        internal void EnableHooks() {
            lock (_hookLock) {
                if (_hooksActive || _unloaded) return;
                EnableHooksLocked();
            }
        }

        internal void DisableHooks() {
            lock (_hookLock) {
                DisableHooksLocked();
            }
        }

        private void EnableHooksLocked() {
            if (_hookManager.EnableHook()) {
                _hookManager.RegisterMouseHandler(Mouse.HandleInput);
                _hookManager.RegisterKeyboardHandler(Keyboard.HandleInput);

                this.Mouse.OnEnable();
                this.Keyboard.OnEnable();
                _hooksActive = true;
            } else {
                // Make sure a half-started hook doesn't leave its thread behind and block the next attempt.
                _hookManager.DisableHook();
                Logger.Error("Failed to acquire hook!");
            }
        }

        private void DisableHooksLocked() {
            _hookManager.DisableHook();
            _hookManager.UnregisterMouseHandler(Mouse.HandleInput);
            _hookManager.UnregisterKeyboardHandler(Keyboard.HandleInput);

            this.Mouse.OnDisable();
            this.Keyboard.OnDisable();
            _hooksActive = false;
        }

        /// <summary>
        /// Under Wine, installing the low-level hooks (new thread + message loop + SetWindowsHookEx) takes long enough to
        /// stall the main loop for hundreds of milliseconds every time the game regains focus, which freezes the overlay.
        /// Do it on a worker thread there.  On Windows keep the original synchronous behavior.
        /// </summary>
        private void RequestHooks(bool enable) {
            if (!WindowUtil.IsWine) {
                if (enable) EnableHooks(); else DisableHooks();
                return;
            }

            _hooksWanted = enable;
            System.Threading.Tasks.Task.Run(() => {
                try {
                    lock (_hookLock) {
                        if (_unloaded) return;

                        if (_hooksWanted && !_hooksActive)      EnableHooksLocked();
                        else if (!_hooksWanted && _hooksActive) DisableHooksLocked();
                    }
                } catch (System.Exception ex) {
                    Logger.Error(ex, "Failed to {0} input hooks.", enable ? "enable" : "disable");
                }
            });
        }

        protected override void Initialize() { /* NOOP */ }

        protected override void Load() {
            _hookManager.Load();
            GameIntegration.Gw2Instance.Gw2AcquiredFocus += (s, e) => RequestHooks(true);
            GameIntegration.Gw2Instance.Gw2LostFocus     += (s, e) => RequestHooks(false);
            GameIntegration.Gw2Instance.Gw2Closed        += (s, e) => RequestHooks(false);
        }

        protected override void Unload() {
            lock (_hookLock) {
                _unloaded = true;
                DisableHooksLocked();
            }
            _hookManager.Unload();
        }

        protected override void Update(GameTime gameTime) {
            Mouse.Update();
            Keyboard.Update();
        }
    }
}
