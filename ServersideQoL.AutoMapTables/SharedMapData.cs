using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ServersideQoL.AutoMapTables;

internal sealed class SharedMapData(byte[] explored, SharedMapData.Pin[] pins)
{
  internal const int MaxPinBytes = 8 * 1024 * 1024;
  internal const int MaxPins = 32768;
  static readonly Encoding Utf8 = new UTF8Encoding(false, true);
  public byte[] Explored { get; } = explored;
  public Pin[] Pins { get; } = pins;
  internal readonly record struct Pin(long Owner, string Name, float X, float Y, float Z, int Type, bool Checked, string Author);
  internal enum DiscoveryKind { Unknown, Portal, Ship, Ore, Dungeon }
  internal readonly record struct Discovery(string Source, Pin Pin, DiscoveryKind Kind = DiscoveryKind.Unknown, int PrefabHash = 0);
  internal readonly record struct Contribution(long CharacterID, Discovery[] Discoveries, bool Complete = true, long Owner = 0);
  internal sealed class UpdateCursor
  {
    public uint DataRevision { get; set; }
    public long SharedRevision { get; set; } = -1;
    public Pin[] Emitted { get; set; } = [];
  }

  internal static Pin StabilizeShipPosition(Pin current, Pin previous, float pixelSize)
  {
    var dx = current.X - previous.X;
    var dz = current.Z - previous.Z;
    // Keep upstream's horizontal pixel threshold; vertical bobbing must not churn shared snapshots.
    return (double)dx * dx + (double)dz * dz < (double)pixelSize * pixelSize
      ? current with { X = previous.X, Y = previous.Y, Z = previous.Z } : current;
  }

  internal static int PixelCount(int textureSize)
  {
    // Bound client-controlled expansion; 4096² supports four times the vanilla map area.
    if (textureSize <= 0 || (long)textureSize * textureSize > 16 * 1024 * 1024)
      throw new InvalidDataException("Unsupported shared map geometry");
    return textureSize * textureSize;
  }

  public static SharedMapData Decode(byte[]? compressed, int textureSize)
  {
    var pixels = PixelCount(textureSize);
    if (compressed is null)
      return new(new byte[(pixels + 7) / 8], []);
    try
    {
      var limit = pixels + MaxPinBytes + 12;
      if (compressed.Length < 18 || compressed.Length > limit + 65536)
        throw new InvalidDataException("Shared map compressed data exceeds limit");
      var expectedLength = BinaryPrimitives.ReadUInt32LittleEndian(compressed.AsSpan(compressed.Length - 4));
      if (expectedLength > limit)
        throw new InvalidDataException("Shared map gzip size exceeds limit");
      using var input = new MemoryStream(compressed, writable: false);
      using var gzip = new GZipStream(input, CompressionMode.Decompress);
      using var stream = new MemoryStream();
      var buffer = new byte[8192];
      int read;
      while ((read = gzip.Read(buffer, 0, buffer.Length)) != 0)
      {
        if (stream.Length + read > limit)
          throw new InvalidDataException("Shared map expanded data exceeds limit");
        stream.Write(buffer, 0, read);
      }
      // GZipStream can accept a truncated footer, so also require its declared expanded size.
      if (stream.Length != expectedLength)
        throw new InvalidDataException("Invalid shared map gzip footer");
      stream.Position = 0;
      using var reader = new BinaryReader(stream, Utf8);
      if (reader.ReadInt32() != 3 || reader.ReadInt32() != pixels)
        throw new InvalidDataException("Unsupported shared map version or geometry");
      if (stream.Length - stream.Position < pixels + sizeof(int))
        throw new InvalidDataException("Truncated shared exploration");
      var explored = new byte[(pixels + 7) / 8];
      for (int i = 0; i < pixels; i++)
      {
        if (ReadBool(reader))
          explored[i / 8] |= (byte)(1 << (i % 8));
      }
      var count = reader.ReadInt32();
      if (count < 0 || count > MaxPins || count > (stream.Length - stream.Position) / 27)
        throw new InvalidDataException("Invalid shared pin count");
      var pins = new Pin[count];
      for (int i = 0; i < count; i++)
        pins[i] = ReadPin(reader);
      if (stream.Position != stream.Length)
        throw new InvalidDataException("Trailing shared map data");
      return new(explored, pins);
    }
    catch (Exception ex) when (ex is EndOfStreamException or ArgumentException or FormatException or OverflowException)
    {
      throw new InvalidDataException("Invalid shared map data", ex);
    }
  }

  public static byte[] Encode(ReadOnlySpan<byte> explored, int textureSize, IReadOnlyList<Pin> pins)
  {
    var pixels = PixelCount(textureSize);
    if (explored.Length != (pixels + 7) / 8 || pins.Count > MaxPins)
      throw new InvalidDataException("Invalid shared map mask or pin count");
    var pinBytes = 0;
    foreach (var pin in pins)
    {
      ValidatePin(pin);
      pinBytes += 35 + Utf8.GetByteCount(pin.Name) + Utf8.GetByteCount(pin.Author);
      if (pinBytes > MaxPinBytes)
        throw new InvalidDataException("Shared pins exceed byte limit");
    }
    using var stream = new MemoryStream();
    using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
    using (var buffered = new BufferedStream(gzip, 64 * 1024))
    using (var writer = new BinaryWriter(buffered, Utf8, leaveOpen: true))
    {
      writer.Write(3);
      writer.Write(pixels);
      for (int i = 0; i < pixels; i++)
        writer.Write((explored[i / 8] & (1 << (i % 8))) != 0);
      writer.Write(pins.Count);
      foreach (var pin in pins)
        WritePin(writer, pin);
    }
    return stream.ToArray();
  }

  internal static Pin ReadPin(BinaryReader reader)
  {
    var pin = new Pin(reader.ReadInt64(), ReadString(reader, 4096), reader.ReadSingle(), reader.ReadSingle(),
      reader.ReadSingle(), reader.ReadInt32(), ReadBool(reader), ReadString(reader, 1024));
    ValidatePin(pin);
    return pin;
  }

  internal static void WritePin(BinaryWriter writer, Pin pin)
  {
    ValidatePin(pin);
    writer.Write(pin.Owner);
    writer.Write(pin.Name);
    writer.Write(pin.X); writer.Write(pin.Y); writer.Write(pin.Z);
    writer.Write(pin.Type); writer.Write(pin.Checked); writer.Write(pin.Author);
  }

  static void ValidatePin(Pin pin)
  {
    if (!float.IsFinite(pin.X) || !float.IsFinite(pin.Y) || !float.IsFinite(pin.Z) ||
        pin.Type < 0 || pin.Type > 17 || pin.Name is null || pin.Author is null ||
        Utf8.GetByteCount(pin.Name) > 4096 || Utf8.GetByteCount(pin.Author) > 1024)
      throw new InvalidDataException("Invalid shared pin fields");
  }

  static bool ReadBool(BinaryReader reader) => reader.ReadByte() switch
  {
    0 => false,
    1 => true,
    _ => throw new InvalidDataException("Invalid shared map boolean")
  };

  internal static string ReadString(BinaryReader reader, int maxBytes)
  {
    uint length = 0;
    for (int shift = 0; shift < 35; shift += 7)
    {
      var b = reader.ReadByte();
      if (shift == 28 && b > 7)
        throw new InvalidDataException("Invalid shared string length");
      length |= (uint)(b & 127) << shift;
      if ((b & 128) != 0)
        continue;
      if (length > maxBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
        throw new InvalidDataException("Shared string exceeds limit or is truncated");
      return Utf8.GetString(reader.ReadBytes((int)length));
    }
    throw new InvalidDataException("Invalid shared string length");
  }
}
