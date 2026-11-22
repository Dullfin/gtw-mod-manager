using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace GTWModManager.Core;

/// <param name="Guid">BepInEx plugin GUID; for MelonLoader mods, the mod name (they have no GUID).</param>
public sealed record PluginMetadata(string Guid, string Name, string Version, ModLoader Loader, string? Author = null);

/// <summary>
/// Reads [BepInPlugin(guid, name, version)] and [assembly: MelonInfo(type, name, version, author)]
/// from a DLL without loading it, so scanning never runs mod code.
/// </summary>
public static class PluginMetadataReader
{
    public static IReadOnlyList<PluginMetadata> Read(string dllPath)
    {
        try
        {
            using var stream = File.OpenRead(dllPath);
            return Read(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Reads from a stream; it is copied to memory first so non-seekable zip streams work.</summary>
    public static IReadOnlyList<PluginMetadata> Read(Stream input)
    {
        var result = new List<PluginMetadata>();
        try
        {
            using var stream = new MemoryStream();
            input.CopyTo(stream);
            stream.Position = 0;
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return result;
            var md = pe.GetMetadataReader();

            foreach (var typeHandle in md.TypeDefinitions)
            {
                foreach (var attrHandle in md.GetTypeDefinition(typeHandle).GetCustomAttributes())
                {
                    var attr = md.GetCustomAttribute(attrHandle);
                    if (AttributeTypeName(md, attr) != "BepInPlugin") continue;
                    var blob = md.GetBlobReader(attr.Value);
                    if (blob.ReadUInt16() != 1) continue; // custom attribute prolog
                    result.Add(new PluginMetadata(
                        blob.ReadSerializedString() ?? "", blob.ReadSerializedString() ?? "",
                        blob.ReadSerializedString() ?? "", ModLoader.BepInEx));
                }
            }

            if (md.IsAssembly)
            {
                foreach (var attrHandle in md.GetAssemblyDefinition().GetCustomAttributes())
                {
                    var attr = md.GetCustomAttribute(attrHandle);
                    if (AttributeTypeName(md, attr) is not ("MelonInfoAttribute" or "MelonModInfoAttribute")) continue;
                    var blob = md.GetBlobReader(attr.Value);
                    if (blob.ReadUInt16() != 1) continue;
                    blob.ReadSerializedString(); // the mod's main type
                    var name = blob.ReadSerializedString() ?? "";
                    var version = blob.ReadSerializedString() ?? "";
                    var author = blob.ReadSerializedString();
                    result.Add(new PluginMetadata(name, name, version, ModLoader.MelonLoader, author));
                }
            }
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            // Not a managed assembly, or an attribute we can't decode: treat as having no metadata.
        }
        return result;
    }

    private static string? AttributeTypeName(MetadataReader md, CustomAttribute attr)
    {
        EntityHandle parent;
        if (attr.Constructor.Kind == HandleKind.MemberReference)
            parent = md.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent;
        else if (attr.Constructor.Kind == HandleKind.MethodDefinition)
            parent = md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor).GetDeclaringType();
        else
            return null;

        return parent.Kind switch
        {
            HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name),
            HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
            _ => null,
        };
    }
}
