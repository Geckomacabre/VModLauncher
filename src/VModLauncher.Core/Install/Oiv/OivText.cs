using System.Text;

namespace VModLauncher.Core.Install.Oiv;

/// <summary>Byte/text round trips that keep a file's encoding, BOM and line endings.</summary>
public static class OivText
{
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Decode(byte[] data, out Encoding encoding, out bool bom)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            encoding = new UTF8Encoding(false); bom = true;
            return encoding.GetString(data, 3, data.Length - 3);
        }
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            encoding = new UnicodeEncoding(false, false); bom = true;
            return encoding.GetString(data, 2, data.Length - 2);
        }
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            encoding = new UnicodeEncoding(true, false); bom = true;
            return encoding.GetString(data, 2, data.Length - 2);
        }
        bom = false;
        try
        {
            encoding = new UTF8Encoding(false);
            return StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            encoding = Encoding.Latin1;
            return encoding.GetString(data);
        }
    }

    public static byte[] Encode(string text, Encoding encoding, bool bom)
    {
        var body = encoding.GetBytes(text);
        if (!bom) return body;
        var preamble = encoding is UnicodeEncoding u
            ? (u.GetPreamble().Length > 0 ? u.GetPreamble() : (encoding.WebName == "utf-16BE" ? new byte[] { 0xFE, 0xFF } : new byte[] { 0xFF, 0xFE }))
            : new byte[] { 0xEF, 0xBB, 0xBF };
        var outp = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, outp, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, outp, preamble.Length, body.Length);
        return outp;
    }

    /// <summary>"\r\n" when the text mostly uses CRLF, otherwise "\n".</summary>
    public static string NewLine(string text)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++; else lf++;
        }
        return crlf > lf ? "\r\n" : "\n";
    }
}
