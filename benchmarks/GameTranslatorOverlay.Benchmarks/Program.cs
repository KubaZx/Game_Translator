using BenchmarkDotNet.Running;

namespace GameTranslatorOverlay.Benchmarks;

public static class Program
{
    // BenchmarkSwitcher daje pełny zestaw przełączników BenchmarkDotNet (--filter, --job short,
    // --exporters json markdown, --list flat) bez własnego parsera argumentów.
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
