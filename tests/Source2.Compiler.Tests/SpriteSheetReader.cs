// Ported from ValveResourceFormat (MIT, https://github.com/ValveResourceFormat/ValveResourceFormat),
// Resource/ResourceTypes/Texture.cs (GetSpriteSheetData), to read SHEET extra data back in the tests.
using System.Numerics;
using System.Text;
using Source2.Compiler.Textures;

namespace Source2.Compiler.Tests;

/// <summary>A texture's SHEET extra data, read back.</summary>
internal sealed class SpritesheetData
{
    public sealed class Sequence
    {
        public sealed class Frame
        {
            public sealed class Image
            {
                public Vector2 CroppedMin { get; set; }
                public Vector2 CroppedMax { get; set; }
                public Vector2 UncroppedMin { get; set; }
                public Vector2 UncroppedMax { get; set; }
            }

            public float DisplayTime { get; set; }
            public Image[] Images { get; set; } = [];
        }

        public uint Id { get; set; }
        public bool Clamp { get; set; }
        public bool AlphaCrop { get; set; }
        public bool NoColor { get; set; }
        public bool NoAlpha { get; set; }
        public float TotalTime { get; set; }
        public string Name { get; set; } = "";
        public Dictionary<string, float> FloatParams { get; } = [];
        public Frame[] Frames { get; set; } = [];
    }

    public Sequence[] Sequences { get; set; } = [];
}

internal static class SpriteSheetReader
{
    /// <summary>The SHEET extra data (version 8), or null when the texture has none.</summary>
    public static SpritesheetData? GetSpriteSheetData(this CompiledTexture texture)
    {
        if (!texture.ExtraData.TryGetValue(VTexExtraData.SHEET, out var bytes))
            return null;
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var version = reader.ReadUInt32();
        if (version != 8)
            throw new InvalidDataException($"SHEET version {version}, not 8.");
        var sequences = new SpritesheetData.Sequence[reader.ReadUInt32()];
        for (var s = 0; s < sequences.Length; s++)
        {
            var sequence = new SpritesheetData.Sequence
            {
                Id = reader.ReadUInt32(),
                Clamp = reader.ReadBoolean(),
                AlphaCrop = reader.ReadBoolean(),
                NoColor = reader.ReadBoolean(),
                NoAlpha = reader.ReadBoolean(),
            };
            var framesOffset = reader.BaseStream.Position + reader.ReadInt32();
            var numFrames = reader.ReadUInt32();
            sequence.TotalTime = reader.ReadSingle();
            sequence.Name = OffsetString(reader);
            var floatParamsOffset = reader.BaseStream.Position + reader.ReadInt32();
            var floatParamsCount = reader.ReadUInt32();
            var endOfHeader = reader.BaseStream.Position;
            if (floatParamsCount > 0)
            {
                reader.BaseStream.Position = floatParamsOffset;
                for (var p = 0; p < floatParamsCount; p++)
                {
                    var name = OffsetString(reader);
                    sequence.FloatParams.Add(name, reader.ReadSingle());
                }
            }
            reader.BaseStream.Position = framesOffset;
            sequence.Frames = new SpritesheetData.Sequence.Frame[numFrames];
            for (var f = 0; f < numFrames; f++)
            {
                var displayTime = reader.ReadSingle();
                var imageOffset = reader.BaseStream.Position + reader.ReadInt32();
                var images = new SpritesheetData.Sequence.Frame.Image[reader.ReadUInt32()];
                var back = reader.BaseStream.Position;
                reader.BaseStream.Position = imageOffset;
                for (var i = 0; i < images.Length; i++)
                    images[i] = new SpritesheetData.Sequence.Frame.Image
                    {
                        CroppedMin = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                        CroppedMax = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                        UncroppedMin = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                        UncroppedMax = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    };
                sequence.Frames[f] = new SpritesheetData.Sequence.Frame { DisplayTime = displayTime, Images = images };
                reader.BaseStream.Position = back;
            }
            reader.BaseStream.Position = endOfHeader;
            sequences[s] = sequence;
        }
        return new SpritesheetData { Sequences = sequences };
    }

    // An i32 offset from the field to a NUL-terminated UTF-8 string (0: empty), the reader left after the field.
    private static string OffsetString(BinaryReader reader)
    {
        var at = reader.BaseStream.Position;
        var offset = reader.ReadInt32();
        if (offset == 0)
            return string.Empty;
        reader.BaseStream.Position = at + offset;
        var bytes = new List<byte>();
        for (var b = reader.ReadByte(); b != 0; b = reader.ReadByte())
            bytes.Add(b);
        reader.BaseStream.Position = at + 4;
        return Encoding.UTF8.GetString([.. bytes]);
    }
}
