using System.IO;

namespace Stedjcast.Services;

/// <summary>
/// Reads the export table of a Windows DLL by parsing the file bytes, without ever
/// loading it or running its code (no LoadLibrary). Used to tell a real VST3 module
/// (exports "GetPluginFactory") apart from a legacy VST2 plugin or any other DLL before
/// handing it to the native VST3 bridge, which would otherwise crash the process.
/// </summary>
public static class PeModuleInspector
{
    public static bool ExportsFunction(string filePath, string exportName)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt16() != 0x5A4D) // "MZ"
                return false;

            stream.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = reader.ReadInt32();

            stream.Seek(peOffset, SeekOrigin.Begin);
            if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
                return false;

            reader.ReadUInt16(); // Machine
            var numberOfSections = reader.ReadUInt16();
            stream.Seek(12, SeekOrigin.Current); // TimeDateStamp, PointerToSymbolTable, NumberOfSymbols
            var sizeOfOptionalHeader = reader.ReadUInt16();
            stream.Seek(2, SeekOrigin.Current); // Characteristics

            var optionalHeaderStart = stream.Position;
            var magic = reader.ReadUInt16();
            var isPe32Plus = magic == 0x20B;
            if (!isPe32Plus && magic != 0x10B)
                return false;

            var dataDirectoryOffset = optionalHeaderStart + (isPe32Plus ? 112 : 96);
            stream.Seek(dataDirectoryOffset, SeekOrigin.Begin);
            var exportTableRva = reader.ReadUInt32();
            var exportTableSize = reader.ReadUInt32();
            if (exportTableRva == 0 || exportTableSize == 0)
                return false;

            var sections = new (uint VirtualAddress, uint SizeOfRawData, uint PointerToRawData)[numberOfSections];
            stream.Seek(optionalHeaderStart + sizeOfOptionalHeader, SeekOrigin.Begin);
            for (var i = 0; i < numberOfSections; i++)
            {
                stream.Seek(8 + 4, SeekOrigin.Current); // Name[8], VirtualSize
                var virtualAddress = reader.ReadUInt32();
                var sizeOfRawData = reader.ReadUInt32();
                var pointerToRawData = reader.ReadUInt32();
                stream.Seek(16, SeekOrigin.Current); // relocations, linenumbers, characteristics
                sections[i] = (virtualAddress, sizeOfRawData, pointerToRawData);
            }

            long? RvaToOffset(uint rva)
            {
                foreach (var section in sections)
                {
                    if (rva >= section.VirtualAddress && rva < section.VirtualAddress + section.SizeOfRawData)
                        return section.PointerToRawData + (rva - section.VirtualAddress);
                }
                return null;
            }

            var exportDirOffset = RvaToOffset(exportTableRva);
            if (exportDirOffset is null)
                return false;

            // IMAGE_EXPORT_DIRECTORY: NumberOfNames at +24, AddressOfFunctions at +28, AddressOfNames at +32.
            stream.Seek(exportDirOffset.Value + 24, SeekOrigin.Begin);
            var numberOfNames = reader.ReadUInt32();
            stream.Seek(4, SeekOrigin.Current); // skip AddressOfFunctions
            var addressOfNamesRva = reader.ReadUInt32();
            if (numberOfNames == 0 || numberOfNames > 100_000)
                return false;

            var namesOffset = RvaToOffset(addressOfNamesRva);
            if (namesOffset is null)
                return false;

            stream.Seek(namesOffset.Value, SeekOrigin.Begin);
            var nameRvas = new uint[numberOfNames];
            for (var i = 0; i < numberOfNames; i++)
                nameRvas[i] = reader.ReadUInt32();

            foreach (var nameRva in nameRvas)
            {
                var nameOffset = RvaToOffset(nameRva);
                if (nameOffset is null)
                    continue;

                stream.Seek(nameOffset.Value, SeekOrigin.Begin);
                if (ReadAsciiString(reader) == exportName)
                    return true;
            }

            return false;
        }
        catch
        {
            // A file that can't be parsed as a valid PE is not a loadable VST3 module:
            // a false negative (hidden from the list) beats loading it into the bridge.
            return false;
        }
    }

    private static string ReadAsciiString(BinaryReader reader)
    {
        var bytes = new List<byte>(64);
        byte b;
        while ((b = reader.ReadByte()) != 0)
            bytes.Add(b);
        return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
    }
}
