using System.Diagnostics;
using System.Text;

namespace StbEditor.Core;

/// <summary>
/// The Creation Kit's own audio tools in the game folder: LipGenerator (Tools\LipGen\LipGenerator, with FonixData.cdf) makes a
/// .lip from a WAV and the spoken text, xWMAEncode (Tools\Audio) turns a WAV into .xwm; a .fuz is the two packed together
/// ("FUZE", version 1, the .lip's size, the .lip, the audio), written here. LipGenerator reads mono 16-bit PCM only: other
/// WAVs are mixed down to that in a work folder first.
/// </summary>
public sealed class VoiceTools(string gameFolder)
{
    public string LipGenerator => Path.Combine(gameFolder, "Tools", "LipGen", "LipGenerator", "LipGenerator.exe");
    public string FonixData => Path.Combine(gameFolder, "Tools", "LipGen", "LipGenerator", "FonixData.cdf");
    public string XwmaEncode => Path.Combine(gameFolder, "Tools", "Audio", "xwmaencode.exe");

    public bool CanMakeLip => File.Exists(LipGenerator) && File.Exists(FonixData);
    public bool CanEncode => File.Exists(XwmaEncode);

    /// <summary>Makes <paramref name="lip"/> from <paramref name="wav"/>; null when it worked, else what went wrong.</summary>
    public string? MakeLip(string wav, string text, string lip, string workFolder)
    {
        Directory.CreateDirectory(workFolder);
        var input = Path.Combine(workFolder, "line.wav");
        if (MonoPcm(File.ReadAllBytes(wav)) is not { } mono) return "not a PCM WAV";
        File.WriteAllBytes(input, mono);
        var produced = Path.ChangeExtension(input, ".lip");
        if (File.Exists(produced)) File.Delete(produced);
        // Quotes would end the argument; the text only guides the phonemes.
        var spoken = text.Replace('"', '\'').Replace('\n', ' ').Replace('\r', ' ').Trim();
        var (code, output) = Run(LipGenerator, $"\"{input}\" \"{spoken}\"", workFolder);
        if (!File.Exists(produced)) return $"LipGenerator: {(output.Length > 0 ? output : $"exit {code}")}";
        Directory.CreateDirectory(Path.GetDirectoryName(lip)!);
        File.Copy(produced, lip, true);
        return null;
    }

    /// <summary>Makes <paramref name="fuz"/> from a .lip and the audio (.xwm as it is, .wav encoded first); null when it worked.</summary>
    public string? MakeFuz(string lip, string audio, string fuz, string workFolder)
    {
        byte[] xwm;
        if (audio.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(workFolder);
            var encoded = Path.Combine(workFolder, "line.xwm");
            if (File.Exists(encoded)) File.Delete(encoded);
            var (code, output) = Run(XwmaEncode, $"\"{audio}\" \"{encoded}\"", workFolder);
            if (!File.Exists(encoded)) return $"xWMAEncode: {(output.Length > 0 ? output : $"exit {code}")}";
            xwm = File.ReadAllBytes(encoded);
        }
        else xwm = File.ReadAllBytes(audio);
        Directory.CreateDirectory(Path.GetDirectoryName(fuz)!);
        File.WriteAllBytes(fuz, Fuz(File.ReadAllBytes(lip), xwm));
        return null;
    }

    public static byte[] Fuz(byte[] lip, byte[] xwm)
    {
        var result = new byte[12 + lip.Length + xwm.Length];
        Encoding.ASCII.GetBytes("FUZE").CopyTo(result, 0);
        BitConverter.GetBytes(1u).CopyTo(result, 4);
        BitConverter.GetBytes((uint)lip.Length).CopyTo(result, 8);
        lip.CopyTo(result, 12);
        xwm.CopyTo(result, 12 + lip.Length);
        return result;
    }

    /// <summary>The .lip and the audio inside a .fuz; null when it is not one.</summary>
    public static (byte[] Lip, byte[] Audio)? Unfuz(byte[] fuz)
    {
        if (fuz.Length < 12 || Encoding.ASCII.GetString(fuz, 0, 4) != "FUZE") return null;
        var size = (int)BitConverter.ToUInt32(fuz, 8);
        if (12 + size > fuz.Length) return null;
        return (fuz[12..(12 + size)], fuz[(12 + size)..]);
    }

    /// <summary>A WAV as mono 16-bit PCM at its own rate (channels averaged, 8/24/32-bit converted); null when it is not PCM.</summary>
    public static byte[]? MonoPcm(byte[] wav)
    {
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return null;
        int channels = 0, rate = 0, bits = 0, format = 0;
        ReadOnlySpan<byte> data = default;
        for (var at = 12; at + 8 <= wav.Length;)
        {
            var id = Encoding.ASCII.GetString(wav, at, 4);
            var size = (int)BitConverter.ToUInt32(wav, at + 4);
            var body = at + 8;
            if (body + size > wav.Length) size = wav.Length - body;
            if (id == "fmt ")
            {
                format = BitConverter.ToUInt16(wav, body);
                channels = BitConverter.ToUInt16(wav, body + 2);
                rate = (int)BitConverter.ToUInt32(wav, body + 4);
                bits = BitConverter.ToUInt16(wav, body + 14);
                if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(wav, body + 24);
            }
            else if (id == "data") data = wav.AsSpan(body, size);
            at = body + size + (size & 1);
        }
        if (format != 1 || channels < 1 || bits is not (8 or 16 or 24 or 32) || data.IsEmpty) return null;
        var bytesPer = bits / 8;
        var frames = data.Length / (bytesPer * channels);
        var samples = new short[frames];
        for (var f = 0; f < frames; f++)
        {
            long sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = (f * channels + c) * bytesPer;
                sum += bits switch
                {
                    8 => (data[o] - 128) << 8,
                    16 => BitConverter.ToInt16(data.Slice(o, 2)),
                    24 => (data[o + 2] << 24 | data[o + 1] << 16 | data[o] << 8) >> 16,
                    _ => BitConverter.ToInt32(data.Slice(o, 4)) >> 16,
                };
            }
            samples[f] = (short)(sum / channels);
        }
        var output = new byte[44 + frames * 2];
        void Put(int at, string s) => Encoding.ASCII.GetBytes(s).CopyTo(output, at);
        Put(0, "RIFF");
        BitConverter.GetBytes((uint)(36 + frames * 2)).CopyTo(output, 4);
        Put(8, "WAVE");
        Put(12, "fmt ");
        BitConverter.GetBytes(16u).CopyTo(output, 16);
        BitConverter.GetBytes((ushort)1).CopyTo(output, 20);
        BitConverter.GetBytes((ushort)1).CopyTo(output, 22);
        BitConverter.GetBytes((uint)rate).CopyTo(output, 24);
        BitConverter.GetBytes((uint)(rate * 2)).CopyTo(output, 28);
        BitConverter.GetBytes((ushort)2).CopyTo(output, 32);
        BitConverter.GetBytes((ushort)16).CopyTo(output, 34);
        Put(36, "data");
        BitConverter.GetBytes((uint)(frames * 2)).CopyTo(output, 40);
        Buffer.BlockCopy(samples, 0, output, 44, frames * 2);
        return output;
    }

    private static (int Code, string Output) Run(string exe, string arguments, string folder)
    {
        var start = new ProcessStartInfo(exe, arguments)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000)) { try { process.Kill(); } catch { } return (-1, "timed out"); }
        var text = (output.Result + "\n" + error.Result).Trim();
        // LipGenerator prints its banner first; keep what follows.
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("LipGenerator.exe, Version", StringComparison.Ordinal));
        return (process.ExitCode, string.Join(" ", lines));
    }
}
