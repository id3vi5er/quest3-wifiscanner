using System.Collections.Generic;

namespace WifiScanner
{
    /// <summary>
    /// A controller tool that owns the trigger (and often A/B or the stick)
    /// while active. Only one exclusive tool may be active at a time.
    /// </summary>
    public interface IExclusiveTool
    {
        bool IsActive { get; }
        void SetActive(bool active);
    }

    /// <summary>
    /// Tiny central registry so the trigger-owning controller tools
    /// (room tracer, drill planner, delete mode, distance measurer, ping
    /// tester, spatial notes) stay mutually exclusive without every tool
    /// having to know about every other one.
    ///
    /// Each tool registers itself in OnEnable, unregisters in OnDisable, and
    /// calls <see cref="DeactivateOthers"/> from its own SetActive(true).
    /// RecordingControls suspends its A/B bindings while <see cref="AnyActive"/>.
    /// </summary>
    public static class ExclusiveTools
    {
        private static readonly List<IExclusiveTool> _all = new List<IExclusiveTool>();

        public static void Register(IExclusiveTool tool)
        {
            if (tool != null && !_all.Contains(tool)) _all.Add(tool);
        }

        public static void Unregister(IExclusiveTool tool)
        {
            _all.Remove(tool);
        }

        /// <summary>Deactivates every registered tool except the caller.</summary>
        public static void DeactivateOthers(IExclusiveTool self)
        {
            // Iterate over a copy: SetActive(false) is allowed to touch the list.
            for (int i = _all.Count - 1; i >= 0; i--)
            {
                var t = _all[i];
                if (!ReferenceEquals(t, self) && t.IsActive)
                    t.SetActive(false);
            }
        }

        /// <summary>True while any exclusive tool owns the controller trigger.</summary>
        public static bool AnyActive
        {
            get
            {
                foreach (var t in _all)
                    if (t.IsActive) return true;
                return false;
            }
        }
    }
}
