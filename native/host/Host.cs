// YouTube Surround native host.
// Speaks Chrome native messaging on stdin/stdout, downloads an IAMF audio
// stream with yt-dlp's Samsung TV client, decodes it to 6-channel PCM, and
// plays that PCM on a WASAPI endpoint whose shared format really is 5.1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

internal static class Host
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
    private static readonly object Gate = new object();
    private static Stream Stdout;
    private static string LogPath;
    private static volatile bool Running = true;
    private static string State = "idle";
    private static string Detail = "Idle";
    private static string VideoId = "";
    private static string DeviceName = "";
    private static int DeviceChannels;
    private static int OutputChannels;
    private static int SampleRate;
    private static int MixId = -1;
    private static double LatencyMs;
    private static string WavPath;
    private static string M4aPath;
    private static Process Child;
    private static Clock ClockNow = new Clock();
    private static bool PlayTones;
    private static double CliStopAt = -1;
    private static volatile int OutputMilli = 1000;

    private static int Main(string[] args)
    {
        CoInitializeEx(IntPtr.Zero, 0);
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouTubeSurround");
        Directory.CreateDirectory(dir);
        LogPath = Path.Combine(dir, "host.log");
        Log("start argc=" + args.Length + " cmd=" + Environment.CommandLine);
        try
        {
            if (args.Length > 0 && args[0] == "play-url") return PlayUrl(args);
            if (args.Length > 0 && args[0] == "tones") { PlayGeneratedTones(Arg(args, "--seconds", 8)); return 0; }
            return NativeLoop();
        }
        catch (Exception ex)
        {
            Log("fatal " + ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int PlayUrl(string[] args)
    {
        string url = null;
        for (int i = 1; i < args.Length; i++) if (!args[i].StartsWith("-")) { url = args[i]; break; }
        if (url == null) { Console.Error.WriteLine("missing url"); return 2; }
        double start = ArgD(args, "--start", 0);
        int seconds = Arg(args, "--seconds", 0);
        PrepareDevice();
        if (DeviceChannels < 6)
        {
            Console.WriteLine("RESULT=DEVICE_NOT_5_1 channels=" + DeviceChannels + " device=" + DeviceName);
            return 3;
        }
        try
        {
            string m4a = Download(url);
            M4aPath = m4a;
            string wav = Decode(m4a, seconds > 0 ? start + seconds + 1 : 0);
            WavPath = wav;
            lock (Gate)
            {
                ClockNow = new Clock { Time = start, Rate = 1, Paused = false, Mono = Stopwatch.GetTimestamp() };
                CliStopAt = seconds > 0 ? start + seconds : -1;
                State = "active";
                Detail = "5.1 is playing.";
            }
            var thread = new Thread(PlayerLoop);
            thread.IsBackground = true;
            thread.Start();
            if (seconds > 0) Thread.Sleep((seconds + 2) * 1000);
            else thread.Join();
            Running = false;
            thread.Join(2000);
            Console.WriteLine("RESULT=PLAYED device=" + DeviceName + " channels=" + OutputChannels + " rate=" + SampleRate);
            return OutputChannels == 6 ? 0 : 4;
        }
        catch (Exception ex)
        {
            Log("play-url " + ex.Message);
            if (ex.Message.IndexOf("no IAMF", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Console.WriteLine("RESULT=STEREO " + Tail(ex.Message));
                return 0;
            }
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            Running = false;
            StopPlayback();
        }
    }

    private static int NativeLoop()
    {
        Stdout = Console.OpenStandardOutput();
        Stream stdin = Console.OpenStandardInput();
        PrepareDevice();
        Publish();
        var player = new Thread(PlayerLoop);
        player.IsBackground = true;
        player.Start();
        byte[] lenb = new byte[4];
        while (Running)
        {
            if (!ReadExact(stdin, lenb)) break;
            int n = BitConverter.ToInt32(lenb, 0);
            if (n <= 0 || n > 1024 * 1024) break;
            byte[] body = new byte[n];
            if (!ReadExact(stdin, body)) break;
            Dictionary<string, object> msg;
            try { msg = Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body)); }
            catch (Exception ex) { Log("bad json " + ex.Message); continue; }
            Handle(msg);
        }
        Running = false;
        StopPlayback();
        return 0;
    }

    private static void Handle(Dictionary<string, object> msg)
    {
        string type = Str(msg, "type");
        Log("msg " + type + " video=" + Str(msg, "videoId") + " t=" + Num(msg, "time", -1).ToString("F2", CultureInfo.InvariantCulture));
        if (type == "stop")
        {
            StopPlayback();
            ClearMeasured();
            State = "idle";
            Detail = "Surround is off. YouTube stereo continues.";
            Publish();
            return;
        }
        if (type == "tones")
        {
            PlayTones = true;
            State = "active";
            Detail = "Playing a different tone on each speaker.";
            Publish();
            return;
        }
        if (type == "clock")
        {
            if (Str(msg, "videoId") != VideoId || string.IsNullOrEmpty(VideoId)) return;
            ApplyUserMix(msg);
            UpdateClock(Num(msg, "time", 0), Num(msg, "rate", 1), Bool(msg, "paused") || Bool(msg, "ad"));
            return;
        }
        if (type == "start")
        {
            string id = Str(msg, "videoId");
            if (!Regex.IsMatch(id ?? "", "^[A-Za-z0-9_-]{11}$"))
            {
                State = "error";
                Detail = "This page has no YouTube video id.";
                Publish();
                return;
            }
            if (id == VideoId && (State == "active" || State == "decoding" || State == "checking"))
            {
                UpdateClock(Num(msg, "time", 0), Num(msg, "rate", 1), Bool(msg, "paused") || Bool(msg, "ad"));
                return;
            }
            StopPlayback();
            ClearMeasured();
            VideoId = id;
            UpdateClock(Num(msg, "time", 0), Num(msg, "rate", 1), Bool(msg, "paused") || Bool(msg, "ad"));
            ApplyUserMix(msg);
            string url = "https://www.youtube.com/watch?v=" + id;
            var worker = new Thread(() => Load(url, id));
            worker.IsBackground = true;
            worker.Start();
        }
    }

    private static void Load(string url, string id)
    {
        try
        {
            if (DeviceChannels < 6)
            {
                ClearMeasured();
                State = "unavailable";
                Detail = "Windows is not using a 5.1 device (" + DeviceName + ", " + DeviceChannels + " channels). Stereo continues.";
                Publish();
                return;
            }
            ClearMeasured();
            State = "checking";
            Detail = "Looking for an IAMF audio stream.";
            Publish();
            string m4a = Download(url);
            if (VideoId != id) { TryDelete(m4a); return; }
            M4aPath = m4a;
            State = "decoding";
            Detail = "Decoding IAMF to 5.1 PCM.";
            Publish();
            string wav = Decode(m4a, 0);
            if (VideoId != id) { TryDelete(wav); StopPlayback(); return; }
            WavPath = wav;
            State = "active";
            OutputChannels = 6;
            Detail = "5.1 is playing. YouTube's stereo track is muted.";
            Publish();
        }
        catch (Exception ex)
        {
            Log("load failed " + ex.Message);
            if (VideoId != id) return;
            StopPlayback();
            ClearMeasured();
            if (ex.Message.IndexOf("no IAMF", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                State = "stereo";
                Detail = "This video has no IAMF surround stream. Stereo continues.";
            }
            else
            {
                State = "error";
                Detail = ex.Message;
            }
            Publish();
        }
    }

    private static string Download(string url)
    {
        string ytdlp = Tool("yt-dlp.exe", "YS_YTDLP");
        string deno = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "deno.exe");
        string runtime = File.Exists(deno) ? " --js-runtimes " + Q("deno:" + deno) : "";
        string m4a = Path.Combine(Path.GetTempPath(), "ys-" + Guid.NewGuid().ToString("N") + ".m4a");
        string output;
        int code = Run(ytdlp, "--no-playlist --no-warnings" + runtime + " --extractor-args " + Q("youtube:player_client=tv_samsung")
            + " -f 773 --fixup never -o " + Q(m4a) + " " + Q(url), out output, 90000);
        if (code != 0 || !File.Exists(m4a) || new FileInfo(m4a).Length < 1000)
        {
            TryDelete(m4a);
            throw new InvalidOperationException("no IAMF stream from the Samsung TV client. " + Tail(output));
        }
        Log("downloaded " + new FileInfo(m4a).Length + " bytes");
        return m4a;
    }

    private static string Decode(string m4a, double maxSeconds)
    {
        string decoder = Tool("ys-iamf.exe", "YS_IAMF");
        string wav = Path.ChangeExtension(m4a, ".wav");
        string args = Q(m4a) + " -o " + Q(wav);
        if (maxSeconds > 0) args += " --max-seconds " + maxSeconds.ToString("F0", CultureInfo.InvariantCulture);
        string output;
        int code = Run(decoder, args, out output, 180000);
        if (code == -2) throw new InvalidOperationException("decoder timed out");
        var mix = Regex.Match(output, @"mix_id=(\d+)");
        var ch = Regex.Match(output, @"channels=(\d+)");
        var rate = Regex.Match(output, @"rate=(\d+)");
        if (mix.Success) MixId = int.Parse(mix.Groups[1].Value);
        if (ch.Success) OutputChannels = int.Parse(ch.Groups[1].Value);
        if (rate.Success) SampleRate = int.Parse(rate.Groups[1].Value);
        if (code != 0 || OutputChannels != 6 || !File.Exists(wav))
        {
            TryDelete(wav);
            throw new InvalidOperationException("decoder did not produce 6 channels. " + Tail(output));
        }
        Log("decoded channels=" + OutputChannels + " rate=" + SampleRate + " mix=" + MixId);
        return wav;
    }

    private static void PlayerLoop()
    {
        CoInitializeEx(IntPtr.Zero, 0);
        while (Running)
        {
            if (PlayTones)
            {
                PlayTones = false;
                try { PlayGeneratedTones(8); }
                catch (Exception ex) { Log("tones " + ex.Message); }
                continue;
            }
            string wav;
            Clock clock;
            double stopAt;
            lock (Gate)
            {
                wav = WavPath;
                clock = ClockNow;
                stopAt = CliStopAt;
            }
            if (string.IsNullOrEmpty(wav) || !File.Exists(wav) || State != "active")
            {
                Thread.Sleep(30);
                continue;
            }
            try { PlayWavFollowClock(wav, clock, stopAt); }
            catch (Exception ex)
            {
                Log("play " + ex.Message);
                State = "error";
                Detail = "Playback failed: " + ex.Message;
                Publish();
                WavPath = null;
            }
            if (stopAt > 0) { Running = false; break; }
        }
    }

    private static void PlayWavFollowClock(string path, Clock initial, double stopAt)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, (FileShare)7))
        {
            var header = new byte[44];
            if (fs.Read(header, 0, 44) != 44) throw new InvalidDataException("short wav");
            int channels = BitConverter.ToInt16(header, 22);
            int rate = BitConverter.ToInt32(header, 24);
            if (channels != 6) throw new InvalidOperationException("wav has " + channels + " channels");
            SampleRate = rate;
            IMMDevice dev = PickDevice();
            IAudioClient client = Activate(dev);
            IntPtr mix = IntPtr.Zero;
            IAudioRenderClient render = null;
            try
            {
                Marshal.ThrowExceptionForHR(client.GetMixFormat(out mix));
                WaveFormat fmt = WaveFormat.Read(mix);
                DeviceName = ReadName(dev);
                DeviceChannels = fmt.Channels;
                if (fmt.Channels < 6)
                    throw new InvalidOperationException("device mix is " + fmt.Channels + " channels");
                int[] map = MapToDevice(fmt.ChannelMask, fmt.Channels);
                long period, minPeriod;
                Marshal.ThrowExceptionForHR(client.GetDevicePeriod(out period, out minPeriod));
                Marshal.ThrowExceptionForHR(client.Initialize(0, 0, period * 4, 0, mix, Guid.Empty));
                long latency;
                Marshal.ThrowExceptionForHR(client.GetStreamLatency(out latency));
                LatencyMs = latency / 10000.0;
                uint bufferFrames;
                Marshal.ThrowExceptionForHR(client.GetBufferSize(out bufferFrames));
                Guid iid = typeof(IAudioRenderClient).GUID;
                IntPtr rp;
                Marshal.ThrowExceptionForHR(client.GetService(ref iid, out rp));
                render = (IAudioRenderClient)Marshal.GetObjectForIUnknown(rp);
                Marshal.Release(rp);
                Marshal.ThrowExceptionForHR(client.Start());
                Publish();
                var frame = new byte[channels * 2];
                while (Running && WavPath == path && !PlayTones)
                {
                    Clock clock;
                    lock (Gate) clock = ClockNow;
                    double media = clock.MediaSeconds();
                    if (stopAt > 0 && media >= stopAt) break;
                    uint pad;
                    Marshal.ThrowExceptionForHR(client.GetCurrentPadding(out pad));
                    int want = (int)bufferFrames - (int)pad;
                    if (want <= 0) { Thread.Sleep(4); continue; }
                    IntPtr buf;
                    Marshal.ThrowExceptionForHR(render.GetBuffer((uint)want, out buf));
                    float[] dst = new float[want * fmt.Channels];
                    if (!clock.Paused)
                    {
                        double ahead = (pad / (double)fmt.SampleRate) + (LatencyMs / 1000.0);
                        double srcOrigin = (media + ahead) * rate;
                        double step = clock.Rate <= 0 ? 1 : clock.Rate;
                        if (Math.Abs(step - 1.0) < 0.001)
                        {
                            long idx = (long)srcOrigin;
                            if (idx >= 0)
                            {
                                byte[] block = new byte[want * frame.Length];
                                fs.Position = 44L + idx * frame.Length;
                                int got = fs.Read(block, 0, block.Length);
                                int framesGot = got / frame.Length;
                                for (int i = 0; i < framesGot; i++)
                                {
                                    for (int c = 0; c < 6; c++)
                                    {
                                        if (map[c] < 0 || map[c] >= fmt.Channels) continue;
                                        short s = BitConverter.ToInt16(block, i * frame.Length + c * 2);
                                        dst[i * fmt.Channels + map[c]] = s / 32768f * (OutputMilli / 1000f);
                                    }
                                }
                            }
                        }
                        else
                        {
                            for (int i = 0; i < want; i++)
                            {
                                long idx = (long)(srcOrigin + i * step);
                                if (idx < 0) continue;
                                fs.Position = 44L + idx * frame.Length;
                                if (fs.Read(frame, 0, frame.Length) != frame.Length) break;
                                for (int c = 0; c < 6; c++)
                                {
                                    if (map[c] < 0 || map[c] >= fmt.Channels) continue;
                                    short s = BitConverter.ToInt16(frame, c * 2);
                                    dst[i * fmt.Channels + map[c]] = s / 32768f * (OutputMilli / 1000f);
                                }
                            }
                        }
                    }
                    if (!fmt.IsFloat)
                        throw new InvalidOperationException("device mix format is not float");
                    Marshal.Copy(dst, 0, buf, dst.Length);
                    Marshal.ThrowExceptionForHR(render.ReleaseBuffer((uint)want, 0));
                }
                Marshal.ThrowExceptionForHR(client.Stop());
            }
            finally
            {
                if (render != null) Marshal.ReleaseComObject(render);
                if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
                Marshal.ReleaseComObject(client);
                Marshal.ReleaseComObject(dev);
            }
        }
    }

    private static void PlayGeneratedTones(int seconds)
    {
        PrepareDevice();
        if (DeviceChannels < 6) throw new InvalidOperationException("no 5.1 device");
        int rate = 48000;
        int frames = rate * seconds;
        float[] pcm = new float[frames * 6];
        double[] freq = { 220, 277, 330, 55, 440, 554 };
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / rate;
            int slot = (int)(t / 0.8);
            for (int c = 0; c < 6; c++)
            {
                if (slot < 6 && slot != c) continue;
                double env = c == 3 ? 0.5 : 0.25;
                pcm[i * 6 + c] = (float)(env * Math.Sin(2 * Math.PI * freq[c] * t));
            }
        }
        // Reuse the wav player by writing a temp wav.
        string path = Path.Combine(Path.GetTempPath(), "ys-tones.wav");
        WriteWav(path, pcm, 6, rate);
        string previous = WavPath;
        string previousState = State;
        string previousDetail = Detail;
        WavPath = path;
        State = "active";
        Detail = "Playing a different tone on each speaker.";
        lock (Gate) { ClockNow = new Clock { Time = 0, Rate = 1, Paused = false, Mono = Stopwatch.GetTimestamp() }; CliStopAt = seconds; }
        PlayWavFollowClock(path, ClockNow, seconds);
        WavPath = previous;
        CliStopAt = -1;
        State = previousState;
        Detail = previousDetail;
        TryDelete(path);
        if (Running) Publish();
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
                w.Write((short)(x * 32767f));
            }
        }
    }

    private static void StopPlayback()
    {
        string wav = WavPath;
        string m4a = M4aPath;
        WavPath = null;
        M4aPath = null;
        if (Child != null && !Child.HasExited) try { Child.Kill(); } catch { }
        TryDelete(m4a);
        TryDelete(wav);
    }

    private static void ApplyUserMix(Dictionary<string, object> msg)
    {
        if (!msg.ContainsKey("volume") && !msg.ContainsKey("muted")) return;
        double volume = Num(msg, "volume", 1);
        if (volume < 0) volume = 0;
        if (volume > 1) volume = 1;
        int milli = Bool(msg, "muted") ? 0 : (int)Math.Round(volume * 1000);
        if (milli == OutputMilli) return;
        OutputMilli = milli;
        Log("gain " + (milli / 1000.0).ToString("F2", CultureInfo.InvariantCulture));
    }

    private static void UpdateClock(double time, double rate, bool paused)
    {
        lock (Gate)
        {
            double projected = ClockNow.MediaSeconds();
            bool seek = double.IsNaN(projected) || Math.Abs(projected - time) > 0.2 || ClockNow.Mono == 0;
            bool pauseChanged = ClockNow.Paused != paused;
            bool rateChanged = Math.Abs(ClockNow.Rate - rate) > 0.01;
            if (seek)
            {
                ClockNow = new Clock { Time = time, Rate = rate, Paused = paused, Mono = Stopwatch.GetTimestamp() };
                Log("clock seek t=" + time.ToString("F2", CultureInfo.InvariantCulture) + " paused=" + paused + " rate=" + rate.ToString("F2", CultureInfo.InvariantCulture));
            }
            else
            {
                ClockNow.Rate = rate;
                ClockNow.Paused = paused;
                ClockNow.Time = time;
                ClockNow.Mono = Stopwatch.GetTimestamp();
                if (pauseChanged || rateChanged) Log("clock paused=" + paused + " t=" + time.ToString("F2", CultureInfo.InvariantCulture) + " rate=" + rate.ToString("F2", CultureInfo.InvariantCulture));
            }
        }
    }

    private static void Publish()
    {
        var msg = new Dictionary<string, object> {
            { "type", "status" },
            { "state", State },
            { "detail", Detail },
            { "videoId", VideoId ?? "" },
            { "device", DeviceName ?? "" },
            { "channelsOut", OutputChannels },
            { "sampleRate", SampleRate },
            { "latencyMs", Math.Round(LatencyMs, 1) },
            { "sourceFormat", OutputChannels == 6 ? "773" : "" },
            { "sourceCodec", OutputChannels == 6 ? "iamf.001.001.Opus" : "" },
            { "layout", OutputChannels == 6 ? "5.1" : "" },
            { "mix", MixId >= 0 ? MixId.ToString(CultureInfo.InvariantCulture) : "" },
            { "decoder", OutputChannels == 6 ? "iamf-rs sound system B" : "" }
        };
        if (SampleRate > 0 && !string.IsNullOrEmpty(WavPath) && File.Exists(WavPath))
        {
            long bytes = new FileInfo(WavPath).Length - 44;
            if (bytes > 0) msg["bufferSeconds"] = Math.Round(bytes / (double)(SampleRate * OutputChannels * 2), 1);
        }
        Log("status " + State + " " + Detail);
        if (Stdout == null)
        {
            Console.WriteLine(State + " " + Detail);
            return;
        }
        byte[] body = Encoding.UTF8.GetBytes(Json.Serialize(msg));
        byte[] len = BitConverter.GetBytes(body.Length);
        lock (Gate)
        {
            Stdout.Write(len, 0, 4);
            Stdout.Write(body, 0, body.Length);
            Stdout.Flush();
        }
    }

    private static void PrepareDevice()
    {
        try
        {
            IMMDevice dev = PickDevice();
            DeviceName = ReadName(dev);
            DeviceChannels = MixChannels(dev);
            Marshal.ReleaseComObject(dev);
        }
        catch (Exception ex)
        {
            DeviceName = "unavailable";
            DeviceChannels = 0;
            Log("device " + ex.Message);
        }
    }

    private static string Tool(string file, string env)
    {
        string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, file);
        if (File.Exists(beside)) return beside;
        string fromEnv = Environment.GetEnvironmentVariable(env);
        if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;
        return file.Replace(".exe", "");
    }

    private static int Run(string file, string args, out string output, int timeoutMs)
    {
        Log("run " + file + " " + args);
        var psi = new ProcessStartInfo {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true
        };
        var sb = new StringBuilder();
        using (var p = Process.Start(psi))
        {
            Child = p;
            p.StandardInput.Close();
            p.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutMs))
            {
                Log("timeout " + file);
                try
                {
                    Process.Start(new ProcessStartInfo {
                        FileName = "taskkill.exe",
                        Arguments = "/PID " + p.Id + " /T /F",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    }).WaitForExit(5000);
                }
                catch (Exception ex) { Log("kill " + ex.Message); }
                try { if (!p.HasExited) p.Kill(); } catch { }
                try { p.WaitForExit(3000); } catch { }
                Child = null;
                output = sb.ToString();
                return -2;
            }
            p.WaitForExit();
            Child = null;
            output = sb.ToString();
            Log("exit " + p.ExitCode);
            return p.ExitCode;
        }
    }

    private static int[] MapToDevice(uint mask, int channels)
    {
        uint[] bits = { 0x1, 0x2, 0x4, 0x8, 0x200, 0x400 };
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

    private static IMMDevice PickDevice()
    {
        IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDeviceCollection all = en.EnumAudioEndpoints(0, 1);
        int count = all.GetCount();
        IMMDevice chosen = null;
        for (int i = 0; i < count; i++)
        {
            IMMDevice d = all.Item(i);
            if (chosen == null && MixChannels(d) >= 6) chosen = d;
            else Marshal.ReleaseComObject(d);
        }
        Marshal.ReleaseComObject(all);
        if (chosen == null) chosen = en.GetDefaultAudioEndpoint(0, 1);
        Marshal.ReleaseComObject(en);
        return chosen;
    }

    private static void ClearMeasured()
    {
        OutputChannels = 0;
        MixId = -1;
        SampleRate = 0;
        LatencyMs = 0;
    }

    private static int MixChannels(IMMDevice dev)
    {
        IAudioClient client = Activate(dev);
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

    private static IAudioClient Activate(IMMDevice dev)
    {
        Guid iid = typeof(IAudioClient).GUID;
        IntPtr p;
        Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out p));
        var client = (IAudioClient)Marshal.GetObjectForIUnknown(p);
        Marshal.Release(p);
        return client;
    }

    private static string ReadName(IMMDevice dev)
    {
        IPropertyStore store;
        Marshal.ThrowExceptionForHR(dev.OpenPropertyStore(0, out store));
        try
        {
            var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            PropVariant v;
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out v));
            try { return v.GetString() ?? ""; }
            finally { v.Clear(); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static bool ReadExact(Stream stream, byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n = stream.Read(buf, off, buf.Length - off);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }

    private static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "") + "\""; }
    private static string Tail(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 240 ? s.Substring(s.Length - 240) : s;
    }
    private static void TryDelete(string path) { try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { } }
    private static void Log(string line)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff ") + line + "\r\n"); } catch { }
    }
    private static string Str(Dictionary<string, object> m, string key)
    {
        object v;
        return m != null && m.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : "";
    }
    private static double Num(Dictionary<string, object> m, string key, double fallback)
    {
        object v;
        if (m == null || !m.TryGetValue(key, out v) || v == null) return fallback;
        try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return fallback; }
    }
    private static bool Bool(Dictionary<string, object> m, string key)
    {
        object v;
        if (m == null || !m.TryGetValue(key, out v) || v == null) return false;
        if (v is bool) return (bool)v;
        return string.Equals(Convert.ToString(v), "true", StringComparison.OrdinalIgnoreCase);
    }
    private static int Arg(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return int.Parse(args[i + 1], CultureInfo.InvariantCulture);
        return fallback;
    }
    private static double ArgD(string[] args, string name, double fallback)
    {
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return double.Parse(args[i + 1], CultureInfo.InvariantCulture);
        return fallback;
    }

    private struct Clock
    {
        public double Time;
        public double Rate;
        public bool Paused;
        public long Mono;
        public double MediaSeconds()
        {
            if (Paused || Mono == 0) return Time;
            double elapsed = (Stopwatch.GetTimestamp() - Mono) / (double)Stopwatch.Frequency;
            return Time + elapsed * (Rate <= 0 ? 1 : Rate);
        }
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coinit);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pvar);

    private struct WaveFormat
    {
        public int Channels, SampleRate;
        public uint ChannelMask;
        public bool IsFloat;
        public static WaveFormat Read(IntPtr p)
        {
            var f = new WaveFormat();
            f.Channels = Marshal.ReadInt16(p, 2);
            f.SampleRate = Marshal.ReadInt32(p, 4);
            int tag = Marshal.ReadInt16(p, 0);
            int cb = Marshal.ReadInt16(p, 16);
            if ((tag == -2 || tag == 65534) && cb >= 22) f.ChannelMask = (uint)Marshal.ReadInt32(p, 20);
            else if (f.Channels == 6) f.ChannelMask = 0x3F;
            else if (f.Channels == 2) f.ChannelMask = 0x3;
            Guid sub = Guid.Empty;
            if ((tag == -2 || tag == 65534) && cb >= 22)
            {
                byte[] g = new byte[16];
                Marshal.Copy(IntPtr.Add(p, 24), g, 0, 16);
                sub = new Guid(g);
            }
            f.IsFloat = sub == new Guid("00000003-0000-0010-8000-00aa00389b71") || tag == 3;
            return f;
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumeratorCom { }
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
    private interface IMMDeviceCollection { int GetCount(); IMMDevice Item(int index); }
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
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
        public string GetString() { return vt == 31 && p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null; }
        public void Clear() { PropVariant local = this; PropVariantClear(ref local); this = local; }
    }
}
