namespace Voxelis
{
    /// <summary>
    /// Global input-capture flags, set by whatever UI is currently consuming raw input
    /// (e.g. an in-game console / chatbox) so gameplay and debug components can suppress
    /// their own <c>Input</c> polling for that frame.
    ///
    /// This is the shared, engine-level gate that both engine and game code can consult
    /// without depending on each other. It is deliberately minimal — analogous to Dear
    /// ImGui's <c>WantCaptureKeyboard</c> / <c>WantCaptureMouse</c>.
    ///
    /// Convention: the capturing UI sets the relevant flag(s) while focused and clears
    /// them when it loses focus or is disabled. Consumers should check the flag at the top
    /// of their input handling, e.g. <c>if (InputFocus.KeyboardCaptured) return;</c>.
    /// </summary>
    public static class InputFocus
    {
        /// <summary>True while a UI element is capturing keyboard / text input.</summary>
        public static bool KeyboardCaptured { get; set; }

        /// <summary>True while a UI element is capturing pointer / mouse input.</summary>
        public static bool PointerCaptured { get; set; }
    }
}
