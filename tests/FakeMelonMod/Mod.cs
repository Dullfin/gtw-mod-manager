// A stand-in MelonLoader mod for tests. The manager detects MelonLoader mods by the name of the
// [assembly: MelonInfo] attribute, so a local copy of the attribute is enough.
using System;

[assembly: MelonLoader.MelonInfo(typeof(FakeMelonMod.Mod), "Fake Melon Mod", "1.2.3", "Tester")]

namespace MelonLoader
{
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class MelonInfoAttribute : Attribute
    {
        public MelonInfoAttribute(Type type, string name, string version, string author) { }
    }
}

namespace FakeMelonMod
{
    public sealed class Mod { }
}
