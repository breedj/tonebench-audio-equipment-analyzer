namespace Tonebench.Device.Tests.Hardware;

/// <summary>
/// Every test class that opens the real QA403 belongs here, because there is one QA403 and
/// <c>Qa40xTransport.Open</c> claims USB interface 0 exclusively.
///
/// <para>xunit runs test <i>classes</i> in parallel by default, and collections are what stops it:
/// classes sharing a collection never overlap. This was invisible while
/// <see cref="Qa40xUsbDeviceHardwareTests"/> was the only QA403 class in the assembly — the sound-card
/// hardware tests talk to different hardware, so they could and still can run alongside. Adding
/// <see cref="Qa40x384kHzHardwareTests"/> made the whole hardware suite fail at once with
/// <c>Qa40xDeviceBusyException: Another process still holds it</c>, which is a misleading message
/// here: the other claimant was this same process, one test class over.</para>
///
/// <para><see cref="CollectionDefinitionAttribute.DisableParallelization"/> is belt and braces —
/// sharing the collection is already enough — but it also pins the intent, so a future class that
/// joins this collection cannot be made parallel by a setting elsewhere.</para>
/// </summary>
[CollectionDefinition(nameof(Qa403HardwareCollection), DisableParallelization = true)]
public sealed class Qa403HardwareCollection;
