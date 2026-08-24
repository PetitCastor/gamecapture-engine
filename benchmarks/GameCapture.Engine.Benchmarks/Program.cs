using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(GameCapture.Engine.Benchmarks.OcrCropScaleBenchmarks).Assembly).Run(args);
