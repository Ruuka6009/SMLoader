#pragma once

namespace smloader::clr {

// Boots CoreCLR through hostfxr and hands control to
// SMLoader.Core!SMLoader.Core.Entry.Boot. Must NOT be called from DllMain.
bool Start();

}
