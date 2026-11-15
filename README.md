# YouTube Surround

Copyright © 2026 elad0elad. Open source under the [MIT License](LICENSE).

YouTube Surround plays a real IAMF stream from YouTube on Windows. The extension does not upmix a stereo video and does not copy channels to imitate surround. A video without an IAMF stream keeps YouTube's own stereo.

The picture stays in the browser. A small program next to the extension downloads the IAMF audio, decodes it, and plays it. Playback starts after the first seconds. The rest of a long video keeps downloading in the background.

## Output

Choose the output in the extension popup. The choice is saved.

| Choice | What plays |
| --- | --- |
| Auto Detect | The default Windows device is measured. Six channels use direct 5.1. Otherwise an active Windows Spatial Audio stream gets a 5.1 bed. Otherwise the audio is downmixed to stereo. |
| 5.1 Speakers | Six discrete channels: front left, front right, center, LFE, surround left, surround right. |
| Dolby Atmos Headphones | A 5.1 channel bed through Windows Spatial Audio, only when that stream actually opens. This is not object-based Dolby Atmos. |
| Surround Headphones | Direct multichannel audio when the Windows device is already accepting six channels. |
| Stereo Headphones | A downmix that keeps the center, the surrounds, and the LFE. |

The popup shows the route that is actually playing, the device name, and a plain error when the selected mode cannot be opened. Test Audio plays one tone on each channel.

Direct 5.1 needs a shared format that is already six channels. The speaker order is front left, front right, center, subwoofer, surround left, surround right.

## Download

The current Windows installer is [YouTube Surround 1.2.0](https://github.com/elad0elad/youtube-surround/releases/tag/v1.2.0). Run `YouTube.Surround.Setup.exe`. The guide opens when installation finishes.

## Install from source

1. Run `installer\install.cmd`. It copies the program into `%LOCALAPPDATA%\YouTubeSurround` and registers it for Chrome and Brave.
2. Open `brave://extensions` or `chrome://extensions`.
3. Turn on Developer mode.
4. Choose **Load unpacked** and select `%LOCALAPPDATA%\YouTubeSurround\extension`.
5. Reload the YouTube tab.

Deno is included because yt-dlp needs a JavaScript runtime to read YouTube.

## Use

- **Automatic** checks each video. **Manual** waits for **Play Surround**.
- **Stereo** stops the external player and returns YouTube's audio.
- YouTube's mute button and volume slider also control the external player.
- While the external player is covering the current position, YouTube's stereo is silent. Ads, a seek past the downloaded audio, and any failure return that stereo. The video is not stopped.

## Remove

Run `installer\uninstall.cmd`, then remove YouTube Surround from the browser's extensions page.

## Limits

- Not every video has an IAMF stream. YouTube can change or remove it.
- Seeking past the downloaded part plays YouTube's stereo until the external audio catches up.
- The spatial mode sends a 5.1 bed. It does not send Dolby Atmos objects, and it opens only when Windows Spatial Audio accepts the stream.
- One visible YouTube tab owns the clock.
- This is a per-user Windows install, not a Chrome Web Store package.

## Build

1. Install Rust with the `x86_64-pc-windows-gnu` toolchain and a static libopus. Set `OPUS_LIB_DIR` and `OPUS_STATIC=1`.
2. Clone [iamf-rs](https://github.com/baylesj/iamf-rs) at commit `6527afa88918cf82ccc39d7b1399d893199d645c` into `third_party/iamf-rs`. It remains under its own BSD-3-Clause-Clear license.
3. From `decoder/ys-iamf`, run `cargo build --release`.
4. Compile `native/host/Host.cs` with the .NET Framework C# compiler, referencing `System.Web.Extensions.dll`.
5. Place `yt-dlp.exe` and `deno.exe` next to `installer/install.ps1`, then run `installer/install.cmd`.

libopus is BSD licensed. yt-dlp is Unlicense. Deno is MIT. Those pieces are not part of the elad0elad copyright.

## If the sound is wrong

- Reload the YouTube tab after loading the extension.
- For speakers, choose **5.1 Speakers** and run **Test Audio**. A tone from the wrong speaker means the Windows wiring does not match.
- A video with no IAMF source stays on **Stereo source**.
- If the popup says the Windows component is missing, run `installer\install.cmd` again.
- The program writes `%LOCALAPPDATA%\YouTubeSurround\host.log`.

---

# YouTube Surround — עברית

זכויות יוצרים © 2026 elad0elad. קוד פתוח ברישיון MIT.

YouTube Surround משמיע זרם IAMF אמיתי מ־YouTube ב־Windows. אין המרה מסטריאו לסראונד ואין שכפול ערוצים. סרטון בלי IAMF נשאר בסטריאו המקורי של YouTube.

התמונה נשארת בדפדפן. תוכנית קטנה מורידה את האודיו, מפענחת אותו, ומשמיעה אותו. הניגון מתחיל אחרי השניות הראשונות, והמשך של סרטון ארוך יורד בזמן הצפייה.

## פלט

את סוג הפלט בוחרים מחלון התוסף. הבחירה נשמרת.

| בחירה | מה מתנגן |
| --- | --- |
| Auto Detect | נבדק התקן ברירת המחדל. שישה ערוצים יוצאים כ־5.1 ישיר. אם Spatial Audio פעיל, נשלחת מיטת 5.1. אחרת מתנגן דאוןמיקס לסטריאו. |
| 5.1 Speakers | שישה ערוצים נפרדים: קדמי שמאל, קדמי ימין, מרכז, סאב, סראונד שמאל, סראונד ימין. |
| Dolby Atmos Headphones | מיטת 5.1 דרך Spatial Audio של Windows, רק כשהזרם באמת נפתח. זה לא Dolby Atmos מבוסס אובייקטים. |
| Surround Headphones | פלט רב־ערוצי כשההתקן של Windows כבר מקבל שישה ערוצים. |
| Stereo Headphones | דאוןמיקס ששומר על המרכז, הסראונד והסאב. |

בחלון מופיעים המסלול שבאמת פעיל, שם ההתקן, והודעה ברורה אם המצב שנבחר לא נפתח. Test Audio משמיע צליל אחד בכל ערוץ.

5.1 ישיר דורש פורמט משותף של שישה ערוצים. סדר הרמקולים: קדמי שמאל, קדמי ימין, מרכז, סאב, סראונד שמאל, סראונד ימין.

## הורדה

קובץ ההתקנה נמצא בשחרור [YouTube Surround 1.2.0](https://github.com/elad0elad/youtube-surround/releases/tag/v1.2.0). מריצים את `YouTube.Surround.Setup.exe`. בסיום נפתחות ההוראות.

## התקנה מקוד המקור

1. מריצים את `installer\install.cmd`.
2. פותחים את `brave://extensions` או `chrome://extensions`.
3. מפעילים מצב מפתח.
4. בוחרים **טעינה ללא אריזה** ואת `%LOCALAPPDATA%\YouTubeSurround\extension`.
5. מרעננים את הלשונית של YouTube.

## שימוש

- **Automatic** בודק כל סרטון. **Manual** מחכה ל־**Play Surround**.
- **Stereo** עוצר את הנגן החיצוני ומחזיר את השמע של YouTube.
- כפתור ההשתקה והמחוון של YouTube שולטים גם בנגן החיצוני.
- בזמן שהנגן החיצוני מכסה את מיקום הניגון, הסטריאו של YouTube שקט. פרסומת, קפיצה מעבר לחלק שירד, או כשל מחזירים את הסטריאו. הסרטון לא נעצר.

## הסרה

מריצים את `installer\uninstall.cmd`, ואז מסירים את YouTube Surround מדף התוספים.
