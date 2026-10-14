namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// Every test class that opens the real QA451 load belongs here. Same reasoning as
/// <see cref="Qa403HardwareCollection"/> — xunit runs test classes in parallel unless a collection
/// stops it — with one difference worth naming: a QA451 is a HID device on the in-box driver, so
/// two classes opening it at once would <i>succeed</i> rather than fail with a busy error. Windows
/// HID allows shared handles, so the failure mode is not a clear exception but interleaved
/// exchanges, and a reply carries nothing that says which request it answers.
///
/// <para>Separate from the QA403's collection on purpose: these are different devices on different
/// transports, and there is no reason a load test should wait for an analyzer sweep.</para>
/// </summary>
[CollectionDefinition(nameof(Qa451HardwareCollection), DisableParallelization = true)]
public sealed class Qa451HardwareCollection;
