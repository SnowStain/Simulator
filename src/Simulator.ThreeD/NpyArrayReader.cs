using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Simulator.ThreeD;

internal static class NpyArrayReader
{
    private static readonly Regex DescriptorRegex =
        new("'descr'\\s*:\\s*'(?<descr>[^']+)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FortranRegex =
        new("'fortran_order'\\s*:\\s*(?<fortran>True|False)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ShapeRegex =
        new("'shape'\\s*:\\s*\\((?<shape>[^\\)]*)\\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static float[] ReadFloatArray2D(string path, out int rows, out int cols)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);
        NpyHeader header = ReadHeader(reader, path);
        Ensure2dShape(header, path, out rows, out cols);

        int count = checked(rows * cols);
        byte[] payload = reader.ReadBytes(checked(count * header.ItemSize));
        if (payload.Length != count * header.ItemSize)
        {
            throw new InvalidDataException($"NPY payload length mismatch in '{path}'.");
        }

        return ConvertToFloatArray(header, payload, count, path);
    }

    public static byte[] ReadByteArray2D(string path, out int rows, out int cols)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);
        NpyHeader header = ReadHeader(reader, path);
        Ensure2dShape(header, path, out rows, out cols);

        int count = checked(rows * cols);
        byte[] payload = reader.ReadBytes(checked(count * header.ItemSize));
        if (payload.Length != count * header.ItemSize)
        {
            throw new InvalidDataException($"NPY payload length mismatch in '{path}'.");
        }

        return ConvertToByteArray(header, payload, count, path);
    }

    public static bool[] ReadBoolArray2D(string path, out int rows, out int cols)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);
        NpyHeader header = ReadHeader(reader, path);
        Ensure2dShape(header, path, out rows, out cols);

        int count = checked(rows * cols);
        byte[] payload = reader.ReadBytes(checked(count * header.ItemSize));
        if (payload.Length != count * header.ItemSize)
        {
            throw new InvalidDataException($"NPY payload length mismatch in '{path}'.");
        }

        return ConvertToBoolArray(header, payload, count, path);
    }

    private static NpyHeader ReadHeader(BinaryReader reader, string path)
    {
        byte[] magic = reader.ReadBytes(6);
        byte[] expectedMagic = { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y' };
        if (magic.Length != expectedMagic.Length || !magic.SequenceEqual(expectedMagic))
        {
            throw new InvalidDataException($"Unsupported NPY magic header in '{path}'.");
        }

        byte major = reader.ReadByte();
        _ = reader.ReadByte();

        int headerLength = major switch
        {
            1 => reader.ReadUInt16(),
            2 or 3 => checked((int)reader.ReadUInt32()),
            _ => throw new InvalidDataException($"Unsupported NPY version {major}.x in '{path}'."),
        };

        string headerText = Encoding.ASCII.GetString(reader.ReadBytes(headerLength));
        string descriptor = MatchRequired(DescriptorRegex, headerText, "descr", path);
        string fortranText = MatchRequired(FortranRegex, headerText, "fortran", path);
        string shapeText = MatchRequired(ShapeRegex, headerText, "shape", path);

        if (string.Equals(fortranText, "True", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Fortran-order NPY arrays are not supported: '{path}'.");
        }

        ParseDescriptor(descriptor, path, out char typeCode, out int itemSize);
        int[] shape = ParseShape(shapeText, path);

        return new NpyHeader(typeCode, itemSize, shape);
    }

    private static string MatchRequired(Regex regex, string input, string groupName, string path)
    {
        Match match = regex.Match(input);
        if (!match.Success)
        {
            throw new InvalidDataException($"Missing '{groupName}' in NPY header: '{path}'.");
        }

        return match.Groups[groupName].Value;
    }

    private static void ParseDescriptor(string descriptor, string path, out char typeCode, out int itemSize)
    {
        if (string.IsNullOrWhiteSpace(descriptor) || descriptor.Length < 2)
        {
            throw new InvalidDataException($"Invalid NPY dtype descriptor '{descriptor}' in '{path}'.");
        }

        int offset = 0;
        char endian = descriptor[0];
        if (endian is '<' or '>' or '|' or '=')
        {
            offset = 1;
        }

        if (endian == '>')
        {
            throw new InvalidDataException($"Big-endian NPY arrays are not supported: '{path}'.");
        }

        if (offset >= descriptor.Length)
        {
            throw new InvalidDataException($"Invalid NPY dtype descriptor '{descriptor}' in '{path}'.");
        }

        typeCode = descriptor[offset];
        string sizeText = descriptor[(offset + 1)..];
        if (!int.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out itemSize) || itemSize <= 0)
        {
            throw new InvalidDataException($"Invalid NPY dtype item-size '{descriptor}' in '{path}'.");
        }
    }

    private static int[] ParseShape(string text, string path)
    {
        string[] parts = text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            throw new InvalidDataException($"Invalid NPY shape tuple in '{path}'.");
        }

        var shape = new List<int>(parts.Length);
        foreach (string part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value <= 0)
            {
                throw new InvalidDataException($"Invalid NPY shape value '{part}' in '{path}'.");
            }

            shape.Add(value);
        }

        return shape.ToArray();
    }

    private static void Ensure2dShape(NpyHeader header, string path, out int rows, out int cols)
    {
        if (header.Shape.Length != 2)
        {
            throw new InvalidDataException($"Expected 2D NPY array in '{path}', got {header.Shape.Length}D.");
        }

        rows = header.Shape[0];
        cols = header.Shape[1];
    }

    private static float[] ConvertToFloatArray(NpyHeader header, byte[] payload, int count, string path)
    {
        return (header.TypeCode, header.ItemSize) switch
        {
            ('f', 4) => ConvertFloat32(payload, count),
            ('f', 8) => ConvertFloat64ToFloat32(payload, count),
            ('u', 1) => payload.Select(value => (float)value).ToArray(),
            ('i', 1) => payload.Select(value => (float)(sbyte)value).ToArray(),
            ('i', 2) => ConvertInt16ToFloat32(payload, count),
            ('u', 2) => ConvertUInt16ToFloat32(payload, count),
            ('?', 1) => payload.Select(value => value != 0 ? 1f : 0f).ToArray(),
            ('b', 1) => payload.Select(value => value != 0 ? 1f : 0f).ToArray(),
            _ => throw new InvalidDataException($"Unsupported float conversion dtype '{header.TypeCode}{header.ItemSize}' in '{path}'."),
        };
    }

    private static byte[] ConvertToByteArray(NpyHeader header, byte[] payload, int count, string path)
    {
        return (header.TypeCode, header.ItemSize) switch
        {
            ('u', 1) => payload,
            ('i', 1) => payload
                .Select(value => (int)(sbyte)value)
                .Select(value => (byte)Math.Clamp(value, 0, 255))
                .ToArray(),
            ('i', 2) => ConvertInt16ToByte(payload, count),
            ('u', 2) => ConvertUInt16ToByte(payload, count),
            ('?', 1) => payload.Select(value => value != 0 ? (byte)1 : (byte)0).ToArray(),
            ('b', 1) => payload.Select(value => value != 0 ? (byte)1 : (byte)0).ToArray(),
            ('f', 4) => ConvertFloat32ToByte(payload, count),
            _ => throw new InvalidDataException($"Unsupported byte conversion dtype '{header.TypeCode}{header.ItemSize}' in '{path}'."),
        };
    }

    private static float[] ConvertFloat32(byte[] payload, int count)
    {
        var result = new float[count];
        Buffer.BlockCopy(payload, 0, result, 0, payload.Length);
        return result;
    }

    private static float[] ConvertFloat64ToFloat32(byte[] payload, int count)
    {
        var result = new float[count];
        for (int index = 0; index < count; index++)
        {
            result[index] = (float)BitConverter.ToDouble(payload, index * sizeof(double));
        }

        return result;
    }

    private static float[] ConvertInt16ToFloat32(byte[] payload, int count)
    {
        var result = new float[count];
        for (int index = 0; index < count; index++)
        {
            result[index] = BitConverter.ToInt16(payload, index * sizeof(short));
        }

        return result;
    }

    private static float[] ConvertUInt16ToFloat32(byte[] payload, int count)
    {
        var result = new float[count];
        for (int index = 0; index < count; index++)
        {
            result[index] = BitConverter.ToUInt16(payload, index * sizeof(ushort));
        }

        return result;
    }

    private static byte[] ConvertInt16ToByte(byte[] payload, int count)
    {
        var result = new byte[count];
        for (int index = 0; index < count; index++)
        {
            short value = BitConverter.ToInt16(payload, index * sizeof(short));
            result[index] = (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);
        }

        return result;
    }

    private static byte[] ConvertUInt16ToByte(byte[] payload, int count)
    {
        var result = new byte[count];
        for (int index = 0; index < count; index++)
        {
            ushort value = BitConverter.ToUInt16(payload, index * sizeof(ushort));
            result[index] = (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);
        }

        return result;
    }

    private static byte[] ConvertFloat32ToByte(byte[] payload, int count)
    {
        var result = new byte[count];
        for (int index = 0; index < count; index++)
        {
            float value = BitConverter.ToSingle(payload, index * sizeof(float));
            result[index] = (byte)Math.Clamp((int)MathF.Round(value), byte.MinValue, byte.MaxValue);
        }

        return result;
    }

    private static bool[] ConvertToBoolArray(NpyHeader header, byte[] payload, int count, string path)
    {
        return (header.TypeCode, header.ItemSize) switch
        {
            ('?', 1) => payload.Select(value => value != 0).ToArray(),
            ('b', 1) => payload.Select(value => value != 0).ToArray(),
            ('u', 1) => payload.Select(value => value != 0).ToArray(),
            ('i', 1) => payload.Select(value => (sbyte)value != 0).ToArray(),
            ('u', 2) => ConvertUInt16ToBool(payload, count),
            ('i', 2) => ConvertInt16ToBool(payload, count),
            ('f', 4) => ConvertFloat32ToBool(payload, count),
            _ => throw new InvalidDataException($"Unsupported bool conversion dtype '{header.TypeCode}{header.ItemSize}' in '{path}'."),
        };
    }

    private static bool[] ConvertUInt16ToBool(byte[] payload, int count)
    {
        var result = new bool[count];
        for (int index = 0; index < count; index++)
        {
            result[index] = BitConverter.ToUInt16(payload, index * sizeof(ushort)) != 0;
        }

        return result;
    }

    private static bool[] ConvertInt16ToBool(byte[] payload, int count)
    {
        var result = new bool[count];
        for (int index = 0; index < count; index++)
        {
            result[index] = BitConverter.ToInt16(payload, index * sizeof(short)) != 0;
        }

        return result;
    }

    private static bool[] ConvertFloat32ToBool(byte[] payload, int count)
    {
        var result = new bool[count];
        for (int index = 0; index < count; index++)
        {
            float value = BitConverter.ToSingle(payload, index * sizeof(float));
            result[index] = Math.Abs(value) > 1e-6f;
        }

        return result;
    }

    private readonly record struct NpyHeader(char TypeCode, int ItemSize, int[] Shape);
}
