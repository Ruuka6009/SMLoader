#pragma once

namespace smloader::log {
void Init();
void Write(const char* fmt, ...);
}

#define SMLOG(...) ::smloader::log::Write(__VA_ARGS__)
