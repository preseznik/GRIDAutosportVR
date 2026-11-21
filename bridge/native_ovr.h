#pragma once

// Installs the known legacy SDK call bindings in the supported game image at process startup.
// desktop: a desktop Practice / Race session (-gasvrmode=desktop) redirects only the StateLoading
// enter / exit calls (phase tracking and the device claim); the game keeps its own rendering.
bool AutosportInstallNativeOvr(bool desktop = false) noexcept;
