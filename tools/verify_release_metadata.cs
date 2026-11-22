#:property PublishAot=false
#:property UseAppHost=false
#:property NuGetAudit=false

// Inspect the release DLL as data. Loading the plugin would require the game and its SDK.
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length != 2 || !Version.TryParse(args[1], out var expectedVersion))
{
    Console.Error.WriteLine("Expected a packaged CompAI DLL and its release version.");
    return 2;
}

try
{
    using var file = File.OpenRead(args[0]);
    using var pe = new PEReader(file);
    CheckAssembly(pe.GetMetadataReader(), expectedVersion);
    var documentCount = CheckDebugInformation(pe);
    Console.WriteLine($"Verified compiled release metadata and {documentCount} source document paths.");
    return 0;
}
catch (InvalidDataException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
catch (BadImageFormatException)
{
    Console.Error.WriteLine("Invalid assembly or embedded debug information.");
    return 1;
}

static void CheckAssembly(MetadataReader metadata, Version expectedVersion)
{
    var assembly = metadata.GetAssemblyDefinition();
    if (!metadata.StringComparer.Equals(assembly.Name, "CompAI") || assembly.Version != expectedVersion)
        throw new InvalidDataException("Packaged assembly identity differs from the release manifest.");

    var versionCount = 0;
    foreach (var handle in assembly.GetCustomAttributes())
    {
        var attribute = metadata.GetCustomAttribute(handle);
        if (attribute.Constructor.Kind != HandleKind.MemberReference)
            continue;
        var parent = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
        if (parent.Kind != HandleKind.TypeReference)
            continue;
        var type = metadata.GetTypeReference((TypeReferenceHandle)parent);
        if (!metadata.StringComparer.Equals(type.Namespace, "System.Reflection"))
            continue;
        var name = metadata.GetString(type.Name);
        if (name is not ("AssemblyMetadataAttribute" or "AssemblyInformationalVersionAttribute"))
            continue;
        var value = metadata.GetBlobReader(attribute.Value);
        if (value.ReadUInt16() != 1)
            throw new InvalidDataException("Invalid release assembly attribute.");
        var text = value.ReadSerializedString();
        if (name == "AssemblyMetadataAttribute")
        {
            if (text?.StartsWith("Repository", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidDataException("Packaged assembly contains repository metadata.");
        }
        else
        {
            ++versionCount;
            if (text != expectedVersion.ToString() &&
                !(expectedVersion.Revision == 0 && text == expectedVersion.ToString(3)))
                throw new InvalidDataException("Packaged informational version differs from the release number.");
        }
    }
    if (versionCount != 1)
        throw new InvalidDataException("Expected a single release informational version.");
}

static int CheckDebugInformation(PEReader pe)
{
    var entries = pe.ReadDebugDirectory();
    var symbols = entries.Where(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb).ToArray();
    var codeView = entries.Where(entry => entry.Type == DebugDirectoryEntryType.CodeView).ToArray();
    if (symbols.Length != 1 || codeView.Length != 1 || pe.ReadCodeViewDebugDirectoryData(codeView[0]).Path != "CompAI.pdb")
        throw new InvalidDataException("Expected one embedded PDB with a plain CompAI.pdb filename.");

    // The MPDB header stores the expanded length before the deflate stream.
    var header = pe.GetSectionData(symbols[0].DataRelativeVirtualAddress).GetReader(0, 8);
    header.ReadUInt32();
    if (header.ReadInt32() is <= 0 or > 32 * 1024 * 1024)
        throw new InvalidDataException("Embedded PDB is outside the package check's size limit.");
    using var provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(symbols[0]);
    var pdb = provider.GetMetadataReader();
    if (pdb.Documents.Count == 0)
        throw new InvalidDataException("Embedded PDB contains no source documents.");
    const string root = "/_/CompAI/";
    foreach (var handle in pdb.Documents)
    {
        var path = pdb.GetString(pdb.GetDocument(handle).Name).Replace('\\', '/');
        if (!path.StartsWith(root, StringComparison.Ordinal) || path.Contains(':') || path.Any(char.IsControl) ||
            path[root.Length..].Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Debug source path falls outside the neutral release root.");
    }

    var sourceLink = new Guid("cc110556-a091-4d38-9fec-25ab9a351a6a");
    var embeddedSource = new Guid("0e8a571b-6926-466e-b4ad-8ab04611f5fe");
    foreach (var handle in pdb.CustomDebugInformation)
    {
        var kind = pdb.GetGuid(pdb.GetCustomDebugInformation(handle).Kind);
        if (kind == sourceLink || kind == embeddedSource)
            throw new InvalidDataException("Debug metadata contains Source Link or embedded source.");
    }
    return pdb.Documents.Count;
}
