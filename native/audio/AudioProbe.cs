// Lists Windows render endpoints and records what WASAPI actually opens.
// A 5.1 label in the UI is not evidence; the mix format and a loopback
// recording are.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class AudioProbe
{
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coinit);

    private static readonly Guid KsDataFormatSubtypePcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid KsDataFormatSubtypeIeeeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");

    private static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "devices";
        CoInitializeEx(IntPtr.Zero, 0);
        try
        {
            if (cmd == "devices") return ListDevices();
            if (cmd == "tones") return PlayTones(Arg(args, "--seconds", 8));
            if (cmd == "play") return PlayFile(Require(args, 1), Arg(args, "--seconds", 0), ArgD(args, "--start", 0));
            if (cmd == "loopback") return Loopback(Require(args, 1), Arg(args, "--seconds", 6));
            Console.Error.WriteLine("usage: AudioProbe devices|tones|play <wav>|loopback <out.wav>");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static string Require(string[] args, int index)
    {
        if (args.Length <= index) throw new ArgumentException("missing argument " + index);
        return args[index];
    }

    private static int Arg(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return int.Parse(args[i + 1]);
        return fallback;
    }

    private static double ArgD(string[] args, string name, double fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
        return fallback;
    }

    private static int ListDevices()
    {
        IMMDeviceEnumerator en = CreateEnumerator();
        Console.WriteLine("DEFAULT");
        Describe(en.GetDefaultAudioEndpoint(0, 1), "default-console");
        IMMDeviceCollection col = en.EnumAudioEndpoints(0, 1);
        int n = col.GetCount();
        Console.WriteLine("RENDER_ENDPOINTS " + n);
        for (int i = 0; i < n; i++)
        {
            Console.WriteLine("--- [" + i + "] ---");
            Describe(col.Item(i), i.ToString());
        }
        return 0;
    }

    private static void Describe(IMMDevice dev, string tag)
    {
        string id = DeviceId(dev);
        string name = ReadName(dev);
        Console.WriteLine("id=" + id);
        Console.WriteLine("name=" + name);
        IAudioClient client = ActivateClient(dev);
        IntPtr mix = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out mix));
            WaveFormat fmt = WaveFormat.Read(mix);
            Console.WriteLine("mix_tag=" + fmt.Tag);
            Console.WriteLine("mix_channels=" + fmt.Channels);
            Console.WriteLine("mix_rate=" + fmt.SampleRate);
            Console.WriteLine("mix_bits=" + fmt.Bits);
            Console.WriteLine("mix_mask=0x" + fmt.ChannelMask.ToString("X"));
            Console.WriteLine("mix_sub=" + fmt.SubFormat);
            Console.WriteLine("mix_order=" + string.Join(",", ChannelNames(fmt.ChannelMask, fmt.Channels)));
            Console.WriteLine("surround_possible=" + (fmt.Channels >= 6 ? "yes" : "no"));
        }
        finally
        {
            if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
            Marshal.ReleaseComObject(client);
        }
        Console.WriteLine("tag=" + tag);
    }

    private static int PlayTones(int seconds)
    {
        // Distinct sine per Windows 5.1 slot. Silence on a speaker means that
        // slot was not opened, not that the label says 5.1.
        int rate = 48000;
        int frames = rate * Math.Max(1, seconds);
        float[] pcm = new float[frames * 6];
        double[] freq = { 220, 277, 330, 55, 440, 554 }; // FL FR FC LFE SL SR
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / rate;
            // Each channel alone for 0.8s, then all together.
            int slot = (int)(t / 0.8);
            for (int c = 0; c < 6; c++)
            {
                bool on = slot >= 6 || slot == c;
                if (!on) continue;
                double env = 0.25;
                if (c == 3) env = 0.5; // LFE needs a higher level to be measurable
                pcm[i * 6 + c] = (float)(env * Math.Sin(2 * Math.PI * freq[c] * t));
            }
        }
        return PlayFloat(pcm, 6, rate, "generated-tones");
    }

    private static int PlayFile(string path, int seconds, double startSec)
    {
        Wav wav = Wav.Load(path);
        Console.WriteLine("file_channels=" + wav.Channels);
        Console.WriteLine("file_rate=" + wav.SampleRate);
        Console.WriteLine("file_frames=" + wav.Frames);
        int start = (int)(startSec * wav.SampleRate);
        if (start < 0) start = 0;
        if (start > wav.Frames) start = 0;
        int count = seconds > 0 ? Math.Min(wav.Frames - start, seconds * wav.SampleRate) : wav.Frames - start;
        if (count <= 0) throw new InvalidOperationException("nothing to play");
        float[] slice = new float[count * wav.Channels];
        Array.Copy(wav.Samples, start * wav.Channels, slice, 0, slice.Length);
        return PlayFloat(slice, wav.Channels, wav.SampleRate, path);
    }

    private static int PlayFloat(float[] interleaved, int channels, int rate, string label)
    {
        IMMDevice dev = PickDevice();
        string name = ReadName(dev);
        IAudioClient client = ActivateClient(dev);
        IntPtr mix = IntPtr.Zero;
        IAudioRenderClient render = null;
        try
        {
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out mix));
            WaveFormat fmt = WaveFormat.Read(mix);
            Console.WriteLine("device=" + name);
            Console.WriteLine("opened_channels=" + fmt.Channels);
            Console.WriteLine("opened_rate=" + fmt.SampleRate);
            Console.WriteLine("opened_mask=0x" + fmt.ChannelMask.ToString("X"));
            Console.WriteLine("opened_order=" + string.Join(",", ChannelNames(fmt.ChannelMask, fmt.Channels)));
            Console.WriteLine("source=" + label);
            Console.WriteLine("source_channels=" + channels);
            if (fmt.Channels < 6)
            {
                Console.WriteLine("RESULT=STEREO_DEVICE");
                Console.WriteLine("detail=WASAPI shared mix format has " + fmt.Channels + " channels. Refusing to pretend this is 5.1.");
                return 3;
            }
            if (channels < 6)
            {
                Console.WriteLine("RESULT=SOURCE_NOT_MULTICHANNEL");
                return 4;
            }

            int[] map = MapToDevice(fmt.ChannelMask, fmt.Channels);
            Console.WriteLine("map_fl_fr_fc_lfe_sl_sr=" + string.Join(",", map));
            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] < 0)
                {
                    Console.WriteLine("RESULT=DEVICE_MASK_MISSING_SPEAKER index=" + i);
                    return 5;
                }
            }

            long period;
            long minPeriod;
            Marshal.ThrowExceptionForHR(client.GetDevicePeriod(out period, out minPeriod));
            Marshal.ThrowExceptionForHR(client.Initialize(0, 0, period * 4, 0, mix, Guid.Empty));
            uint bufferFrames;
            Marshal.ThrowExceptionForHR(client.GetBufferSize(out bufferFrames));
            Guid iid = typeof(IAudioRenderClient).GUID;
            IntPtr renderPtr;
            Marshal.ThrowExceptionForHR(client.GetService(ref iid, out renderPtr));
            render = (IAudioRenderClient)Marshal.GetObjectForIUnknown(renderPtr);
            Marshal.Release(renderPtr);

            bool ieee = fmt.IsFloat;
            int outCh = fmt.Channels;
            int srcRate = rate;
            int dstRate = fmt.SampleRate;
            long srcFrames = interleaved.Length / channels;
            // Resample with linear interpolation only when the device is not 48 kHz.
            double ratio = (double)dstRate / srcRate;
            long dstFrames = (long)(srcFrames * ratio);
            int frameCursor = 0;

            Marshal.ThrowExceptionForHR(client.Start());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (frameCursor < dstFrames)
            {
                uint pad;
                Marshal.ThrowExceptionForHR(client.GetCurrentPadding(out pad));
                int want = (int)bufferFrames - (int)pad;
                if (want <= 0)
                {
                    Thread.Sleep(2);
                    continue;
                }
                int take = (int)Math.Min(want, dstFrames - frameCursor);
                IntPtr buf;
                Marshal.ThrowExceptionForHR(render.GetBuffer((uint)take, out buf));
                if (ieee)
                {
                    float[] tmp = new float[take * outCh];
                    Fill(tmp, outCh, map, interleaved, channels, srcRate, dstRate, frameCursor, take);
                    Marshal.Copy(tmp, 0, buf, tmp.Length);
                }
                else
                {
                    short[] tmp = new short[take * outCh];
                    float[] f = new float[take * outCh];
                    Fill(f, outCh, map, interleaved, channels, srcRate, dstRate, frameCursor, take);
                    for (int i = 0; i < f.Length; i++)
                    {
                        float x = f[i];
                        if (x > 1) x = 1;
                        if (x < -1) x = -1;
                        tmp[i] = (short)(x * 32767);
                    }
                    Marshal.Copy(tmp, 0, buf, tmp.Length);
                }
                Marshal.ThrowExceptionForHR(render.ReleaseBuffer((uint)take, 0));
                frameCursor += take;
            }
            // Let the buffer drain.
            Thread.Sleep((int)(period / 10000) + 50);
            Marshal.ThrowExceptionForHR(client.Stop());
            sw.Stop();
            Console.WriteLine("played_frames=" + frameCursor);
            Console.WriteLine("elapsed_ms=" + sw.ElapsedMilliseconds);
            Console.WriteLine("RESULT=PLAYED_MULTICHANNEL");
            return 0;
        }
        finally
        {
            if (render != null) Marshal.ReleaseComObject(render);
            if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
            Marshal.ReleaseComObject(client);
            Marshal.ReleaseComObject(dev);
        }
    }

    private static void Fill(float[] dst, int outCh, int[] map, float[] src, int srcCh, int srcRate, int dstRate, int dstFrame0, int frames)
    {
        Array.Clear(dst, 0, dst.Length);
        double step = (double)srcRate / dstRate;
        int srcFrames = src.Length / srcCh;
        for (int i = 0; i < frames; i++)
        {
            double pos = (dstFrame0 + i) * step;
            int i0 = (int)pos;
            if (i0 >= srcFrames) break;
            int i1 = Math.Min(i0 + 1, srcFrames - 1);
            float frac = (float)(pos - i0);
            for (int c = 0; c < map.Length && c < srcCh; c++)
            {
                int di = map[c];
                if (di < 0 || di >= outCh) continue;
                float a = src[i0 * srcCh + c];
                float b = src[i1 * srcCh + c];
                dst[i * outCh + di] = a + (b - a) * frac;
            }
        }
    }

    // Source order is FL, FR, FC, LFE, SL, SR (WAVE 5.1, the AOMedia WAV order).
    private static int[] MapToDevice(uint mask, int channels)
    {
        uint[] bits = {
            0x1,    // FL
            0x2,    // FR
            0x4,    // FC
            0x8,    // LFE
            0x200,  // SIDE_LEFT preferred for 5.1 surround left
            0x400   // SIDE_RIGHT
        };
        // If the mask uses back speakers instead of side, fall back.
        if ((mask & 0x200) == 0 && (mask & 0x10) != 0) bits[4] = 0x10;
        if ((mask & 0x400) == 0 && (mask & 0x20) != 0) bits[5] = 0x20;
        int[] map = new int[6];
        for (int i = 0; i < 6; i++) map[i] = ChannelIndex(mask, bits[i], channels);
        return map;
    }

    private static int ChannelIndex(uint mask, uint bit, int channels)
    {
        if ((mask & bit) == 0) return -1;
        int index = 0;
        for (int b = 0; b < 32; b++)
        {
            uint flag = 1u << b;
            if ((mask & flag) == 0) continue;
            if (flag == bit) return index;
            index++;
            if (index >= channels) break;
        }
        return -1;
    }

    private static int Loopback(string outPath, int seconds)
    {
        IMMDevice dev = PickDevice();
        string name = ReadName(dev);
        IAudioClient client = ActivateClient(dev);
        IntPtr mix = IntPtr.Zero;
        IAudioCaptureClient cap = null;
        try
        {
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out mix));
            WaveFormat fmt = WaveFormat.Read(mix);
            Console.WriteLine("device=" + name);
            Console.WriteLine("loopback_channels=" + fmt.Channels);
            Console.WriteLine("loopback_rate=" + fmt.SampleRate);
            Console.WriteLine("loopback_mask=0x" + fmt.ChannelMask.ToString("X"));
            Console.WriteLine("loopback_float=" + fmt.IsFloat);
            if (fmt.Channels < 6)
            {
                Console.WriteLine("RESULT=LOOPBACK_NOT_MULTICHANNEL");
                return 3;
            }
            const uint LOOPBACK = 0x00020000;
            long period, minPeriod;
            Marshal.ThrowExceptionForHR(client.GetDevicePeriod(out period, out minPeriod));
            Marshal.ThrowExceptionForHR(client.Initialize(0, LOOPBACK, period * 4, 0, mix, Guid.Empty));
            Guid iid = typeof(IAudioCaptureClient).GUID;
            IntPtr capPtr;
            Marshal.ThrowExceptionForHR(client.GetService(ref iid, out capPtr));
            cap = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capPtr);
            Marshal.Release(capPtr);
            Marshal.ThrowExceptionForHR(client.Start());

            int ch = fmt.Channels;
            var samples = new List<float>(fmt.SampleRate * seconds * ch);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                uint packet;
                Marshal.ThrowExceptionForHR(cap.GetNextPacketSize(out packet));
                while (packet > 0)
                {
                    IntPtr data;
                    uint frames;
                    uint flags;
                    ulong pos, qpc;
                    Marshal.ThrowExceptionForHR(cap.GetBuffer(out data, out frames, out flags, out pos, out qpc));
                    int n = (int)frames * ch;
                    if ((flags & 0x2) != 0) // silent
                    {
                        for (int i = 0; i < n; i++) samples.Add(0);
                    }
                    else if (fmt.IsFloat)
                    {
                        float[] tmp = new float[n];
                        Marshal.Copy(data, tmp, 0, n);
                        samples.AddRange(tmp);
                    }
                    else
                    {
                        short[] tmp = new short[n];
                        Marshal.Copy(data, tmp, 0, n);
                        for (int i = 0; i < n; i++) samples.Add(tmp[i] / 32768f);
                    }
                    Marshal.ThrowExceptionForHR(cap.ReleaseBuffer(frames));
                    Marshal.ThrowExceptionForHR(cap.GetNextPacketSize(out packet));
                }
                Thread.Sleep(5);
            }
            Marshal.ThrowExceptionForHR(client.Stop());
            float[] all = samples.ToArray();
            int framesOut = all.Length / ch;
            Console.WriteLine("captured_frames=" + framesOut);
            double[] rms = new double[ch];
            int[] nonzero = new int[ch];
            for (int i = 0; i < framesOut; i++)
            {
                for (int c = 0; c < ch; c++)
                {
                    float s = all[i * ch + c];
                    rms[c] += s * s;
                    if (s > 0.001f || s < -0.001f) nonzero[c]++;
                }
            }
            for (int c = 0; c < ch; c++)
            {
                double r = Math.Sqrt(rms[c] / Math.Max(1, framesOut));
                double db = 20 * Math.Log10(r + 1e-12);
                Console.WriteLine("ch" + c + "_rms_db=" + db.ToString("F2") + " nonzero=" + nonzero[c]);
            }
            WriteWav(outPath, all, ch, fmt.SampleRate);
            Console.WriteLine("wrote=" + outPath);
            int active = 0;
            for (int c = 0; c < ch; c++) if (nonzero[c] > framesOut / 50) active++;
            Console.WriteLine("active_channels=" + active);
            Console.WriteLine(active >= 6 ? "RESULT=LOOPBACK_6CH" : "RESULT=LOOPBACK_FEWER_THAN_6");
            return active >= 6 ? 0 : 6;
        }
        finally
        {
            if (cap != null) Marshal.ReleaseComObject(cap);
            if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
            Marshal.ReleaseComObject(client);
            Marshal.ReleaseComObject(dev);
        }
    }

    private static void WriteWav(string path, float[] samples, int channels, int rate)
    {
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            int dataBytes = samples.Length * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + dataBytes);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16);
            w.Write((ushort)1);
            w.Write((ushort)channels);
            w.Write(rate);
            w.Write(rate * channels * 2);
            w.Write((ushort)(channels * 2));
            w.Write((ushort)16);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(dataBytes);
            for (int i = 0; i < samples.Length; i++)
            {
                float x = samples[i];
                if (x > 1) x = 1;
                if (x < -1) x = -1;
                w.Write((short)(x * 32767));
            }
        }
    }

    private static IMMDevice PickDevice()
    {
        string prefer = Environment.GetEnvironmentVariable("YS_AUDIO_DEVICE");
        IMMDeviceEnumerator en = CreateEnumerator();
        if (!string.IsNullOrEmpty(prefer))
        {
            IMMDeviceCollection col = en.EnumAudioEndpoints(0, 1);
            int n = col.GetCount();
            for (int i = 0; i < n; i++)
            {
                IMMDevice d = col.Item(i);
                if (ReadName(d).IndexOf(prefer, StringComparison.OrdinalIgnoreCase) >= 0) return d;
                Marshal.ReleaseComObject(d);
            }
            throw new InvalidOperationException("no render device matching " + prefer);
        }
        // Prefer an endpoint whose shared mix is already >= 6 channels.
        IMMDeviceCollection all = en.EnumAudioEndpoints(0, 1);
        int count = all.GetCount();
        IMMDevice fallback = null;
        for (int i = 0; i < count; i++)
        {
            IMMDevice d = all.Item(i);
            int ch = MixChannels(d);
            if (ch >= 6) return d;
            if (fallback == null) fallback = d;
            else Marshal.ReleaseComObject(d);
        }
        if (fallback != null) return fallback;
        return en.GetDefaultAudioEndpoint(0, 1);
    }

    private static int MixChannels(IMMDevice dev)
    {
        IAudioClient client = ActivateClient(dev);
        IntPtr mix = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out mix));
            return WaveFormat.Read(mix).Channels;
        }
        finally
        {
            if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
            Marshal.ReleaseComObject(client);
        }
    }

    private static string[] ChannelNames(uint mask, int channels)
    {
        string[] names = {
            "FL","FR","FC","LFE","BL","BR","FLC","FRC","BC","SL","SR",
            "TC","TFL","TFC","TFR","TBL","TBC","TBR"
        };
        var list = new List<string>();
        int seen = 0;
        for (int b = 0; b < 32 && seen < channels; b++)
        {
            if ((mask & (1u << b)) == 0) continue;
            list.Add(b < names.Length ? names[b] : ("BIT" + b));
            seen++;
        }
        while (list.Count < channels) list.Add("CH" + list.Count);
        return list.ToArray();
    }

    private static string DeviceId(IMMDevice dev)
    {
        string id;
        Marshal.ThrowExceptionForHR(dev.GetId(out id));
        return id ?? "";
    }

    private static string ReadName(IMMDevice dev)
    {
        IPropertyStore store;
        Marshal.ThrowExceptionForHR(dev.OpenPropertyStore(0, out store));
        try
        {
            PropertyKey key = new PropertyKey
            {
                fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
                pid = 14
            };
            PropVariant v;
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out v));
            try { return v.GetString() ?? ""; }
            finally { v.Clear(); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static IAudioClient ActivateClient(IMMDevice dev)
    {
        Guid iid = typeof(IAudioClient).GUID;
        IntPtr p;
        Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out p));
        IAudioClient client = (IAudioClient)Marshal.GetObjectForIUnknown(p);
        Marshal.Release(p);
        return client;
    }

    private static IMMDeviceEnumerator CreateEnumerator()
    {
        return (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
    }

    private struct WaveFormat
    {
        public int Tag, Channels, SampleRate, Bits;
        public uint ChannelMask;
        public Guid SubFormat;
        public bool IsFloat;

        public static WaveFormat Read(IntPtr p)
        {
            WaveFormat f = new WaveFormat();
            f.Tag = Marshal.ReadInt16(p, 0);
            f.Channels = Marshal.ReadInt16(p, 2);
            f.SampleRate = Marshal.ReadInt32(p, 4);
            f.Bits = Marshal.ReadInt16(p, 14);
            int cb = Marshal.ReadInt16(p, 16);
            f.ChannelMask = 0;
            f.SubFormat = Guid.Empty;
            if (f.Tag == -2 || f.Tag == 65534) // WAVE_FORMAT_EXTENSIBLE
            {
                if (cb >= 22)
                {
                    f.ChannelMask = (uint)Marshal.ReadInt32(p, 20);
                    byte[] g = new byte[16];
                    Marshal.Copy(IntPtr.Add(p, 24), g, 0, 16);
                    f.SubFormat = new Guid(g);
                    f.IsFloat = f.SubFormat == KsDataFormatSubtypeIeeeFloat;
                    if (f.SubFormat == KsDataFormatSubtypePcm) f.IsFloat = false;
                }
            }
            else
            {
                f.IsFloat = f.Tag == 3;
                if (f.Channels == 6) f.ChannelMask = 0x3F;
                else if (f.Channels == 2) f.ChannelMask = 0x3;
                else if (f.Channels == 1) f.ChannelMask = 0x4;
            }
            return f;
        }
    }

    private sealed class Wav
    {
        public int Channels, SampleRate, Frames;
        public float[] Samples;

        public static Wav Load(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 44) throw new InvalidDataException("wav too small");
            int pos = 12;
            int channels = 0, rate = 0, bits = 0, audioFormat = 0;
            uint mask = 0;
            byte[] pcm = null;
            while (pos + 8 <= data.Length)
            {
                string id = Encoding.ASCII.GetString(data, pos, 4);
                int size = BitConverter.ToInt32(data, pos + 4);
                int body = pos + 8;
                if (size < 0 || body + size > data.Length) break;
                if (id == "fmt ")
                {
                    audioFormat = BitConverter.ToUInt16(data, body);
                    channels = BitConverter.ToUInt16(data, body + 2);
                    rate = BitConverter.ToInt32(data, body + 4);
                    bits = BitConverter.ToUInt16(data, body + 14);
                    if (audioFormat == 65534 && size >= 40)
                        mask = BitConverter.ToUInt32(data, body + 20);
                }
                else if (id == "data")
                {
                    pcm = new byte[size];
                    Buffer.BlockCopy(data, body, pcm, 0, size);
                }
                pos = body + size + (size & 1);
            }
            if (pcm == null || channels <= 0) throw new InvalidDataException("wav missing data");
            int bytesPer = bits / 8;
            int frames = pcm.Length / (channels * bytesPer);
            float[] samples = new float[frames * channels];
            if (bits == 16)
            {
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
            }
            else if (bits == 32 && audioFormat == 3)
            {
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = BitConverter.ToSingle(pcm, i * 4);
            }
            else throw new InvalidDataException("unsupported wav bits=" + bits + " fmt=" + audioFormat);
            Wav w = new Wav();
            w.Channels = channels;
            w.SampleRate = rate;
            w.Frames = frames;
            w.Samples = samples;
            w.ToString();
            if (mask != 0) { /* order already in file; player maps by our FL..SR contract */ }
            return w;
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        IMMDeviceCollection EnumAudioEndpoints(int dataFlow, int stateMask);
        IMMDevice GetDefaultAudioEndpoint(int dataFlow, int role);
        [PreserveSig] int GetDevice(string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount();
        IMMDevice Item(int index);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int i, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, Guid session);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid riid, out IntPtr iface);
    }

    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint flags);
    }

    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFrames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFrames);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;

        public string GetString()
        {
            if (vt == 31 && p != IntPtr.Zero) return Marshal.PtrToStringUni(p);
            return null;
        }

        public void Clear()
        {
            PropVariant local = this;
            PropVariantClear(ref local);
            this = local;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}
