using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("MSCSteamMP")]
[assembly: AssemblyDescription("Steam P2P sync for My Summer Car: drivable vehicles, Satsuma part assembly/removal and bolt tightness (MSCLoader).")]
[assembly: AssemblyCompany("MSCSteamMP")]
[assembly: AssemblyProduct("MSCSteamMP")]
// tests\InterpProbe.cs ayri bir assembly olarak derleniyor (tests\verify.cmd) ve
// VehicleInterpolator'in ic/test-only olan SlerpOverride alanina erismesi gerekiyor.
// Alan "internal" kaldi; sadece bu tek assembly'ye gorunur kiliniyor, yani public API
// yuzeyine bir sey eklenmiyor.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("InterpProbe")]
// Must match ModEntry.Version. MSCLoader reports the assembly version in the mod list,
// and a stale one there is how two players end up on different protocol revisions.
[assembly: AssemblyVersion("0.3.0")]
[assembly: AssemblyFileVersion("0.3.0")]
[assembly: ComVisible(false)]
