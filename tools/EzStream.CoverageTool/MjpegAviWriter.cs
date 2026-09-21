using System.Drawing.Imaging;

namespace EzStream.CoverageTool;

internal static class MjpegAviWriter
{
    private const int Width = 320;
    private const int Height = 240;
    private const int FramesPerSecond = 10;
    private const int FrameCount = 80;
    private const int LongFrameCount = 240;

    public static void Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Create(path, FrameCount);
    }

    public static void CreateLong(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Create(path, LongFrameCount);
    }

    public static void CreateWithAudio(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var frames = CreateFrames(FrameCount);
        try
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new BinaryWriter(stream);
            WriteAviWithAudio(writer, frames);
        }
        finally
        {
            foreach (var frame in frames)
                frame.Dispose();
        }
    }

    private static void Create(string path, int frameCount)
    {
        var frames = CreateFrames(frameCount);
        try
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new BinaryWriter(stream);
            WriteAvi(writer, frames);
        }
        finally
        {
            foreach (var frame in frames)
                frame.Dispose();
        }
    }

    public static void CreateGif(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var bitmap = new Bitmap(Width, Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.DarkBlue);
        graphics.DrawString("EzStream unsupported codec", SystemFonts.DefaultFont,
            Brushes.White, new PointF(65, 105));
        bitmap.Save(path, ImageFormat.Gif);
    }

    public static void CreateJpeg(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var bitmap = new Bitmap(Width, Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.DarkGreen);
        graphics.DrawString("EzStream segment coverage", SystemFonts.DefaultFont,
            Brushes.White, new PointF(70, 105));
        bitmap.Save(path, ImageFormat.Jpeg);
    }

    private static List<MemoryStream> CreateFrames(int frameCount)
    {
        var frames = new List<MemoryStream>(frameCount);
        for (var index = 0; index < frameCount; index++)
        {
            using var bitmap = new Bitmap(Width, Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb((index * 17) % 255, (index * 31) % 255, (index * 47) % 255));
            graphics.DrawString($"EzStream coverage {index + 1}", SystemFonts.DefaultFont,
                Brushes.White, new PointF(60, 105));
            var frame = new MemoryStream();
            bitmap.Save(frame, ImageFormat.Jpeg);
            frame.Position = 0;
            frames.Add(frame);
        }
        return frames;
    }

    private static void WriteAvi(BinaryWriter writer, IReadOnlyList<MemoryStream> frames)
    {
        var riff = BeginContainer(writer, "RIFF", "AVI ");
        WriteHeaderList(writer, frames);
        var movie = BeginContainer(writer, "LIST", "movi");
        var offsets = WriteFrames(writer, frames, movie.DataStart);
        EndContainer(writer, movie);
        WriteIndex(writer, frames, offsets);
        EndContainer(writer, riff);
    }

    private static void WriteAviWithAudio(BinaryWriter writer, List<MemoryStream> frames)
    {
        var riff = BeginContainer(writer, "RIFF", "AVI ");
        var header = BeginContainer(writer, "LIST", "hdrl");
        WriteMainHeader(writer, frames, streamCount: 2);
        var videoList = BeginContainer(writer, "LIST", "strl");
        WriteStreamHeader(writer, frames);
        WriteBitmapHeader(writer, frames);
        EndContainer(writer, videoList);
        WriteAudioStreamList(writer, frames.Count);
        EndContainer(writer, header);

        var movie = BeginContainer(writer, "LIST", "movi");
        var entries = WriteAudioVideoFrames(writer, frames, movie.DataStart);
        EndContainer(writer, movie);
        WriteAudioVideoIndex(writer, entries);
        EndContainer(writer, riff);
    }

    private static void WriteHeaderList(BinaryWriter writer, IReadOnlyList<MemoryStream> frames)
    {
        var header = BeginContainer(writer, "LIST", "hdrl");
        WriteMainHeader(writer, frames);
        var streamList = BeginContainer(writer, "LIST", "strl");
        WriteStreamHeader(writer, frames);
        WriteBitmapHeader(writer, frames);
        EndContainer(writer, streamList);
        EndContainer(writer, header);
    }

    private static void WriteMainHeader(BinaryWriter writer, IReadOnlyList<MemoryStream> frames)
        => WriteMainHeader(writer, frames, streamCount: 1);

    private static void WriteMainHeader(
        BinaryWriter writer,
        IReadOnlyList<MemoryStream> frames,
        int streamCount)
    {
        WriteFourCc(writer, "avih");
        writer.Write(56);
        writer.Write(1_000_000 / FramesPerSecond);
        writer.Write((int)(LargestFrame(frames) * FramesPerSecond));
        writer.Write(0);
        writer.Write(0x10);
        writer.Write(frames.Count);
        writer.Write(0);
        writer.Write(streamCount);
        writer.Write(LargestFrame(frames));
        writer.Write(Width);
        writer.Write(Height);
        WriteZeros(writer, 4);
    }

    private static void WriteAudioStreamList(BinaryWriter writer, int frameCount)
    {
        const int sampleRate = 8000;
        const int samplesPerFrame = sampleRate / FramesPerSecond;
        var streamList = BeginContainer(writer, "LIST", "strl");
        WriteFourCc(writer, "strh");
        writer.Write(56);
        WriteFourCc(writer, "auds");
        writer.Write(0);
        writer.Write(0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write(0);
        writer.Write(1);
        writer.Write(sampleRate);
        writer.Write(0);
        writer.Write(checked(frameCount * samplesPerFrame));
        writer.Write(samplesPerFrame);
        writer.Write(-1);
        writer.Write(1);
        WriteZeros(writer, 4);

        WriteFourCc(writer, "strf");
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate);
        writer.Write((short)1);
        writer.Write((short)8);
        EndContainer(writer, streamList);
    }

    private static List<AviIndexEntry> WriteAudioVideoFrames(
        BinaryWriter writer,
        List<MemoryStream> frames,
        long movieDataStart)
    {
        const int audioBytesPerFrame = 800;
        var audio = new byte[audioBytesPerFrame];
        Array.Fill(audio, (byte)128);
        var entries = new List<AviIndexEntry>(frames.Count * 2);
        foreach (var frame in frames)
        {
            entries.Add(new AviIndexEntry("00dc",
                checked((int)(writer.BaseStream.Position - movieDataStart)), checked((int)frame.Length)));
            WriteFourCc(writer, "00dc");
            writer.Write(checked((int)frame.Length));
            frame.CopyTo(writer.BaseStream);
            if ((frame.Length & 1) != 0)
                writer.Write((byte)0);

            entries.Add(new AviIndexEntry("01wb",
                checked((int)(writer.BaseStream.Position - movieDataStart)), audio.Length));
            WriteFourCc(writer, "01wb");
            writer.Write(audio.Length);
            writer.Write(audio);
        }
        return entries;
    }

    private static void WriteAudioVideoIndex(BinaryWriter writer, IReadOnlyList<AviIndexEntry> entries)
    {
        WriteFourCc(writer, "idx1");
        writer.Write(checked(entries.Count * 16));
        foreach (var entry in entries)
        {
            WriteFourCc(writer, entry.ChunkId);
            writer.Write(entry.ChunkId == "00dc" ? 0x10 : 0);
            writer.Write(entry.Offset);
            writer.Write(entry.Length);
        }
    }

    private static void WriteStreamHeader(BinaryWriter writer, IReadOnlyList<MemoryStream> frames)
    {
        WriteFourCc(writer, "strh");
        writer.Write(56);
        WriteFourCc(writer, "vids");
        WriteFourCc(writer, "MJPG");
        writer.Write(0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write(0);
        writer.Write(1);
        writer.Write(FramesPerSecond);
        writer.Write(0);
        writer.Write(frames.Count);
        writer.Write(LargestFrame(frames));
        writer.Write(-1);
        writer.Write(0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)Width);
        writer.Write((short)Height);
    }

    private static void WriteBitmapHeader(BinaryWriter writer, IReadOnlyList<MemoryStream> frames)
    {
        WriteFourCc(writer, "strf");
        writer.Write(40);
        writer.Write(40);
        writer.Write(Width);
        writer.Write(Height);
        writer.Write((short)1);
        writer.Write((short)24);
        WriteFourCc(writer, "MJPG");
        writer.Write(LargestFrame(frames));
        WriteZeros(writer, 4);
    }

    private static List<int> WriteFrames(
        BinaryWriter writer,
        IReadOnlyList<MemoryStream> frames,
        long movieDataStart)
    {
        var offsets = new List<int>(frames.Count);
        foreach (var frame in frames)
        {
            offsets.Add(checked((int)(writer.BaseStream.Position - movieDataStart)));
            WriteFourCc(writer, "00dc");
            writer.Write(checked((int)frame.Length));
            frame.CopyTo(writer.BaseStream);
            if ((frame.Length & 1) != 0)
                writer.Write((byte)0);
        }
        return offsets;
    }

    private static void WriteIndex(
        BinaryWriter writer,
        IReadOnlyList<MemoryStream> frames,
        List<int> offsets)
    {
        WriteFourCc(writer, "idx1");
        writer.Write(checked(frames.Count * 16));
        for (var index = 0; index < frames.Count; index++)
        {
            WriteFourCc(writer, "00dc");
            writer.Write(0x10);
            writer.Write(offsets[index]);
            writer.Write(checked((int)frames[index].Length));
        }
    }

    private static ContainerPosition BeginContainer(BinaryWriter writer, string id, string type)
    {
        WriteFourCc(writer, id);
        var sizePosition = writer.BaseStream.Position;
        writer.Write(0);
        WriteFourCc(writer, type);
        return new ContainerPosition(sizePosition, writer.BaseStream.Position);
    }

    private static void EndContainer(BinaryWriter writer, ContainerPosition position)
    {
        var end = writer.BaseStream.Position;
        writer.BaseStream.Position = position.SizePosition;
        writer.Write(checked((int)(end - position.SizePosition - sizeof(int))));
        writer.BaseStream.Position = end;
        if ((end & 1) != 0)
            writer.Write((byte)0);
    }

    private static int LargestFrame(IEnumerable<MemoryStream> frames)
        => checked((int)frames.Max(static frame => frame.Length));

    private static void WriteFourCc(BinaryWriter writer, string value)
    {
        if (value.Length != 4)
            throw new ArgumentException("FourCC는 네 글자여야 합니다.", nameof(value));
        foreach (var character in value)
            writer.Write(checked((byte)character));
    }

    private static void WriteZeros(BinaryWriter writer, int count)
    {
        for (var index = 0; index < count; index++)
            writer.Write(0);
    }

    private readonly record struct ContainerPosition(long SizePosition, long DataStart);
    private readonly record struct AviIndexEntry(string ChunkId, int Offset, int Length);
}
