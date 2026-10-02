using SqlOS.Benchmarks.SignIn;

// Sign-in latency, end to end, against the behavior-lock host. Run it with
// scripts/sign-in-benchmark.sh; --help lists the options. The method and the recorded numbers are
// in docs/architecture/8.0-baseline-metrics.md#sign-in-latency.
return await SignInBenchmark.RunAsync(args);
