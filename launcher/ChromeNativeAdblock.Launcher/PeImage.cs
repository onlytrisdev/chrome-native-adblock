using System.Buffers.Binary;

namespace ChromeNativeAdblock.Launcher;

public sealed record PeSection(
    string Name,
    int VirtualAddress,
    int VirtualSize,
    int RawOffset,
    int RawSize,
    uint Characteristics)
{
    public bool IsExecutable => (Characteristics & 0x20000000) != 0;
}

public sealed class PeImage
{
    private const ushort Pe32PlusMagic = 0x20b;

    private PeImage(string path, byte[] bytes, ulong preferredImageBase, IReadOnlyList<PeSection> sections)
    {
        Path = path;
        Bytes = bytes;
        PreferredImageBase = preferredImageBase;
        Sections = sections;
    }

    public string Path { get; }
    public byte[] Bytes { get; }
    public ulong PreferredImageBase { get; }
    public IReadOnlyList<PeSection> Sections { get; }

    public static PeImage Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = File.ReadAllBytes(path);
        EnsureRange(bytes, 0, 0x40);

        if (bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            throw new InvalidDataException("Missing DOS MZ magic header.");
        }

        var peHeaderOffset = ReadInt32(bytes, 0x3c);
        EnsureRange(bytes, peHeaderOffset, 4 + 20);

        if (bytes[peHeaderOffset] != (byte)'P' ||
            bytes[peHeaderOffset + 1] != (byte)'E' ||
            bytes[peHeaderOffset + 2] != 0 ||
            bytes[peHeaderOffset + 3] != 0)
        {
            throw new InvalidDataException("Missing PE signature.");
        }

        var coffOffset = peHeaderOffset + 4;
        var numberOfSections = ReadUInt16(bytes, coffOffset + 2);
        var sizeOfOptionalHeader = ReadUInt16(bytes, coffOffset + 16);
        var optionalHeaderOffset = coffOffset + 20;

        EnsureRange(bytes, optionalHeaderOffset, sizeOfOptionalHeader);
        var magic = ReadUInt16(bytes, optionalHeaderOffset);
        if (magic != Pe32PlusMagic)
        {
            throw new InvalidDataException($"Expected PE32+ (magic 0x{Pe32PlusMagic:X}), found 0x{magic:X}.");
        }

        var preferredImageBase = ReadUInt64(bytes, optionalHeaderOffset + 24);
        var sectionTableOffset = optionalHeaderOffset + sizeOfOptionalHeader;
        var sections = new List<PeSection>(numberOfSections);

        for (var i = 0; i < numberOfSections; i++)
        {
            var offset = sectionTableOffset + i * 40;
            EnsureRange(bytes, offset, 40);

            var nameBytes = bytes.AsSpan(offset, 8);
            var zeroIndex = nameBytes.IndexOf((byte)0);
            var nameLength = zeroIndex < 0 ? 8 : zeroIndex;
            var name = System.Text.Encoding.ASCII.GetString(nameBytes[..nameLength]);

            var virtualSize = ReadInt32(bytes, offset + 8);
            var virtualAddress = ReadInt32(bytes, offset + 12);
            var rawSize = ReadInt32(bytes, offset + 16);
            var rawOffset = ReadInt32(bytes, offset + 20);
            var characteristics = ReadUInt32(bytes, offset + 36);

            sections.Add(new PeSection(
                name,
                virtualAddress,
                virtualSize,
                rawOffset,
                rawSize,
                characteristics));
        }

        return new PeImage(System.IO.Path.GetFullPath(path), bytes, preferredImageBase, sections);
    }

    public PeSection GetSection(string name) =>
        Sections.SingleOrDefault(section => string.Equals(section.Name, name, StringComparison.Ordinal))
        ?? throw new InvalidDataException($"PE section '{name}' was not found.");

    public int RawOffsetToRva(int rawOffset)
    {
        foreach (var section in Sections)
        {
            if (rawOffset >= section.RawOffset && rawOffset < section.RawOffset + section.RawSize)
            {
                return section.VirtualAddress + (rawOffset - section.RawOffset);
            }
        }

        throw new ArgumentOutOfRangeException(nameof(rawOffset), $"Raw offset 0x{rawOffset:X} does not fall within any known PE section.");
    }

    private static ushort ReadUInt16(byte[] bytes, int offset)
    {
        EnsureRange(bytes, offset, sizeof(ushort));
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, sizeof(ushort)));
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        EnsureRange(bytes, offset, sizeof(uint));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)));
    }

    private static int ReadInt32(byte[] bytes, int offset)
    {
        EnsureRange(bytes, offset, sizeof(int));
        return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)));
    }

    private static ulong ReadUInt64(byte[] bytes, int offset)
    {
        EnsureRange(bytes, offset, sizeof(ulong));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, sizeof(ulong)));
    }

    private static void EnsureRange(byte[] bytes, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > bytes.Length)
        {
            throw new InvalidDataException("Unexpected end of file while parsing PE image.");
        }
    }
}
