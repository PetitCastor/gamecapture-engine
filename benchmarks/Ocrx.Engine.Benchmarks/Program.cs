using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Ocrx.Engine.Benchmarks.OcrCropScaleBenchmarks).Assembly).Run(args);
