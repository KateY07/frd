# Legacy projects

These projects no longer build against the current code, or are copies of it from before v3. They are kept for reference only and are not part of `FRD.sln` or any release.

| Path | Why it is here |
|---|---|
| `tests/LocalDesktop`, `tests/DesktopProbe`, `tests/SecureDesktopProbe`, `tests/LocalInput`, `tests/InputPipeline`, `tests/Input`, `tests/ConstrainedNetwork`, `tests/InputVisualLatency`, `tests/PathSwitchResilience` | Reference classes and methods that were removed or renamed in v3 (the Avalonia UI, the in-process input server, the FFmpeg-only code path). Compile errors as of v4.pre1. |
| `demo/FrdDemo.cs`, `Build-SingleFileDemo.ps1` | A generated single-file copy of the whole program (about 9,700 lines) from the Avalonia/FFmpeg era. It drifted from the real code and is no longer generated. |

The working tests are in `../tests`; run them with `tests/Run-Tests.ps1`.
