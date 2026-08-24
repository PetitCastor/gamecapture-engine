# Performance

This document records the baseline benchmark suite introduced for the naming, structure,
performance, and stability program. It exists to make later optimization PRs prove both
correctness and a before/after delta on the same machine.

## Scope

`benchmarks/GameCapture.Engine.Benchmarks` covers the engine hot-path work most likely to change in
PRs 8 and 9:

- OCR crop and scale (`OcrPipeline.CropAndScaleAsync`)
- Pixel sampling and wire serialization (`PixelStrip.CaptureAsync`, `ByteString.CopyFrom`)
- Repeated equivalent ROI reads versus unique ROI reads (a complete `ScanLoop` tick for two clients)
- Retained-frame gated access (`ScanLoop.FrameGate` + retained frame read)

CI builds this project but does not run timing thresholds.

## Baseline Environment

Captured on August 24, 2026:

- CPU: AMD Ryzen 7 9800X3D (8 physical / 16 logical cores)
- RAM: 31.15 GB
- GPU: AMD Radeon Graphics; NVIDIA GeForce RTX 5070 Ti
- OS: Windows 11 25H2, build 26200.9168
- .NET SDK: 10.0.400
- Runtime: .NET 10.0.11, X64 RyuJIT x86-64-v4, concurrent workstation GC
- OCR language pack: user-profile recognizer
- BenchmarkDotNet: 0.15.8, `ShortRun` (1 launch, 3 warmups, 3 iterations)

## Baseline Results

Fill this in with local `BenchmarkDotNet` output from the baseline machine before landing
performance PRs:

| Benchmark | Mean | Allocated | Notes |
| --- | --- | --- | --- |
| `OcrCropScaleBenchmarks.CropAndScale_TextPanel` | 2.816 ms | 19.83 KB | 420x120 ROI at 2.5x scale |
| `PixelSamplingBenchmarks.CapturePixels` | 291.961 us | 29.34 KB | 96x24 ROI |
| `PixelSamplingBenchmarks.SerializePixels` | 257.5 ns | 9.19 KB | Existing `ByteString.CopyFrom` path |
| `RepeatedRoiBenchmarks.RepeatedEquivalentPixelRois` | 1.941 ms | 102.93 KB | Complete tick; two clients share equivalent work |
| `RepeatedRoiBenchmarks.UniquePixelRois` | 1.943 ms | 102.99 KB | Complete tick; two clients request distinct work |
| `RetainedFrameBenchmarks.ReadRetainedFrameUnderGate` | 37.27 ns | 72 B | Existing serialized gate access |

These values are evidence for same-machine comparisons in PRs 8 and 9, not portable thresholds.
Run the suite with:

```powershell
dotnet run --project benchmarks/GameCapture.Engine.Benchmarks -c Release -- --job short
```
