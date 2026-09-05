# Performance

This document records the baseline benchmark suite introduced for the naming, structure,
performance, and stability program. It exists to make later optimization PRs prove both
correctness and a before/after delta on the same machine.

## Scope

`benchmarks/Ocrx.Engine.Benchmarks` covers the engine hot-path work most likely to change in
PRs 8 and 9:

- OCR crop and scale (`OcrPipeline.CropAndScaleAsync`)
- Pixel sampling and wire serialization (`PixelStrip.CaptureAsync`, `ByteString.CopyFrom`)
- Repeated equivalent ROI reads versus unique ROI reads (a complete `ScanLoop` tick for two clients)
- Serialized versus reference-counted retained-frame reads

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
dotnet run --project benchmarks/Ocrx.Engine.Benchmarks -c Release -- --job short
```

## PR 8 — equivalent ROI processing

PR 8 shares equivalent ROI work within one frame, while cloning the result for each requested ROI
id and clearing the cache after every tick. The relevant benchmark class was run immediately
before and after the change on the baseline machine with `ShortRun`:

| Benchmark | Before | After | Delta |
| --- | ---: | ---: | ---: |
| Repeated equivalent ROI mean | 2.016 ms | 1.732 ms | -14.1% |
| Repeated equivalent ROI allocated | 103.12 KB | 53.25 KB | -48.4% |
| Unique ROI mean | 1.935 ms | 1.933 ms | -0.1% |
| Unique ROI allocated | 103.12 KB | 103.11 KB | -0.01 KB |

The repeated workload clears the improvement gate. The unique workload is unchanged within
ShortRun noise; its 0.01 KB allocation difference is about 0.01%. The implementation retains no
`RoiResult` instances or pixel payloads between ticks, and discards dictionary capacity after a
one-off tick exceeds 256 unique reads.

## PR 9 — retained-frame lifetime

PR 9 replaces the ownership gate held across each unary read with reference-counted leases. Frame
replacement now holds only a short ownership lock; actual `ReadRoi`, `DumpFrame`, and manual-dump
work shares a two-operation gate. At most two active operations can retain superseded bitmaps, so
concurrency does not turn into unbounded retained memory.

The `RetainedFrameBenchmarks` workload runs two 96x24 pixel reads concurrently. The before result
was captured from `v1.1.15` with the stale benchmark setup repaired but the serialized
`ScanLoop.FrameGate` behavior unchanged. The after result uses the lease path. Both are same-machine
`ShortRun` measurements:

| Benchmark | Before | After | Delta |
| --- | ---: | ---: | ---: |
| Two retained pixel reads mean | 549.8 us | 431.7 us | -21.5% |
| Two retained pixel reads allocated | 77.85 KB | 77.76 KB | -0.09 KB |

An in-branch serialized control measured 533.9 us / 78.01 KB, matching the tagged baseline within
`ShortRun` noise. The lease path clears the latency gate without an allocation regression.
