using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.IO;
using Accord.Video.FFMPEG;
using NAudio.Wave;

namespace ASCIIVideoPlayer
{
    class Program
    {
        private const int STD_OUTPUT_HANDLE = -11;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WriteConsoleOutput(IntPtr hConsoleOutput, [In] CHAR_INFO[] lpBuffer,
            COORD dwBufferSize, COORD dwBufferCoord, ref SMALL_RECT lpWriteRegion);

        [StructLayout(LayoutKind.Sequential)]
        struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Explicit)]
        struct CHAR_INFO
        {
            [FieldOffset(0)] public char UnicodeChar;
            [FieldOffset(2)] public short Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SMALL_RECT { public short Left; public short Top; public short Right; public short Bottom; }

        private static IWavePlayer waveOut;
        private static AudioFileReader audioReader;
        private static string tempAudioPath;
        private static readonly object audioLock = new object();
        private static readonly char[] asciiChars = { ' ', '.', ':', '-', '=', '+', '*', '#', '%', 'S', '@' };
        private const double AspectRatio = 2.2;
        private const int MinWidth = 80;

        private static int colorMode = 1;
        private static bool isVideo = true;

        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            MaximizeConsole();

            while (true)
            {
                Console.Clear();
                Console.WriteLine("ASCII Video/Photo Player (C# 4.8 .NET Framework)\n=======================================================");

                Console.WriteLine("\nВыберите режим:");
                Console.WriteLine("1 - Видео (цветное)");
                Console.WriteLine("2 - Видео (серое)");
                Console.WriteLine("3 - Видео (цветное улучшенное)");
                Console.WriteLine("4 - Фото (цветное)");
                Console.WriteLine("5 - Фото (серое)");
                Console.WriteLine("6 - Фото (цветное улучшенное)");
                Console.WriteLine("0 - Выход");
                Console.Write("\nВаш выбор: ");

                string choice = Console.ReadLine();

                if (choice == "0") break;

                switch (choice)
                {
                    case "1": colorMode = 1; isVideo = true; break;
                    case "2": colorMode = 0; isVideo = true; break;
                    case "3": colorMode = 2; isVideo = true; break;
                    case "4": colorMode = 1; isVideo = false; break;
                    case "5": colorMode = 0; isVideo = false; break;
                    case "6": colorMode = 2; isVideo = false; break;
                    default:
                        Console.WriteLine("Неверный выбор!");
                        Thread.Sleep(1000);
                        continue;
                }

                Console.Write("\nВведите путь к файлу (или перетащите): ");
                string path = Console.ReadLine();

                if (!string.IsNullOrEmpty(path))
                {
                    path = path.Trim('"', ' ', '\'', '[', ']', '(', ')');
                }

                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Console.WriteLine("Файл не найден!");
                    Thread.Sleep(1500);
                    continue;
                }

                if (isVideo)
                    PlayVideo(path);
                else
                    ShowPhoto(path);

                CleanupTemp();
            }
        }

        static void MaximizeConsole()
        {
            try
            {
                int width = Console.LargestWindowWidth;
                int height = Console.LargestWindowHeight;
                Console.SetWindowSize(width, height);
                Console.SetBufferSize(width, height);
            }
            catch
            {
                try { Console.SetWindowSize(160, 80); Console.SetBufferSize(160, 80); } catch { }
            }
        }

        static void CleanupTemp()
        {
            try
            {
                string tempPath = Path.GetTempPath();
                string[] tempFiles = Directory.GetFiles(tempPath, "*_temp_*.wav");
                foreach (string file in tempFiles)
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < DateTime.Now.AddHours(-1))
                            File.Delete(file);
                    }
                    catch { }
                }
            }
            catch { }
        }

        static void ShowPhoto(string path)
        {
            Console.CursorVisible = false;
            Console.Clear();

            try
            {
                using (Bitmap img = new Bitmap(path))
                {
                    int consoleW = Console.WindowWidth;
                    int consoleH = Console.WindowHeight - 3;
                    int targetW = consoleW;
                    int targetH = (int)(consoleH / AspectRatio);

                    Console.WriteLine($"Изображение: {img.Width}x{img.Height}");
                    Console.WriteLine($"Режим: {(colorMode == 0 ? "Серый" : colorMode == 1 ? "Цветной" : "Цветной улучшенный")}");
                    Console.WriteLine("Нажмите любую клавишу для отображения. ESC для выхода.\n");

                    ConsoleKeyInfo key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Escape) return;

                    Console.Clear();

                    Bitmap processedImg = (colorMode == 2) ? EnhanceImage(img) : img;
                    CHAR_INFO[] buffer = ConvertToBuffer(processedImg, targetW, targetH, consoleW, consoleH);
                    if (processedImg != img) processedImg.Dispose();

                    WriteBuffer(buffer, consoleW, consoleH);

                    Console.SetCursorPosition(0, consoleH);
                    Console.WriteLine("ESC - выход | M - меню");

                    while (true)
                    {
                        key = Console.ReadKey(true);
                        if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.M) break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Clear();
                Console.WriteLine($"Ошибка загрузки изображения: {ex.Message}");
                Thread.Sleep(2000);
            }
            finally
            {
                Console.CursorVisible = true;
            }
        }

        static Bitmap EnhanceImage(Bitmap original)
        {
            int width = original.Width;
            int height = original.Height;
            Bitmap enhanced = new Bitmap(width, height, PixelFormat.Format24bppRgb);

            BitmapData srcData = original.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, original.PixelFormat);
            BitmapData dstData = enhanced.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            int srcDepth = Image.GetPixelFormatSize(original.PixelFormat) / 8;
            int srcBytes = Math.Abs(srcData.Stride) * height;
            int dstBytes = Math.Abs(dstData.Stride) * height;

            byte[] srcRgb = new byte[srcBytes];
            byte[] dstRgb = new byte[dstBytes];

            Marshal.Copy(srcData.Scan0, srcRgb, 0, srcBytes);

            for (int y = 0; y < height; y++)
            {
                int srcLine = y * srcData.Stride;
                int dstLine = y * dstData.Stride;

                for (int x = 0; x < width; x++)
                {
                    int srcIdx = srcLine + x * srcDepth;
                    int dstIdx = dstLine + x * 3;

                    byte b = srcRgb[srcIdx];
                    byte g = srcRgb[srcIdx + 1];
                    byte r = srcRgb[srcIdx + 2];

                    int newR = Math.Min(255, (r * 115 / 100) + 13);
                    int newG = Math.Min(255, (g * 115 / 100) + 13);
                    int newB = Math.Min(255, (b * 115 / 100) + 13);

                    double gamma = 0.85;
                    newR = (int)(255 * Math.Pow(newR / 255.0, gamma));
                    newG = (int)(255 * Math.Pow(newG / 255.0, gamma));
                    newB = (int)(255 * Math.Pow(newB / 255.0, gamma));

                    dstRgb[dstIdx] = (byte)newB;
                    dstRgb[dstIdx + 1] = (byte)newG;
                    dstRgb[dstIdx + 2] = (byte)newR;
                }
            }

            Marshal.Copy(dstRgb, 0, dstData.Scan0, dstBytes);

            original.UnlockBits(srcData);
            enhanced.UnlockBits(dstData);

            return enhanced;
        }

        static void PlayVideo(string path)
        {
            Console.CursorVisible = false;
            using (VideoFileReader reader = new VideoFileReader())
            {
                try
                {
                    reader.Open(path);
                    Console.Clear();
                    Console.WriteLine($"Видео: {reader.Width}x{reader.Height}, FPS: {reader.FrameRate.Value:F1}");
                    Console.WriteLine($"Режим: {(colorMode == 0 ? "Серый" : colorMode == 1 ? "Цветной" : "Цветной улучшенный")}");
                    Console.WriteLine("Нажмите любую клавишу для начала. ESC для выхода.");

                    ConsoleKeyInfo key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Escape) return;

                    Console.Clear();

                    double fps = reader.FrameRate.Value;
                    int frameDelay = Math.Max(1, (int)(1000.0 / fps));
                    Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

                    StartAudio(path);
                    Thread.Sleep(500);

                    long frameNum = 0;
                    int consoleW = Console.WindowWidth;
                    int consoleH = Console.WindowHeight;
                    int targetW = consoleW;
                    int targetH = (int)(consoleH / AspectRatio);

                    while (true)
                    {
                        long startTick = Environment.TickCount;

                        using (Bitmap frame = reader.ReadVideoFrame())
                        {
                            if (frame == null) break;

                            Bitmap processedFrame = (colorMode == 2) ? EnhanceImage(frame) : frame;
                            CHAR_INFO[] buffer = ConvertToBuffer(processedFrame, targetW, targetH, consoleW, consoleH);
                            if (processedFrame != frame) processedFrame.Dispose();

                            WriteBuffer(buffer, consoleW, consoleH);
                            frameNum++;

                            SyncWithAudio(frameNum, fps, reader, ref frameNum);
                        }

                        int elapsed = (int)(Environment.TickCount - startTick);
                        int sleep = frameDelay - elapsed;
                        if (sleep > 0) Thread.Sleep(sleep);

                        if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                            break;
                    }

                    StopAudio();
                    Console.Clear();
                    Console.WriteLine("\n\nВоспроизведение завершено! Нажмите любую клавишу...");
                    Console.ReadKey(true);
                }
                catch (Exception ex)
                {
                    Console.Clear();
                    Console.WriteLine($"Ошибка: {ex.Message}\nStack trace: {ex.StackTrace}");
                    StopAudio();
                    Thread.Sleep(3000);
                }
                finally
                {
                    Console.CursorVisible = true;
                }
            }
        }

        static void SyncWithAudio(long frameNum, double fps, VideoFileReader reader, ref long frameNumber)
        {
            lock (audioLock)
            {
                if (audioReader == null || waveOut == null || waveOut.PlaybackState != PlaybackState.Playing) return;

                double videoTime = frameNum / fps;
                double audioTime = audioReader.CurrentTime.TotalSeconds;
                double diff = Math.Abs(audioTime - videoTime);
                double tolerance = 3.0 / fps;

                if (diff > tolerance && diff < 5.0)
                {
                    if (videoTime < audioTime - tolerance)
                    {
                        int skip = Math.Min(30, (int)((audioTime - videoTime) * fps));
                        Console.Title = $"[Пропуск {skip} кадров]";
                        for (int i = 0; i < skip; i++)
                        {
                            using (Bitmap skipFrame = reader.ReadVideoFrame())
                            {
                                if (skipFrame == null) break;
                            }
                            frameNumber++;
                        }
                    }
                    else if (videoTime > audioTime + tolerance)
                    {
                        int delay = Math.Min(500, (int)((videoTime - audioTime) * 1000));
                        Thread.Sleep(delay);
                    }
                }

                Console.Title = $"Frame: {frameNum} | Video: {videoTime:F2}s | Audio: {audioTime:F2}s | Diff: {(videoTime - audioTime):F3}s";
            }
        }

        static void StartAudio(string videoPath)
        {
            try
            {
                Console.Write("[Аудио] Извлечение...");
                tempAudioPath = Path.Combine(Path.GetTempPath(),
                    Path.GetFileNameWithoutExtension(videoPath) + "_temp_" + Guid.NewGuid().ToString().Substring(0, 8) + ".wav");

                using (MediaFoundationReader reader = new MediaFoundationReader(videoPath))
                {
                    MediaFoundationResampler resampler = new MediaFoundationResampler(reader, new WaveFormat(44100, 16, 2));
                    using (WaveFileWriter writer = new WaveFileWriter(tempAudioPath, resampler.WaveFormat))
                    {
                        byte[] buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
                        int read;
                        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                            writer.Write(buffer, 0, read);
                    }
                }

                Console.WriteLine(" Готово.");

                lock (audioLock)
                {
                    waveOut = new WaveOutEvent();
                    ((WaveOutEvent)waveOut).DesiredLatency = 300;
                    ((WaveOutEvent)waveOut).NumberOfBuffers = 3;

                    audioReader = new AudioFileReader(tempAudioPath);
                    audioReader.Volume = 0.5f;

                    waveOut.PlaybackStopped += delegate (object s, StoppedEventArgs e)
                    {
                        if (e.Exception != null)
                            Console.WriteLine($"\n[АУДИО ОШИБКА] {e.Exception.Message}");
                    };

                    waveOut.Init(audioReader);
                    waveOut.Play();
                    Console.WriteLine("[Аудио] Воспроизведение начато.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[Аудио Ошибка] {ex.Message}");
                tempAudioPath = null;
            }
        }

        static void StopAudio()
        {
            lock (audioLock)
            {
                if (waveOut != null)
                {
                    try { waveOut.Stop(); waveOut.Dispose(); } catch { }
                    waveOut = null;
                }
                if (audioReader != null)
                {
                    try { audioReader.Dispose(); } catch { }
                    audioReader = null;
                }
            }

            Thread.Sleep(100);

            if (tempAudioPath != null && File.Exists(tempAudioPath))
            {
                try
                {
                    File.Delete(tempAudioPath);
                    Console.WriteLine("[Аудио] Временный файл удален.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Аудио] Ошибка удаления: {ex.Message}");
                }
                finally
                {
                    tempAudioPath = null;
                }
            }
        }

        static void WriteBuffer(CHAR_INFO[] buffer, int w, int h)
        {
            IntPtr handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (handle == IntPtr.Zero) return;

            COORD bufSize = new COORD { X = (short)w, Y = (short)h };
            COORD bufCoord = new COORD { X = 0, Y = 0 };
            SMALL_RECT region = new SMALL_RECT { Left = 0, Top = 0, Right = (short)(w - 1), Bottom = (short)(h - 1) };

            WriteConsoleOutput(handle, buffer, bufSize, bufCoord, ref region);
        }

        static CHAR_INFO[] ConvertToBuffer(Bitmap img, int w, int h, int consoleW, int consoleH)
        {
            int bufferSize = consoleW * consoleH;
            CHAR_INFO[] buffer = new CHAR_INFO[bufferSize];

            using (Bitmap resized = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(resized))
                {
                    g.CompositingQuality = CompositingQuality.HighSpeed;
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.SmoothingMode = SmoothingMode.HighSpeed;
                    g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                    g.DrawImage(img, 0, 0, w, h);
                }

                BitmapData data = resized.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                int stride = data.Stride;
                byte[] rgb = new byte[Math.Abs(stride) * h];
                Marshal.Copy(data.Scan0, rgb, 0, rgb.Length);
                resized.UnlockBits(data);

                int maxChar = asciiChars.Length - 1;
                int bufIdx = 0;

                for (int y = 0; y < h; y++)
                {
                    int lineStart = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int idx = lineStart + x * 3;
                        byte b = rgb[idx];
                        byte g = rgb[idx + 1];
                        byte r = rgb[idx + 2];

                        int brightness = (r * 77 + g * 150 + b * 29) >> 8;

                        buffer[bufIdx].UnicodeChar = asciiChars[brightness * maxChar / 255];

                        if (colorMode == 0)
                        {
                            buffer[bufIdx].Attributes = (short)7;
                        }
                        else if (colorMode == 1)
                        {
                            buffer[bufIdx].Attributes = GetConsoleColor(r, g, b);
                        }
                        else
                        {
                            buffer[bufIdx].Attributes = GetEnhancedConsoleColor(r, g, b);
                        }

                        bufIdx++;
                    }

                    for (int x = w; x < consoleW; x++, bufIdx++)
                    {
                        buffer[bufIdx].UnicodeChar = ' ';
                        buffer[bufIdx].Attributes = 0;
                    }
                }

                for (; bufIdx < bufferSize; bufIdx++)
                {
                    buffer[bufIdx].UnicodeChar = ' ';
                    buffer[bufIdx].Attributes = 0;
                }
            }

            return buffer;
        }

        static short GetConsoleColor(byte r, byte g, byte b)
        {
            bool rHigh = r > 127;
            bool gHigh = g > 127;
            bool bHigh = b > 127;

            int gray = (r + g + b) / 3;

            if (Math.Abs(r - g) < 30 && Math.Abs(g - b) < 30 && Math.Abs(r - b) < 30)
            {
                if (gray < 64) return 0x00;
                if (gray < 128) return 0x08;
                if (gray < 192) return 0x07;
                return 0x0F;
            }

            short color = 0;
            if (rHigh) color |= 0x04;
            if (gHigh) color |= 0x02;
            if (bHigh) color |= 0x01;

            if (r > 192 || g > 192 || b > 192)
                color |= 0x08;

            return color;
        }

        static short GetEnhancedConsoleColor(byte r, byte g, byte b)
        {
            int brightness = (r + g + b) / 3;

            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            int delta = max - min;

            if (delta < 30)
            {
                if (brightness < 32) return 0x00;
                if (brightness < 80) return 0x08;
                if (brightness < 160) return 0x07;
                return 0x0F;
            }

            short color = 0;
            int secondMax = 0;

            if (r >= g && r >= b)
            {
                secondMax = Math.Max(g, b);
                if (r > secondMax + 40 && secondMax < 120)
                {
                    color = 0x04;
                    if (r > 180 || brightness > 140) color |= 0x08;
                    return color;
                }
            }
            else if (g >= r && g >= b)
            {
                secondMax = Math.Max(r, b);
                if (g > secondMax + 35)
                {
                    color = 0x02;
                    if (g > 180 || brightness > 140) color |= 0x08;
                    return color;
                }
            }
            else if (b >= r && b >= g)
            {
                secondMax = Math.Max(r, g);
                if (b > secondMax + 35)
                {
                    color = 0x01;
                    if (b > 180 || brightness > 140) color |= 0x08;
                    return color;
                }
            }

            if (r > 110 && g > 110 && b < 90)
            {
                color = 0x06;
                if ((r > 180 || g > 180) && brightness > 130) color |= 0x08;
                return color;
            }

            if (r > 110 && b > 110 && g < 90)
            {
                color = 0x05;
                if ((r > 180 || b > 180) && brightness > 130) color |= 0x08;
                return color;
            }

            if (g > 110 && b > 110 && r < 90)
            {
                color = 0x03;
                if ((g > 180 || b > 180) && brightness > 130) color |= 0x08;
                return color;
            }

            bool rHigh = r > 110;
            bool gHigh = g > 110;
            bool bHigh = b > 110;

            if (rHigh) color |= 0x04;
            if (gHigh) color |= 0x02;
            if (bHigh) color |= 0x01;

            if (brightness > 160) color |= 0x08;

            return color;
        }
    }
}