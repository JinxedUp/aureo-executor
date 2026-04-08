# Aureo Public Executor (Open Core)

This folder contains the public-facing UI and interface layer.

Included:
- `ui/` (WPF UI source)
- `src/main.cpp` (public-safe entry sample)
- `src/exports.h` (public-safe API surface)
- `src/http_server.h` (public-safe local metadata server)

Intentionally excluded:
- execution engine internals
- VM hooks/detours
- syscall layer
- process/memory manipulation
- pattern scanning/offset logic

The UI in this package runs in public/demo mode and does not attach to or execute against external processes.

# Enjoy the free executor at https://discord.gg/zg8ZJhCBK8