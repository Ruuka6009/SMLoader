#pragma once

namespace smloader::log {
void Init();
void Write(const char* fmt, ...);

// Closes the append handle. Safe to call without a matching Init.
void Shutdown();
}

#define SMLOG(...) ::smloader::log::Write(__VA_ARGS__)
