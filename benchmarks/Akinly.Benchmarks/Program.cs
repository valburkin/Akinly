using BenchmarkDotNet.Running;

// Run all benchmarks, or pick with --filter, e.g. --filter *Nearest*.
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
