using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.IO;
using System.Threading.Tasks;
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
        static extern bool WriteConsoleOutput(
            IntPtr hConsoleOutput,
            [In] CHAR_INFO[] lpBuffer,
            COORD dwBufferSize,
            COORD dwBufferCoord,
            ref SMALL_RECT lpWriteRegion
        );

        [StructLayout(LayoutKind.Sequential)]
        struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct CHAR_INFO
        {
            [FieldOffset(0)] public char UnicodeChar;
            [FieldOffset(0)] public short AsciiChar;
            [FieldOffset(2)] public short Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SMALL_RECT
        {
            public short Left;
            public short Top;
            public short Right;
            public short Bottom;
        }

        private static IWavePlayer waveOut;
        private static AudioFileReader audioFileReader;
        private static CancellationTokenSource cts = new CancellationTokenSource();
        private static string tempAudioPath = null;
        private static object audioLock = new object();

        private static readonly char[] asciiChars = { ' ', '.', ':', '-', '=', '+', '*', '#', '%', 'S', '@' };
        private const double AspectRatioCompensation = 2.2;
        private const int MinConsoleWidth = 80;

        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            try
            {
                Console.SetWindowSize(160, 80);
                Console.SetBufferSize(160, 80);
            }
            catch { }

            Console.WriteLine("ASCII Video Player (Исправленная версия)");
            Console.WriteLine("=======================================================");
            Console.Write("Введите путь к видео файлу: ");

            string videoPath = args.Length > 0 ? args[0] : Console.ReadLine();

            if (string.IsNullOrEmpty(videoPath) || !System.IO.File.Exists(videoPath))
            {
                Console.WriteLine("Файл не найден!");
                Console.ReadKey();
                return;
            }

            PlayVideo(videoPath);
        }

        static void PlayVideo(string path)
        {
            Console.CursorVisible = false;

            using (var reader = new VideoFileReader())
            {
                try
                {
                    reader.Open(path);

                    Console.Clear();
                    Console.WriteLine($"Видео: {reader.Width}x{reader.Height}, FPS: {reader.FrameRate.Value:F1}");
                    Console.WriteLine("Нажмите любую клавишу для начала. ESC для выхода.");
                    Console.ReadKey(true);

                    Console.Clear();

                    double fps = reader.FrameRate.Value;
                    int frameDelay = (int)(1000.0 / fps);
                    if (frameDelay < 1) frameDelay = 1;

                    Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

                    StartAudio(path);

                    Thread.Sleep(500);

                    long frameNumber = 0;
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                    while (true)
                    {
                        int consoleWidth = Math.Max(Console.WindowWidth, MinConsoleWidth);
                        int consoleHeight = Console.WindowHeight;

                        int targetWidth = consoleWidth;
                        int targetHeight = (int)(consoleHeight / AspectRatioCompensation);

                        long frameStartTime = Environment.TickCount;

                        using (Bitmap frame = reader.ReadVideoFrame())
                        {
                            if (frame == null)
                                break;

                            string asciiContent = ConvertToASCII_Optimized(frame, targetWidth, targetHeight);
                            string fullFrame = PadFrame(asciiContent, consoleWidth, consoleHeight);
                            WriteAsciiFrameToBuffer(fullFrame, consoleWidth, consoleHeight);

                            frameNumber++;

                            double expectedVideoTime = frameNumber / fps;

                            lock (audioLock)
                            {
                                if (audioFileReader != null && waveOut != null && waveOut.PlaybackState == PlaybackState.Playing)
                                {
                                    double audioTime = audioFileReader.CurrentTime.TotalSeconds;
                                    double diff = Math.Abs(audioTime - expectedVideoTime);

                                    double tolerance = 3.0 / fps;

                                    if (diff > tolerance && diff < 5.0) 
                                    {
                                        if (expectedVideoTime < audioTime - tolerance)
                                        {
                                            int framesToSkip = (int)((audioTime - expectedVideoTime) * fps);
                                            if (framesToSkip > 0 && framesToSkip < 30) 
                                            {
                                                Console.Title = $"[Пропуск {framesToSkip} кадров для синхронизации]";
                                                for (int i = 0; i < framesToSkip; i++)
                                                {
                                                    using (var skipFrame = reader.ReadVideoFrame())
                                                    {
                                                        if (skipFrame == null) break;
                                                    }
                                                    frameNumber++;
                                                }
                                            }
                                        }
                                        else if (expectedVideoTime > audioTime + tolerance)
                                        {
                                            int extraDelay = (int)((expectedVideoTime - audioTime) * 1000);
                                            if (extraDelay > 0 && extraDelay < 500) 
                                            {
                                                Thread.Sleep(extraDelay);
                                            }
                                        }
                                    }

                                    Console.Title = $"Frame: {frameNumber} | Video: {expectedVideoTime:F2}s | Audio: {audioTime:F2}s | Diff: {(expectedVideoTime - audioTime):F3}s";
                                }
                            }
                        }

                        int elapsed = (int)(Environment.TickCount - frameStartTime);
                        int sleepTime = frameDelay - elapsed;

                        if (sleepTime > 0)
                        {
                            Thread.Sleep(sleepTime);
                        }

                        if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                            break;
                    }

                    StopAudio();

                    Console.Clear();
                    Console.WriteLine("\n\nВоспроизведение завершено!");
                }
                catch (Exception ex)
                {
                    Console.Clear();
                    Console.WriteLine($"Ошибка: {ex.Message}");
                    Console.WriteLine($"Stack trace: {ex.StackTrace}");
                    StopAudio();
                }
                finally
                {
                    Console.CursorVisible = true;
                }
            }

            Console.WriteLine("Нажмите любую клавишу для выхода...");
            Console.ReadKey(true);
        }

        /// <summary>
        /// Инициализирует и запускает воспроизведение аудиодорожки.
        /// </summary>
        static void StartAudio(string videoPath)
        {
            try
            {
                Console.Write("[Аудио] Извлечение аудиодорожки...");

                tempAudioPath = Path.Combine(Path.GetTempPath(),
                    Path.GetFileNameWithoutExtension(videoPath) + "_temp_" +
                    Guid.NewGuid().ToString().Substring(0, 8) + ".wav");

                using (var reader = new MediaFoundationReader(videoPath))
                {
                    var resampler = new MediaFoundationResampler(reader, new WaveFormat(44100, 16, 2));

                    using (var writer = new WaveFileWriter(tempAudioPath, resampler.WaveFormat))
                    {
                        byte[] buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
                        int read;
                        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            writer.Write(buffer, 0, read);
                        }
                    }
                }

                Console.WriteLine(" Готово.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[Аудио Ошибка] Не удалось извлечь звук: {ex.Message}");
                tempAudioPath = null;
                return;
            }

            try
            {
                lock (audioLock)
                {
                    waveOut = new WaveOutEvent
                    {
                        DesiredLatency = 300, 
                        NumberOfBuffers = 3   
                    };

                    audioFileReader = new AudioFileReader(tempAudioPath)
                    {
                        Volume = 0.5f
                    };

                    waveOut.PlaybackStopped += (sender, e) =>
                    {
                        if (e.Exception != null)
                        {
                            Console.WriteLine($"\n[АУДИО ОШИБКА] {e.Exception.Message}");
                        }
                    };

                    waveOut.Init(audioFileReader);
                    waveOut.Play();

                    Console.WriteLine("[Аудио] Воспроизведение начато.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[Аудио Ошибка] Не удалось запустить звук: {ex.Message}");
                StopAudio();
            }
        }

        /// <summary>
        /// Останавливает и очищает ресурсы NAudio, удаляет временный файл.
        /// </summary>
        static void StopAudio()
        {
            if (cts != null)
            {
                cts.Cancel();
            }

            lock (audioLock)
            {
                if (waveOut != null)
                {
                    try
                    {
                        waveOut.Stop();
                        waveOut.Dispose();
                    }
                    catch { }
                    waveOut = null;
                }

                if (audioFileReader != null)
                {
                    try
                    {
                        audioFileReader.Dispose();
                    }
                    catch { }
                    audioFileReader = null;
                }
            }

            Thread.Sleep(100);

            if (tempAudioPath != null && File.Exists(tempAudioPath))
            {
                try
                {
                    File.Delete(tempAudioPath);
                    Console.WriteLine($"[Аудио] Временный файл удален.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Аудио] Не удалось удалить временный файл: {ex.Message}");
                }
                finally
                {
                    tempAudioPath = null;
                }
            }
        }

        static void WriteAsciiFrameToBuffer(string asciiFrame, int width, int height)
        {
            IntPtr hConsole = GetStdHandle(STD_OUTPUT_HANDLE);
            if (hConsole == IntPtr.Zero) return;

            CHAR_INFO[] buffer = new CHAR_INFO[width * height];
            int bufferIndex = 0;

            for (int i = 0; i < asciiFrame.Length && bufferIndex < buffer.Length; i++)
            {
                if (asciiFrame[i] == '\n') continue;

                buffer[bufferIndex].UnicodeChar = asciiFrame[i];
                buffer[bufferIndex].Attributes = 7;
                bufferIndex++;
            }

            COORD bufferSize = new COORD { X = (short)width, Y = (short)height };
            COORD bufferCoord = new COORD { X = 0, Y = 0 };
            SMALL_RECT writeRegion = new SMALL_RECT
            {
                Left = 0,
                Top = 0,
                Right = (short)(width - 1),
                Bottom = (short)(height - 1)
            };

            WriteConsoleOutput(hConsole, buffer, bufferSize, bufferCoord, ref writeRegion);
        }

        static string ConvertToASCII_Optimized(Bitmap image, int width, int height)
        {
            if (width <= 0 || height <= 0) return new string('\n', 1);

            using (Bitmap resized = new Bitmap(width, height))
            {
                using (Graphics g = Graphics.FromImage(resized))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(image, 0, 0, width, height);
                }

                StringBuilder sb = new StringBuilder(width * height);

                Rectangle rect = new Rectangle(0, 0, resized.Width, resized.Height);
                BitmapData bmpData = resized.LockBits(rect, ImageLockMode.ReadOnly, resized.PixelFormat);

                IntPtr ptr = bmpData.Scan0;
                int bytes = Math.Abs(bmpData.Stride) * resized.Height;
                byte[] rgbValues = new byte[bytes];
                Marshal.Copy(ptr, rgbValues, 0, bytes);

                int depth = Image.GetPixelFormatSize(resized.PixelFormat) / 8;

                for (int y = 0; y < resized.Height; y++)
                {
                    int lineStart = y * bmpData.Stride;
                    for (int x = 0; x < resized.Width; x++)
                    {
                        int pixelIndex = lineStart + x * depth;

                        byte b = rgbValues[pixelIndex];
                        byte g = rgbValues[pixelIndex + 1];
                        byte r = rgbValues[pixelIndex + 2];

                        int brightness = (int)(r * 0.299 + g * 0.587 + b * 0.114);
                        int charIndex = (brightness * (asciiChars.Length - 1)) / 255;

                        sb.Append(asciiChars[charIndex]);
                    }
                    sb.Append('\n');
                }

                resized.UnlockBits(bmpData);

                return sb.ToString();
            }
        }

        static string PadFrame(string asciiContent, int consoleWidth, int consoleHeight)
        {
            StringBuilder paddedContent = new StringBuilder(consoleWidth * consoleHeight);
            StringReader reader = new StringReader(asciiContent);
            int linesRead = 0;

            string line;
            while ((line = reader.ReadLine()) != null && linesRead < consoleHeight)
            {
                int lineLength = line.Length;

                paddedContent.Append(line);

                if (lineLength < consoleWidth)
                {
                    paddedContent.Append(' ', consoleWidth - lineLength);
                }

                paddedContent.Append('\n');
                linesRead++;
            }

            int paddingLines = consoleHeight - linesRead;
            if (paddingLines > 0)
            {
                string emptyLineWithPadding = new string(' ', consoleWidth) + '\n';
                for (int i = 0; i < paddingLines; i++)
                {
                    paddedContent.Append(emptyLineWithPadding);
                }
            }

            return paddedContent.ToString();
        }
    }
}