using System;
using System.IO;
using System.Text;

namespace Blackbird.Services;

/// <summary>
/// Reads and writes modders' text files without changing their encoding. BO3 sources are a mix of
/// ANSI, UTF-8 and UTF-8 with a BOM; a plain ReadAllText/WriteAllText turns ANSI bytes into U+FFFD
/// and drops BOMs. Text read here is written back byte-for-byte except where the text changed.
/// </summary>
public static class TextFiles
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>A file's text and the encoding (BOM included) to write it back with.</summary>
    public readonly record struct Content(string Text, Encoding Encoding);

    public static Content Read(string path) => Decode(File.ReadAllBytes(path));

    public static Content Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new Content(Utf8NoBom.GetString(bytes, 3, bytes.Length - 3), Utf8WithBom);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new Content(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new Content(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode);

        try
        {
            return new Content(StrictUtf8.GetString(bytes), Utf8NoBom);
        }
        catch (DecoderFallbackException)
        {
            // ANSI (usually Windows-1252): Latin-1 maps every byte to one char and back, so it round-trips exactly.
            return new Content(Encoding.Latin1.GetString(bytes), Encoding.Latin1);
        }
    }

    public static byte[] Encode(string text, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);
        if (preamble.Length == 0)
            return body;

        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    /// <summary>Writes <paramref name="text"/> with <paramref name="encoding"/>'s BOM, if it has one.</summary>
    public static void Write(string path, string text, Encoding encoding, FileMode mode = FileMode.Create)
    {
        using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.None);
        stream.Write(Encode(text, encoding));
    }
}
