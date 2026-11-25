// A stand-in BepInEx plugin for tests, so they don't need real mods from a game install.
// The manager recognises BepInEx plugins by the name of the [BepInPlugin] attribute,
// so a local copy of the attribute is enough.
using System;

namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BepInPlugin : Attribute
    {
        public BepInPlugin(string guid, string name, string version) { }
    }
}

namespace FakeBepInPlugin
{
    [BepInEx.BepInPlugin("com.test.fakeplugin", "Fake Plugin", "2.0.0")]
    public sealed class Plugin { }
}
