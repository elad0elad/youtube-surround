# YouTube Surround

Copyright © 2026 elad0elad. Open source under the [MIT License](LICENSE). The extension developer name is elad0elad.

YouTube Surround plays a real IAMF surround stream from YouTube as six discrete channels on Windows: front left, front right, center, LFE, surround left, surround right. If a video has no IAMF stream, YouTube's normal stereo keeps playing. The extension does not upmix stereo and does not copy channels to imitate 5.1.

The browser alone does not deliver those six channels. A small Windows program next to the extension downloads the IAMF audio, decodes it, and plays it through WASAPI on a device whose shared format is already 5.1.

## Requirements

- Windows 10 or 11.
- Brave or Chrome.
- Windows sound set to a 5.1 speaker configuration. The shared format of the output device must already be six channels. Headphones and stereo endpoints stay stereo. The extension will say surround is unavailable and leave YouTube's audio alone.
- The speakers must be wired front left, front right, center, subwoofer, surround left, surround right.

## Download

The current Windows installer is [YouTube Surround 1.1.0](https://github.com/elad0elad/youtube-surround/releases/tag/v1.1.0). Run `YouTube.Surround.Setup.exe`. The guide opens when installation finishes.

## Install

From a source checkout:

1. Run `installer\install.cmd`. It copies the host, the decoder, yt-dlp, and Deno into `%LOCALAPPDATA%\YouTubeSurround`, and registers the native-messaging host for Chrome and Brave.
2. Open `brave://extensions` or `chrome://extensions`.
3. Turn on Developer mode.
4. Choose **Load unpacked** and select `%LOCALAPPDATA%\YouTubeSurround\extension`.
5. Open YouTube and play a video that actually offers IAMF audio. The first one used here is `https://www.youtube.com/watch?v=iMrnS6finbA`.

No Python install and no manual registry edit are required. Deno is included because current yt-dlp needs a JavaScript runtime to read YouTube.

## Remove

Run `installer\uninstall.cmd`. Then remove **YouTube Surround** from the browser's extensions page.

## How to use it

The popup has two modes.

- **Automatic.** Each video is checked. IAMF audio is played as 5.1. Anything else stays stereo.
- **Manual.** Nothing starts until **Play Surround**. **Stereo** stops the six-channel playback and restores YouTube's audio.

**Test speakers** plays a different tone on each speaker for about eight seconds: front left 220 Hz, front right 277 Hz, center 330 Hz, LFE 55 Hz, surround left 440 Hz, surround right 554 Hz.

Advanced information shows only values the host has measured: video id, codec, format, mix, output channel count, layout, sample rate, buffered audio length, device latency reported by WASAPI, decoder, and device name.

While 5.1 is actually playing, the original YouTube stereo track is muted. Ads and any failure return to that stereo track. A surround failure does not stop the video.

## Limits

- Not every video has an IAMF stream. The Samsung TV client is the path that currently exposes format 773. YouTube can change or remove it.
- Playback starts as soon as the first seconds of 5.1 audio are decoded. The rest of a long video keeps downloading in the background. Seeking past the downloaded part plays YouTube's stereo until the surround audio catches up.
- Two YouTube tabs do not share one clock. The visible tab owns the surround playback.
- This build is installed for the current Windows user. It is not a Chrome Web Store package.

## Build from source

The Windows setup file is only a package of this source. To build it yourself:

1. Install Rust with the `x86_64-pc-windows-gnu` toolchain, a static libopus, and set `OPUS_LIB_DIR` to that library with `OPUS_STATIC=1`.
2. Clone [iamf-rs](https://github.com/baylesj/iamf-rs) at commit `6527afa88918cf82ccc39d7b1399d893199d645c` into `third_party/iamf-rs`. It stays under its own BSD-3-Clause-Clear license.
3. From `decoder/ys-iamf`, run `cargo build --release`. The result is `ys-iamf.exe`.
4. Compile `native/host/Host.cs` with the .NET Framework C# compiler, referencing `System.Web.Extensions.dll`.
5. Place `yt-dlp.exe` (Unlicense) and `deno.exe` (MIT) next to `installer/install.ps1`, then run `installer/install.cmd`.

libopus is BSD licensed. Those third-party pieces are not part of the elad0elad copyright.

## If it does not play in 5.1

- Confirm Windows is using the 5.1 speakers, not headphones or a stereo default device.
- Use **Test speakers**. Each tone must come from one speaker only.
- A video with no IAMF source stays on **Stereo source**. That is the correct result.
- If the badge says the Windows component is missing, run `installer\install.cmd` again and reload the extension.
- The host writes `%LOCALAPPDATA%\YouTubeSurround\host.log`.

---

# YouTube Surround — עברית

זכויות יוצרים © 2026 elad0elad. קוד פתוח ברישיון MIT. שם המפתח של ההרחבה הוא elad0elad.

YouTube Surround משמיע זרם IAMF אמיתי מ־YouTube כשישה ערוצים נפרדים ב־Windows: קדמי שמאל, קדמי ימין, מרכז, סאב, סראונד שמאל וסראונד ימין. אם לסרטון אין זרם IAMF, נשאר הסטריאו הרגיל של YouTube. אין המרה מסטריאו ל־5.1 ואין שכפול ערוצים.

הדפדפן לבדו לא מוציא כאן שישה ערוצים. תוכנית Windows קטנה מורידה את אודיו ה־IAMF, מפענחת אותו, ומשמיעה אותו דרך WASAPI בהתקן שכבר מוגדר ל־5.1.

## הורדה

קובץ ההתקנה ל־Windows נמצא בשחרור [YouTube Surround 1.1.0](https://github.com/elad0elad/youtube-surround/releases/tag/v1.1.0). מריצים את `YouTube.Surround.Setup.exe`. בסיום נפתח דף ההפעלה. הניגון מתחיל אחרי השניות הראשונות, והמשך הקובץ יורד ברקע.

## התקנה

מתוך קוד המקור:

1. מריצים את `installer\install.cmd`. הקבצים עוברים אל `%LOCALAPPDATA%\YouTubeSurround`, והרישום ל־Chrome ול־Brave נכתב אוטומטית.
2. פותחים `brave://extensions` או `chrome://extensions`.
3. מפעילים מצב מפתח.
4. בוחרים **טעינה ללא אריזה** ואת התיקייה `%LOCALAPPDATA%\YouTubeSurround\extension`.
5. פותחים סרטון YouTube שבאמת מציע IAMF. הסרטון שנבדק: `https://www.youtube.com/watch?v=iMrnS6finbA`.

אין צורך להתקין Python ואין צורך לערוך את הרישום ביד. Deno כלול כי yt-dlp צריך מנוע JavaScript כדי לקרוא את YouTube.

## הסרה

מריצים את `installer\uninstall.cmd`, ואז מסירים את **YouTube Surround** מדף התוספים.

## שימוש

- **Automatic:** כל סרטון נבדק. אם יש IAMF, מתנגן 5.1. אחרת נשאר סטריאו.
- **Manual:** כלום לא מתחיל עד **Play Surround**. **Stereo** עוצר את ששת הערוצים ומחזיר את השמע של YouTube.

**Test speakers** משמיע צליל אחר בכל רמקול: 220, 277, 330, 55, 440 ו־554 הרץ.

בזמן ש־5.1 באמת מתנגן, הסטריאו המקורי של YouTube מושתק. פרסומת או כשל מחזירים את הסטריאו. כשל ב־Surround לא עוצר את הסרטון.

## דרישות שמע

Windows צריך להיות מוגדר לרמקולים ב־5.1. אוזניות והתקן סטריאו נשארים סטריאו, והתוסף יציג שאין Surround זמין. סדר החיבור: קדמי שמאל, קדמי ימין, מרכז, סאב, סראונד שמאל, סראונד ימין.

## מה עדיין לא נבדק עד הסוף

לא רצה בדיקת יציבות של שעה. הפרסום בחנות של Chrome דורש חשבון מפתח. סנכרון השפתיים נבדק מול שעון הסרטון, לא בהקלטה של החדר.
