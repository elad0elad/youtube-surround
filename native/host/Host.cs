// YouTube Surround native host.
// Speaks Chrome native messaging on stdin/stdout, downloads an IAMF audio
// stream with yt-dlp's Samsung TV client, decodes it to 6-channel PCM, and
// plays that PCM through the output route selected in the extension:
// direct 5.1, a 5.1 bed on Windows Spatial Audio, or a stereo downmix.
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
    private static Process DownloadProc;
    private static Process DecodeProc;
    private static int LoadGeneration;
    private static int LastPublishTick;
    private static Clock ClockNow = new Clock();
    private static bool PlayTones;
    private static double CliStopAt = -1;
    private static volatile int OutputMilli = 1000;
    private static string OutputPreference = "auto";
    private static string OutputPath = "";
    private static string Rendering = "";
    private static string OutputError = "";
    private static string SpatialEngine = "";
    private static bool SpatialAvailable;
    private static int RenderChannels;
    private static volatile int OutputGeneration;

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
            if (args.Length > 0 && args[0] == "probe") return Probe(args);
            if (args.Length > 0 && args[0] == "downmix-check") return DownmixCheck();
            if (args.Length > 0 && args[0] == "render-check") return RenderCheck(args);
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
        OutputPreference = "speakers51";
        ResolveRoute();
        if (OutputPath != "discrete-5.1")
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
        LoadOutputPreference();
        ResolveRoute();
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
        if (type == "set-output")
        {
            string mode = NormalizeOutput(Str(msg, "output"));
            if (mode != OutputPreference)
            {
                OutputPreference = mode;
                SaveOutputPreference();
                OutputGeneration++;
            }
            ResolveRoute();
            if (State == "active") Detail = PlayingDetail(true);
            Publish();
            return;
        }
        if (type == "tones")
        {
            PlayTones = true;
            State = "active";
            Detail = "Playing test tones through " + OutputPath + ".";
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
            ResolveRoute();
            ClearMeasured();
            State = "checking";
            Detail = "Looking for an IAMF audio stream.";
            Publish();
            int generation = LoadGeneration;
            StreamAudio(url, id, generation);
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

    private static void StreamAudio(string url, string id, int generation)
    {
        string ytdlp = Tool("yt-dlp.exe", "YS_YTDLP");
        string deno = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "deno.exe");
        string runtime = File.Exists(deno) ? " --js-runtimes " + Q("deno:" + deno) : "";
        string decoder = Tool("ys-iamf.exe", "YS_IAMF");
        string m4a = Path.Combine(Path.GetTempPath(), "ys-" + Guid.NewGuid().ToString("N") + ".m4a");
        string wav = Path.ChangeExtension(m4a, ".wav");
        M4aPath = m4a;
        string dlArgs = "--no-playlist --no-warnings --no-progress --no-part --fixup never" + runtime
            + " --extractor-args " + Q("youtube:player_client=tv_samsung")
            + " -f 773 -o " + Q(m4a) + " " + Q(url);
        string decArgs = "--follow " + Q(m4a) + " -o " + Q(wav);
        Log("stream " + url);
        var downloadNotes = new StringBuilder();
        Process download = StartTool(ytdlp, dlArgs);
        download.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) downloadNotes.AppendLine(e.Data); };
        download.BeginErrorReadLine();
        download.BeginOutputReadLine();
        try { download.StandardInput.Close(); } catch { }
        Process decode = StartTool(decoder, decArgs);
        decode.BeginOutputReadLine();
        try { decode.StandardInput.Close(); } catch { }
        DownloadProc = download;
        DecodeProc = decode;
        var waiter = new Thread(() =>
        {
            try { download.WaitForExit(); } catch { }
            try { File.WriteAllText(m4a + ".done", "1"); } catch { }
        });
        waiter.IsBackground = true;
        waiter.Start();
        bool ready = false;
        double lastPublished = 0;
        string line;
        while ((line = decode.StandardError.ReadLine()) != null)
        {
            if (generation != LoadGeneration || VideoId != id) break;
            if (line.StartsWith("decoder:"))
            {
                var mix = Regex.Match(line, @"mix_id=(\d+)");
                var ch = Regex.Match(line, @"channels=(\d+)");
                var rate = Regex.Match(line, @"rate=(\d+)");
                if (mix.Success) MixId = int.Parse(mix.Groups[1].Value);
                if (ch.Success) OutputChannels = int.Parse(ch.Groups[1].Value);
                if (rate.Success) SampleRate = int.Parse(rate.Groups[1].Value);
                State = "decoding";
                Detail = "Decoding IAMF as it downloads.";
                Publish();
            }
            else if (line.StartsWith("ready ") || line.StartsWith("progress "))
            {
                var audio = Regex.Match(line, @"audio=([0-9.]+)");
                double seconds = audio.Success ? double.Parse(audio.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                if (!ready)
                {
                    ready = true;
                    M4aPath = null;
                    WavPath = wav;
                    OutputChannels = 6;
                    State = "active";
                    Detail = PlayingDetail(true);
                    Publish();
                    lastPublished = seconds;
                }
                else if (seconds >= lastPublished + 1.0 && unchecked(Environment.TickCount - LastPublishTick) > 1000)
                {
                    lastPublished = seconds;
                    LastPublishTick = Environment.TickCount;
                    Publish(false);
                }
            }
            else if (line.StartsWith("error:") || line.StartsWith("RESULT=")) Log(line);
        }
        try { if (!decode.HasExited) decode.WaitForExit(5000); } catch { }
        try { if (!download.HasExited) download.WaitForExit(5000); } catch { }
        if (generation != LoadGeneration || VideoId != id) return;
        if (!ready)
        {
            TryDelete(wav);
            string tail = "";
            tail = downloadNotes.ToString();
            StopPlayback();
            ClearMeasured();
            State = "stereo";
            Detail = "This video has no IAMF surround stream. Stereo continues.";
            if (download.HasExited && download.ExitCode == 0) Detail = "The surround stream could not be decoded. Stereo continues.";
            Log("stream missed " + Tail(tail));
            Publish();
            return;
        }
        if (decode.HasExited && decode.ExitCode == 0)
        {
            Detail = PlayingDetail(false);
            Publish(false);
        }
        Log("stream finished ready=" + ready);
    }

    private static Process StartTool(string file, string args)
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
        return Process.Start(psi);
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
        int playFailures = 0;
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
            if (string.IsNullOrEmpty(wav) || !File.Exists(wav) || (State != "active" && State != "unavailable"))
            {
                Thread.Sleep(30);
                continue;
            }
            try
            {
                PlayWavFollowClock(wav, clock, stopAt);
                playFailures = 0;
            }
            catch (Exception ex)
            {
                playFailures++;
                Log("play " + ex.Message);
                if (playFailures < 5 && !string.IsNullOrEmpty(WavPath))
                {
                    State = "unavailable";
                    Detail = "Output was interrupted (" + ex.Message + "). YouTube stereo continues until audio returns.";
                    Publish();
                    Thread.Sleep(500);
                    ResolveRoute();
                    continue;
                }
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
        if (OutputPath == "spatial-bed")
        {
            try
            {
                PlaySpatial(path, stopAt);
                return;
            }
            catch (Exception ex)
            {
                if (IsTransient(ex)) throw;
                Log("spatial fallback " + ex.Message);
                lock (Gate)
                {
                    OutputPath = "stereo-downmix";
                    RenderChannels = 2;
                    OutputError = "Spatial Audio did not start (" + ex.Message + "). Stereo downmix is playing instead.";
                }
                Publish();
            }
        }
        FileStream fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, (FileShare)7);
            var header = new byte[44];
            if (fs.Read(header, 0, 44) != 44) throw new InvalidDataException("short wav");
            int channels = BitConverter.ToInt16(header, 22);
            int rate = BitConverter.ToInt32(header, 24);
            if (channels != 6) throw new InvalidOperationException("wav has " + channels + " channels");
            SampleRate = rate;
            int generation = OutputGeneration;
            IMMDevice dev = PickDevice(OutputPreference == "speakers51" && OutputPath == "discrete-5.1");
            string openedId = ReadId(dev);
            int lastDeviceTick = Environment.TickCount;
            IAudioClient client = Activate(dev);
            IntPtr mix = IntPtr.Zero;
            IAudioRenderClient render = null;
            try
            {
                Marshal.ThrowExceptionForHR(client.GetMixFormat(out mix));
                WaveFormat fmt = WaveFormat.Read(mix);
                DeviceName = ReadName(dev);
                DeviceChannels = fmt.Channels;
                if (fmt.Channels < 6 && OutputPath != "stereo-downmix")
                {
                    lock (Gate)
                    {
                        OutputPath = "stereo-downmix";
                        RenderChannels = 2;
                        OutputError = "This device mix is " + fmt.Channels + " channels, so direct 5.1 was not opened. Stereo downmix is playing instead.";
                    }
                }
                Rendering = OutputPath == "stereo-downmix" ? "stereo-downmix" : "discrete-5.1";
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
                if (State == "unavailable")
                {
                    State = "active";
                    Detail = PlayingDetail(true);
                }
                Publish();
                var frame = new byte[channels * 2];
                while (Running && WavPath == path && !PlayTones && OutputGeneration == generation)
                {
                    if (unchecked(Environment.TickCount - lastDeviceTick) > 1000)
                    {
                        lastDeviceTick = Environment.TickCount;
                        if (!SameEndpoint(openedId)) throw new InvalidOperationException("device changed");
                    }
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
                                if (fs == null) fs = new FileStream(path, FileMode.Open, FileAccess.Read, (FileShare)7);
                                int got = 0;
                                try
                                {
                                    fs.Position = 44L + idx * frame.Length;
                                    got = fs.Read(block, 0, block.Length);
                                }
                                catch (IOException) { got = 0; }
                                catch (ArgumentException) { got = 0; }
                                int framesGot = got / frame.Length;
                                if (fs != null && got < block.Length)
                                {
                                    fs.Dispose();
                                    fs = null;
                                    if (framesGot == 0) Thread.Sleep(30);
                                }
                                for (int i = 0; i < framesGot; i++)
                                    WriteBed(dst, i, fmt, map, block, i * frame.Length);
                            }
                        }
                        else
                        {
                            for (int i = 0; i < want; i++)
                            {
                                long idx = (long)(srcOrigin + i * step);
                                if (idx < 0) continue;
                                if (fs == null) fs = new FileStream(path, FileMode.Open, FileAccess.Read, (FileShare)7);
                                fs.Position = 44L + idx * frame.Length;
                                if (fs.Read(frame, 0, frame.Length) != frame.Length)
                                {
                                    fs.Dispose();
                                    fs = null;
                                    break;
                                }
                                WriteBed(dst, i, fmt, map, frame, 0);
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
                if (fs != null) fs.Dispose();
                if (render != null) Marshal.ReleaseComObject(render);
                if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
                Marshal.ReleaseComObject(client);
                Marshal.ReleaseComObject(dev);
            }
        }
        finally
        {
            if (fs != null) fs.Dispose();
        }
    }

    private static void PlayGeneratedTones(int seconds)
    {
        ResolveRoute();
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
        Detail = "Playing test tones through " + OutputPath + ".";
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
        LoadGeneration++;
        string wav = WavPath;
        string m4a = M4aPath;
        WavPath = null;
        M4aPath = null;
        Rendering = "";
        KillTree(Child);
        KillTree(DownloadProc);
        KillTree(DecodeProc);
        Child = null;
        DownloadProc = null;
        DecodeProc = null;
        TryDelete(m4a);
        if (!string.IsNullOrEmpty(m4a)) TryDelete(m4a + ".done");
        TryDelete(wav);
    }

    private static void KillTree(Process process)
    {
        if (process == null) return;
        try
        {
            if (process.HasExited) return;
            Process.Start(new ProcessStartInfo {
                FileName = "taskkill.exe",
                Arguments = "/PID " + process.Id + " /T /F",
                CreateNoWindow = true,
                UseShellExecute = false
            }).WaitForExit(5000);
        }
        catch (Exception ex) { Log("kill " + ex.Message); }
        try { if (!process.HasExited) process.Kill(); } catch { }
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
        Publish(true);
    }

    private static void Publish(bool writeLog)
    {
        var msg = new Dictionary<string, object> {
            { "type", "status" },
            { "state", State },
            { "detail", Detail },
            { "videoId", VideoId ?? "" },
            { "device", DeviceName ?? "" },
            { "deviceChannels", DeviceChannels },
            { "channelsOut", OutputChannels },
            { "renderChannels", RenderChannels },
            { "sampleRate", SampleRate },
            { "latencyMs", Math.Round(LatencyMs, 1) },
            { "sourceFormat", OutputChannels == 6 ? "773" : "" },
            { "sourceCodec", OutputChannels == 6 ? "iamf.001.001.Opus" : "" },
            { "layout", LayoutName() },
            { "outputMode", OutputPreference },
            { "outputPath", OutputPath ?? "" },
            { "rendering", Rendering ?? "" },
            { "spatialAvailable", SpatialAvailable },
            { "spatialEngine", SpatialEngine ?? "" },
            { "outputError", OutputError ?? "" },
            { "mix", MixId >= 0 ? MixId.ToString(CultureInfo.InvariantCulture) : "" },
            { "decoder", OutputChannels == 6 ? "iamf-rs sound system B" : "" }
        };
        if (SampleRate > 0 && !string.IsNullOrEmpty(WavPath) && File.Exists(WavPath))
        {
            long bytes = new FileInfo(WavPath).Length - 44;
            if (bytes > 0) msg["bufferSeconds"] = Math.Round(bytes / (double)(SampleRate * OutputChannels * 2), 1);
        }
        if (writeLog) Log("status " + State + " " + Detail);
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

    private static void ResolveRoute()
    {
        IMMDevice dev = null;
        try
        {
            bool preferSix = OutputPreference == "speakers51";
            dev = PickDevice(preferSix);
            DeviceName = ReadName(dev);
            WaveFormat fmt = ReadMix(dev);
            DeviceChannels = fmt.Channels;
            SpatialAvailable = QuerySpatial(dev);
            SpatialEngine = SpatialAvailable ? ReadSpatialEngine(dev) : "";
            bool six = fmt.Channels >= 6;
            OutputError = "";
            if (OutputPreference == "stereo")
            {
                OutputPath = "stereo-downmix";
                RenderChannels = 2;
            }
            else if (OutputPreference == "speakers51" || OutputPreference == "surroundHeadphones")
            {
                if (six)
                {
                    OutputPath = "discrete-5.1";
                    RenderChannels = 6;
                }
                else
                {
                    OutputPath = "stereo-downmix";
                    RenderChannels = 2;
                    OutputError = DeviceName + " is presenting " + fmt.Channels + " channels, so direct 5.1 was not opened. Stereo downmix is playing instead.";
                }
            }
            else if (OutputPreference == "atmos")
            {
                if (SpatialAvailable)
                {
                    OutputPath = "spatial-bed";
                    RenderChannels = 6;
                }
                else
                {
                    OutputPath = "stereo-downmix";
                    RenderChannels = 2;
                    OutputError = "Windows Spatial Audio is not active on " + DeviceName + ". Stereo downmix is playing instead.";
                }
            }
            else if (six)
            {
                OutputPath = "discrete-5.1";
                RenderChannels = 6;
            }
            else if (SpatialAvailable)
            {
                OutputPath = "spatial-bed";
                RenderChannels = 6;
            }
            else
            {
                OutputPath = "stereo-downmix";
                RenderChannels = 2;
            }
            Log("route " + OutputPreference + " -> " + OutputPath + " device=" + DeviceName + " ch=" + DeviceChannels + " spatial=" + SpatialAvailable);
        }
        catch (Exception ex)
        {
            DeviceName = string.IsNullOrEmpty(DeviceName) ? "unavailable" : DeviceName;
            DeviceChannels = 0;
            OutputPath = "stereo-downmix";
            RenderChannels = 2;
            SpatialAvailable = false;
            OutputError = "The output device could not be opened (" + ex.Message + ").";
            Log("route " + ex.Message);
        }
        finally
        {
            if (dev != null) Marshal.ReleaseComObject(dev);
        }
    }

    private static string PlayingDetail(bool downloading)
    {
        string tail = downloading ? " The rest of the audio is still downloading." : " YouTube's stereo track is muted.";
        string err = string.IsNullOrEmpty(OutputError) ? "" : " " + OutputError;
        if (OutputPath == "discrete-5.1") return "Direct 5.1 is playing on " + DeviceName + "." + tail + err;
        if (OutputPath == "spatial-bed")
        {
            string engine = string.IsNullOrEmpty(SpatialEngine) ? "Windows Spatial Audio" : SpatialEngine;
            return "A 5.1 channel bed is playing through " + engine + " on " + DeviceName + ". This is not object-based Dolby Atmos." + tail + err;
        }
        return "Stereo downmix is playing on " + DeviceName + "." + tail + err;
    }

    private static string LayoutName()
    {
        if (OutputChannels != 6) return "";
        if (OutputPath == "discrete-5.1") return "5.1";
        if (OutputPath == "spatial-bed") return "spatial-bed";
        if (OutputPath == "stereo-downmix") return "stereo-downmix";
        return "";
    }

    private static string NormalizeOutput(string value)
    {
        if (value == "speakers51" || value == "atmos" || value == "surroundHeadphones" || value == "stereo") return value;
        return "auto";
    }

    private static string OutputPreferenceFile()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YouTubeSurround", "output-mode.txt");
    }

    private static void LoadOutputPreference()
    {
        try
        {
            string path = OutputPreferenceFile();
            if (File.Exists(path)) OutputPreference = NormalizeOutput(File.ReadAllText(path).Trim());
        }
        catch (Exception ex) { Log("output pref " + ex.Message); }
    }

    private static void SaveOutputPreference()
    {
        try { File.WriteAllText(OutputPreferenceFile(), OutputPreference); }
        catch (Exception ex) { Log("output pref save " + ex.Message); }
    }

    private static void Downmix(float fl, float fr, float fc, float lfe, float sl, float sr, out float left, out float right)
    {
        const float center = 0.7071f;
        const float surround = 0.7071f;
        const float low = 0.5f;
        const float scale = 1f / (1f + 0.7071f + 0.7071f + 0.5f);
        left = (fl + center * fc + surround * sl + low * lfe) * scale;
        right = (fr + center * fc + surround * sr + low * lfe) * scale;
    }

    private static int DownmixCheck()
    {
        float l, r;
        Downmix(0, 0, 1, 0, 0, 0, out l, out r);
        if (Math.Abs(l - r) > 0.0001f || l <= 0) return DownmixFail("center");
        Downmix(1, 0, 0, 0, 0, 0, out l, out r);
        if (l <= 0.2f || Math.Abs(r) > 0.0001f) return DownmixFail("front-left");
        Downmix(0, 1, 0, 0, 0, 0, out l, out r);
        if (r <= 0.2f || Math.Abs(l) > 0.0001f) return DownmixFail("front-right");
        Downmix(0, 0, 0, 1, 0, 0, out l, out r);
        if (Math.Abs(l - r) > 0.0001f || l <= 0) return DownmixFail("lfe");
        Downmix(0, 0, 0, 0, 1, 0, out l, out r);
        if (l <= 0 || Math.Abs(r) > 0.0001f) return DownmixFail("surround-left");
        Downmix(0, 0, 0, 0, 0, 1, out l, out r);
        if (r <= 0 || Math.Abs(l) > 0.0001f) return DownmixFail("surround-right");
        Downmix(1, 1, 1, 1, 1, 1, out l, out r);
        if (l > 1.0001f || r > 1.0001f || l < 0.99f || Math.Abs(l - r) > 0.0001f) return DownmixFail("full-scale");
        Console.WriteLine("RESULT=DOWNMIX_OK");
        return 0;
    }

    private static int DownmixFail(string name)
    {
        Console.WriteLine("RESULT=DOWNMIX_FAIL " + name);
        return 1;
    }

    private static int Probe(string[] args)
    {
        OutputPreference = args.Length > 1 ? NormalizeOutput(args[1]) : "auto";
        ResolveRoute();
        Console.WriteLine("preference=" + OutputPreference);
        Console.WriteLine("path=" + OutputPath);
        Console.WriteLine("device=" + DeviceName);
        Console.WriteLine("deviceChannels=" + DeviceChannels);
        Console.WriteLine("renderChannels=" + RenderChannels);
        Console.WriteLine("spatialAvailable=" + (SpatialAvailable ? "true" : "false"));
        Console.WriteLine("spatialEngine=" + SpatialEngine);
        Console.WriteLine("outputError=" + OutputError);
        Console.WriteLine("RESULT=PROBE_OK");
        return 0;
    }

    private static int RenderCheck(string[] args)
    {
        OutputPreference = args.Length > 1 ? NormalizeOutput(args[1]) : "auto";
        string requested = OutputPreference;
        ResolveRoute();
        string planned = OutputPath;
        int rate = 48000;
        int frames = rate / 5;
        float[] pcm = new float[frames * 6];
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / rate;
            pcm[i * 6] = (float)(0.04 * Math.Sin(2 * Math.PI * 440 * t));
            pcm[i * 6 + 2] = (float)(0.04 * Math.Sin(2 * Math.PI * 330 * t));
            pcm[i * 6 + 3] = (float)(0.04 * Math.Sin(2 * Math.PI * 55 * t));
        }
        string path = Path.Combine(Path.GetTempPath(), "ys-render-check.wav");
        WriteWav(path, pcm, 6, rate);
        WavPath = path;
        State = "active";
        lock (Gate) { ClockNow = new Clock { Time = 0, Rate = 1, Paused = false, Mono = Stopwatch.GetTimestamp() }; }
        try { PlayWavFollowClock(path, ClockNow, 0.2); }
        finally
        {
            WavPath = null;
            TryDelete(path);
        }
        Console.WriteLine("preference=" + requested);
        Console.WriteLine("planned=" + planned);
        Console.WriteLine("path=" + OutputPath);
        Console.WriteLine("rendering=" + Rendering);
        Console.WriteLine("device=" + DeviceName);
        Console.WriteLine("renderChannels=" + RenderChannels);
        Console.WriteLine("outputError=" + OutputError);
        string result = "RENDER_FAILED";
        if (Rendering == "discrete-5.1") result = "DISCRETE_RENDERED";
        else if (Rendering == "spatial-bed") result = "SPATIAL_RENDERED";
        else if (Rendering == "stereo-downmix") result = "DOWNMIX_RENDERED";
        Console.WriteLine("RESULT=" + result);
        return Rendering == planned && planned.Length > 0 ? 0 : 2;
    }

    private static void WriteBed(float[] dst, int frameIndex, WaveFormat fmt, int[] map, byte[] src, int offset)
    {
        float gain = OutputMilli / 1000f;
        if (OutputPath == "stereo-downmix")
        {
            float left, right;
            Downmix(
                BitConverter.ToInt16(src, offset) / 32768f * gain,
                BitConverter.ToInt16(src, offset + 2) / 32768f * gain,
                BitConverter.ToInt16(src, offset + 4) / 32768f * gain,
                BitConverter.ToInt16(src, offset + 6) / 32768f * gain,
                BitConverter.ToInt16(src, offset + 8) / 32768f * gain,
                BitConverter.ToInt16(src, offset + 10) / 32768f * gain,
                out left, out right);
            int il = ChannelIndex(fmt.ChannelMask, 0x1, fmt.Channels);
            int ir = ChannelIndex(fmt.ChannelMask, 0x2, fmt.Channels);
            if (il >= 0) dst[frameIndex * fmt.Channels + il] = left;
            if (ir >= 0) dst[frameIndex * fmt.Channels + ir] = right;
            return;
        }
        for (int c = 0; c < 6; c++)
        {
            if (map[c] < 0 || map[c] >= fmt.Channels) continue;
            short sample = BitConverter.ToInt16(src, offset + c * 2);
            dst[frameIndex * fmt.Channels + map[c]] = sample / 32768f * gain;
        }
    }

    private static bool IsTransient(Exception ex)
    {
        string message = ex == null ? "" : ex.Message;
        return message.IndexOf("device changed", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool SameEndpoint(string openedId)
    {
        IMMDevice dev = null;
        try
        {
            dev = PickDevice(OutputPreference == "speakers51");
            return string.Equals(ReadId(dev), openedId ?? "", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
        finally { if (dev != null) Marshal.ReleaseComObject(dev); }
    }

    private static string ReadId(IMMDevice dev)
    {
        string id;
        if (dev.GetId(out id) != 0 || id == null) return "";
        return id;
    }

    private static bool QuerySpatial(IMMDevice dev)
    {
        ISpatialAudioClient client = null;
        try
        {
            client = ActivateSpatial(dev);
            Guid streamId = new Guid("BAB5F473-B423-477B-85F5-B5A332A04153");
            int hr = client.IsSpatialAudioStreamAvailable(ref streamId, IntPtr.Zero);
            Log("spatial available hr=0x" + hr.ToString("X8"));
            return hr == 0;
        }
        catch (Exception ex)
        {
            Log("spatial probe " + ex.Message);
            return false;
        }
        finally { if (client != null) Marshal.ReleaseComObject(client); }
    }

    private static string ReadSpatialEngine(IMMDevice dev)
    {
        IPropertyStore store;
        if (dev.OpenPropertyStore(0, out store) != 0) return "";
        try
        {
            int count;
            if (store.GetCount(out count) != 0) return "";
            string best = "";
            int bestScore = 0;
            if (count > 80) count = 80;
            for (int i = 0; i < count; i++)
            {
                PropertyKey key;
                if (store.GetAt(i, out key) != 0) continue;
                if (key.fmtid == new Guid("a45c254e-df1c-4efd-8020-67d146a850e0") && key.pid == 14) continue;
                PropVariant value;
                if (store.GetValue(ref key, out value) != 0) continue;
                try
                {
                    string text = null;
                    if (value.vt == 31 && value.p != IntPtr.Zero) text = Marshal.PtrToStringUni(value.p);
                    else if (value.vt == 8 && value.p != IntPtr.Zero) text = Marshal.PtrToStringBSTR(value.p);
                    if (string.IsNullOrEmpty(text) || text.Length > 160) continue;
                    int score = SpatialScore(text);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = text;
                    }
                }
                finally { value.Clear(); }
            }
            return best;
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static int SpatialScore(string text)
    {
        string lower = text.ToLowerInvariant();
        if (lower.IndexOf("dolby") >= 0) return 4;
        if (lower.IndexOf("dts") >= 0) return 3;
        if (lower.IndexOf("sonic") >= 0) return 2;
        if (lower.IndexOf("spatial") >= 0) return 1;
        return 0;
    }

    private static uint[] ChooseBed(uint native)
    {
        uint frontLeft = 1u << 1;
        uint frontRight = 1u << 2;
        uint frontCenter = 1u << 3;
        uint low = 1u << 4;
        uint sideLeft = 1u << 5;
        uint sideRight = 1u << 6;
        uint backLeft = 1u << 7;
        uint backRight = 1u << 8;
        if ((native & frontLeft) == 0 || (native & frontRight) == 0) return null;
        uint left = (native & sideLeft) != 0 ? sideLeft : ((native & backLeft) != 0 ? backLeft : 0);
        uint right = (native & sideRight) != 0 ? sideRight : ((native & backRight) != 0 ? backRight : 0);
        uint center = (native & frontCenter) != 0 ? frontCenter : 0;
        uint lfe = (native & low) != 0 ? low : 0;
        if (left == 0 || right == 0 || center == 0 || lfe == 0) return null;
        return new uint[] { frontLeft, frontRight, center, lfe, left, right };
    }

    private static void PlaySpatial(string path, double stopAt)
    {
        int generation = OutputGeneration;
        FileStream fs = null;
        IMMDevice dev = null;
        ISpatialAudioClient client = null;
        ISpatialAudioObjectRenderStream stream = null;
        ISpatialAudioObject[] objects = new ISpatialAudioObject[6];
        IntPtr fmtPtr = IntPtr.Zero;
        IntPtr actPtr = IntPtr.Zero;
        IntPtr evt = IntPtr.Zero;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, (FileShare)7);
            var header = new byte[44];
            if (fs.Read(header, 0, 44) != 44) throw new InvalidDataException("short wav");
            int channels = BitConverter.ToInt16(header, 22);
            int rate = BitConverter.ToInt32(header, 24);
            if (channels != 6) throw new InvalidOperationException("wav has " + channels + " channels");
            if (rate != 48000) throw new InvalidOperationException("spatial path expects 48000 Hz");
            SampleRate = rate;
            dev = PickDevice(false);
            DeviceName = ReadName(dev);
            string openedId = ReadId(dev);
            client = ActivateSpatial(dev);
            Guid streamId = new Guid("BAB5F473-B423-477B-85F5-B5A332A04153");
            int available = client.IsSpatialAudioStreamAvailable(ref streamId, IntPtr.Zero);
            if (available != 0) throw new InvalidOperationException("spatial stream is not available (0x" + available.ToString("X8") + ")");
            uint native;
            Marshal.ThrowExceptionForHR(client.GetNativeStaticObjectTypeMask(out native));
            uint[] types = ChooseBed(native);
            if (types == null) throw new InvalidOperationException("the spatial engine has no 5.1 static bed");
            var wave = new PcmWave();
            wave.Tag = 3;
            wave.Channels = 1;
            wave.Rate = 48000;
            wave.Avg = 192000;
            wave.Align = 4;
            wave.Bits = 32;
            fmtPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(PcmWave)));
            Marshal.StructureToPtr(wave, fmtPtr, false);
            int supported = client.IsAudioObjectFormatSupported(fmtPtr);
            if (supported != 0) throw new InvalidOperationException("spatial engine rejected 48 kHz float objects (0x" + supported.ToString("X8") + ")");
            evt = CreateEvent(IntPtr.Zero, false, false, null);
            if (evt == IntPtr.Zero) throw new InvalidOperationException("could not create a spatial audio event");
            var act = new SpatialActivation();
            act.ObjectFormat = fmtPtr;
            act.StaticMask = types[0] | types[1] | types[2] | types[3] | types[4] | types[5];
            act.Category = 11;
            act.EventHandle = evt;
            int actSize = Marshal.SizeOf(typeof(SpatialActivation));
            actPtr = Marshal.AllocCoTaskMem(actSize);
            Marshal.StructureToPtr(act, actPtr, false);
            var blob = new PropVariantBlob();
            blob.vt = 65;
            blob.size = actSize;
            blob.data = actPtr;
            IntPtr streamPtr;
            int hr = client.ActivateSpatialAudioStream(ref blob, ref streamId, out streamPtr);
            if (hr != 0 || streamPtr == IntPtr.Zero) throw new InvalidOperationException("ActivateSpatialAudioStream failed (0x" + hr.ToString("X8") + ")");
            stream = (ISpatialAudioObjectRenderStream)Marshal.GetObjectForIUnknown(streamPtr);
            Marshal.Release(streamPtr);
            for (int c = 0; c < 6; c++)
            {
                IntPtr obj;
                hr = stream.ActivateSpatialAudioObject(types[c], out obj);
                if (hr != 0 || obj == IntPtr.Zero) throw new InvalidOperationException("static bed object failed (0x" + hr.ToString("X8") + ")");
                objects[c] = (ISpatialAudioObject)Marshal.GetObjectForIUnknown(obj);
                Marshal.Release(obj);
                objects[c].SetVolume(1f);
            }
            Marshal.ThrowExceptionForHR(stream.Start());
            Rendering = "spatial-bed";
            RenderChannels = 6;
            if (State == "unavailable")
            {
                State = "active";
                Detail = PlayingDetail(true);
            }
            Publish();
            int lastDeviceTick = Environment.TickCount;
            while (Running && WavPath == path && !PlayTones && OutputGeneration == generation)
            {
                uint waited = WaitForSingleObject(evt, 50);
                if (unchecked(Environment.TickCount - lastDeviceTick) > 1000)
                {
                    lastDeviceTick = Environment.TickCount;
                    if (!SameEndpoint(openedId)) throw new InvalidOperationException("device changed");
                }
                Clock clock;
                lock (Gate) clock = ClockNow;
                if (stopAt > 0 && clock.MediaSeconds() >= stopAt) break;
                if (waited != 0) continue;
                uint dynamicCount, frameCount;
                hr = stream.BeginUpdatingAudioObjects(out dynamicCount, out frameCount);
                if (hr == unchecked((int)0x88890101)) continue;
                if (hr == unchecked((int)0x88890100) || hr == unchecked((int)0x88890102)) throw new InvalidOperationException("device changed");
                if (hr != 0) throw new InvalidOperationException("spatial update failed (0x" + hr.ToString("X8") + ")");
                try
                {
                    int count = (int)frameCount;
                    if (count < 0) count = 0;
                    if (count > 48000) count = 48000;
                    byte[] block = new byte[count * 12];
                    int gotFrames = 0;
                    if (!clock.Paused && count > 0)
                    {
                        double step = clock.Rate <= 0 ? 1 : clock.Rate;
                        double origin = (clock.MediaSeconds() + 0.02) * rate;
                        if (fs == null) fs = OpenGrowing(path);
                        if (fs != null && Math.Abs(step - 1.0) < 0.001)
                        {
                            long idx = (long)origin;
                            if (idx >= 0)
                            {
                                int got = 0;
                                try
                                {
                                    fs.Position = 44L + idx * 12;
                                    got = fs.Read(block, 0, block.Length);
                                }
                                catch (IOException) { got = 0; }
                                catch (ArgumentException) { got = 0; }
                                gotFrames = got / 12;
                                if (got < block.Length)
                                {
                                    fs.Dispose();
                                    fs = null;
                                    if (gotFrames == 0) Thread.Sleep(20);
                                }
                            }
                        }
                        else if (fs != null)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                long idx = (long)(origin + i * step);
                                if (idx < 0) continue;
                                try
                                {
                                    fs.Position = 44L + idx * 12;
                                    if (fs.Read(block, i * 12, 12) != 12) { fs.Dispose(); fs = null; break; }
                                    gotFrames = i + 1;
                                }
                                catch (IOException) { break; }
                                catch (ArgumentException) { break; }
                            }
                        }
                    }
                    float gain = OutputMilli / 1000f;
                    for (int c = 0; c < 6; c++)
                    {
                        if (objects[c] == null) continue;
                        IntPtr buffer;
                        uint length;
                        if (objects[c].GetBuffer(out buffer, out length) != 0 || buffer == IntPtr.Zero) continue;
                        int samples = (int)(length / 4);
                        if (samples > count) samples = count;
                        if (samples <= 0) continue;
                        float[] mono = new float[samples];
                        for (int i = 0; i < samples && i < gotFrames; i++)
                        {
                            short sample = BitConverter.ToInt16(block, i * 12 + c * 2);
                            mono[i] = sample / 32768f * gain;
                        }
                        Marshal.Copy(mono, 0, buffer, samples);
                    }
                }
                finally { stream.EndUpdatingAudioObjects(); }
            }
            try { stream.Stop(); } catch { }
        }
        finally
        {
            if (fs != null) fs.Dispose();
            for (int i = 0; i < objects.Length; i++) if (objects[i] != null) Marshal.ReleaseComObject(objects[i]);
            if (stream != null) Marshal.ReleaseComObject(stream);
            if (client != null) Marshal.ReleaseComObject(client);
            if (dev != null) Marshal.ReleaseComObject(dev);
            if (fmtPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(fmtPtr);
            if (actPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(actPtr);
            if (evt != IntPtr.Zero) CloseHandle(evt);
        }
    }

    private static FileStream OpenGrowing(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, (FileShare)7); }
        catch { return null; }
    }

    private static ISpatialAudioClient ActivateSpatial(IMMDevice dev)
    {
        Guid iid = typeof(ISpatialAudioClient).GUID;
        IntPtr p;
        int hr = dev.Activate(ref iid, 23, IntPtr.Zero, out p);
        if (hr != 0 || p == IntPtr.Zero) throw new InvalidOperationException("Spatial Audio is not available on this device (0x" + hr.ToString("X8") + ")");
        var client = (ISpatialAudioClient)Marshal.GetObjectForIUnknown(p);
        Marshal.Release(p);
        return client;
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

    private static IMMDevice PickDevice(bool preferSix)
    {
        IMMDeviceEnumerator en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice chosen = null;
        if (preferSix)
        {
            IMMDeviceCollection all = en.EnumAudioEndpoints(0, 1);
            int count = all.GetCount();
            for (int i = 0; i < count; i++)
            {
                IMMDevice d = all.Item(i);
                if (chosen == null && MixChannels(d) >= 6) chosen = d;
                else Marshal.ReleaseComObject(d);
            }
            Marshal.ReleaseComObject(all);
        }
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
        return ReadMix(dev).Channels;
    }

    private static WaveFormat ReadMix(IMMDevice dev)
    {
        IAudioClient client = Activate(dev);
        IntPtr mix = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out mix));
            return WaveFormat.Read(mix);
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
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr ptr);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateEvent(IntPtr attrs, bool manual, bool initial, string name);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

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
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PcmWave
    {
        public ushort Tag;
        public ushort Channels;
        public uint Rate;
        public uint Avg;
        public ushort Align;
        public ushort Bits;
        public ushort Extra;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SpatialActivation
    {
        public IntPtr ObjectFormat;
        public uint StaticMask;
        public uint MinDynamic;
        public uint MaxDynamic;
        public int Category;
        public IntPtr EventHandle;
        public IntPtr NotifyObject;
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariantBlob
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public int size;
        [FieldOffset(16)] public IntPtr data;
    }
    [ComImport, Guid("BBF8E066-AAAA-49BE-9A4D-FD2A858EA27F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpatialAudioClient
    {
        [PreserveSig] int GetStaticObjectPosition(uint type, out float x, out float y, out float z);
        [PreserveSig] int GetNativeStaticObjectTypeMask(out uint mask);
        [PreserveSig] int GetMaxDynamicObjectCount(out uint value);
        [PreserveSig] int GetSupportedAudioObjectFormatEnumerator(out IntPtr enumerator);
        [PreserveSig] int GetMaxFrameCount(IntPtr objectFormat, out uint frameCountPerBuffer);
        [PreserveSig] int IsAudioObjectFormatSupported(IntPtr objectFormat);
        [PreserveSig] int IsSpatialAudioStreamAvailable(ref Guid streamUuid, IntPtr auxiliaryInfo);
        [PreserveSig] int ActivateSpatialAudioStream(ref PropVariantBlob activationParams, ref Guid riid, out IntPtr stream);
    }
    [ComImport, Guid("BAB5F473-B423-477B-85F5-B5A332A04153"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpatialAudioObjectRenderStream
    {
        [PreserveSig] int GetAvailableDynamicObjectCount(out uint value);
        [PreserveSig] int GetService(ref Guid riid, out IntPtr service);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int BeginUpdatingAudioObjects(out uint availableDynamicObjectCount, out uint frameCountPerBuffer);
        [PreserveSig] int EndUpdatingAudioObjects();
        [PreserveSig] int ActivateSpatialAudioObject(uint type, out IntPtr audioObject);
    }
    [ComImport, Guid("DDE28967-521B-46E5-8F00-BD6F2BC8AB1D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpatialAudioObject
    {
        [PreserveSig] int GetBuffer(out IntPtr buffer, out uint bufferLength);
        [PreserveSig] int SetEndOfStream(uint frameCount);
        [PreserveSig] int IsActive(out int isActive);
        [PreserveSig] int GetAudioObjectType(out uint audioObjectType);
        [PreserveSig] int SetPosition(float x, float y, float z);
        [PreserveSig] int SetVolume(float volume);
    }
}
