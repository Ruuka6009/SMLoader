#pragma once

namespace smloader::plugins {

// LoadLibraryW's every path listed in SMLOADER_NATIVE_PLUGINS, in order.
//
// The launcher decides what goes in that list (it can read PE headers, version
// resources and the mod allowlist; this side cannot). All the shim does is map
// them, as early in the process as it is legal to - a graphics plugin such as
// ReShade hooks D3D from its own DllMain, so it has to be in before the engine
// creates its device. That is why this runs on its own thread instead of behind
// the second it takes CoreCLR to come up.
//
// Must NOT be called from DllMain: it calls LoadLibraryW.
void LoadAll();

// Marks the report finished and empty, for when the thread could not be started
// at all. Without it Report() would stall the boot for its full timeout.
void Cancel();

// Newline-separated "<status>|<path>" lines, one per plugin the launcher asked
// for, where <status> is "ok", "err <win32 code>", or "other <path already
// mapped under that name>". Blocks until LoadAll has finished, so the managed
// side is never told about a load that is still in flight. Valid for the life
// of the process.
const wchar_t* Report();

}
