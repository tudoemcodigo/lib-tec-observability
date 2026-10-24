using BenchmarkDotNet.Running;

// Exemplos (sempre em Release):
//   dotnet run -c Release --project TEC.Observability.Benchmarks -f net10.0 -- --filter *
//   dotnet run -c Release --project TEC.Observability.Benchmarks -f net10.0 -- --filter *Redaction* --runtimes net8.0 net10.0
//   dotnet run -c Release --project TEC.Observability.Benchmarks -f net10.0 -- --filter * --job short   (execução rápida, menos precisa)
BenchmarkSwitcher.FromAssembly(typeof(TEC.Observability.Benchmarks.CorrelationBenchmarks).Assembly).Run(args);
